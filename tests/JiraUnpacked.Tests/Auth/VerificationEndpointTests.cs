using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Auth;

public sealed class VerificationEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public VerificationEndpointTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> SeedInviteAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = $"admin-{unique}@example.com",
            Username = $"admin-{unique}",
            PasswordHash = "x",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(admin);
        var code = $"invite-{unique}";
        db.InviteCodes.Add(new InviteCode
        {
            Id = Guid.NewGuid(),
            Code = code,
            CreatedByAdminId = admin.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();
        return code;
    }

    private async Task<(string email, string password, string code)> RegisterAsync(HttpClient client)
    {
        var invite = await SeedInviteAsync();
        var unique = Guid.NewGuid().ToString("N");
        var email = $"newuser-{unique}@example.com";
        const string password = "Correct-Horse-9!";

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { inviteCode = invite, email, username = $"newuser-{unique[..20]}", password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var sender = _factory.Services.GetRequiredService<CapturingEmailSender>();
        var code = sender.LastCodeFor(email)!;
        Assert.False(string.IsNullOrEmpty(code));
        return (email, password, code);
    }

    [Fact]
    public async Task Verify_WithCorrectCode_ActivatesUser_AndAllowsLogin()
    {
        var client = _factory.CreateClient();
        var (email, password, code) = await RegisterAsync(client);

        var verify = await client.PostAsJsonAsync("/api/auth/verify", new { email, code });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == email);
            Assert.Equal(UserStatus.Active, user.Status);
        }

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Login_BeforeVerification_Fails()
    {
        var client = _factory.CreateClient();
        var (email, password, _) = await RegisterAsync(client);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Verify_WithWrongCode_Returns400()
    {
        var client = _factory.CreateClient();
        var (email, _, code) = await RegisterAsync(client);
        var wrong = code == "000000" ? "111111" : "000000";

        var verify = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = wrong });
        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
    }

    [Fact]
    public async Task Verify_AfterTooManyWrongAttempts_Returns429()
    {
        var client = _factory.CreateClient();
        var (email, _, code) = await RegisterAsync(client);
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var bad = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = wrong });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        var capped = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = wrong });
        Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
    }

    [Fact]
    public async Task Resend_IssuesNewCode_AndOldCodeNoLongerWorks()
    {
        var client = _factory.CreateClient();
        var (email, _, oldCode) = await RegisterAsync(client);
        await AgeCodesAsync(email, TimeSpan.FromSeconds(61)); // past the resend cooldown

        var resend = await client.PostAsJsonAsync("/api/auth/verify/resend", new { email });
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);

        var sender = _factory.Services.GetRequiredService<CapturingEmailSender>();
        var newCode = sender.LastCodeFor(email)!;
        Assert.NotEqual(oldCode, newCode);

        var oldTry = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = oldCode });
        Assert.Equal(HttpStatusCode.BadRequest, oldTry.StatusCode);

        var newTry = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = newCode });
        Assert.Equal(HttpStatusCode.OK, newTry.StatusCode);
    }

    [Fact]
    public async Task Resend_ForUnknownEmail_Returns202_Silently()
    {
        var client = _factory.CreateClient();
        var resend = await client.PostAsJsonAsync("/api/auth/verify/resend",
            new { email = $"nobody-{Guid.NewGuid():N}@example.com" });
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
    }

    [Fact]
    public async Task Resend_WithinTheCooldown_IsASilentNoOp()
    {
        var client = _factory.CreateClient();
        var (email, _, code) = await RegisterAsync(client);

        // Same 202 as always — the throttle must not reveal that the account is pending...
        var resend = await client.PostAsJsonAsync("/api/auth/verify/resend", new { email });
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);

        // ...but no new code was issued or mailed, and the original one still works.
        var sender = _factory.Services.GetRequiredService<CapturingEmailSender>();
        Assert.Equal(code, sender.LastCodeFor(email));
        Assert.Equal(1, await CodeCountAsync(email));

        var verify = await client.PostAsJsonAsync("/api/auth/verify", new { email, code });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    [Fact]
    public async Task Resend_AfterTheCooldown_IssuesOneCode_AndRestartsTheCooldown()
    {
        var client = _factory.CreateClient();
        var (email, _, oldCode) = await RegisterAsync(client);
        await AgeCodesAsync(email, TimeSpan.FromSeconds(61));

        await client.PostAsJsonAsync("/api/auth/verify/resend", new { email });
        var sender = _factory.Services.GetRequiredService<CapturingEmailSender>();
        var newCode = sender.LastCodeFor(email)!;
        Assert.NotEqual(oldCode, newCode);
        Assert.Equal(2, await CodeCountAsync(email));

        // The fresh code is itself the newest one now, so an immediate second resend is throttled.
        await client.PostAsJsonAsync("/api/auth/verify/resend", new { email });
        Assert.Equal(newCode, sender.LastCodeFor(email));
        Assert.Equal(2, await CodeCountAsync(email));
    }

    [Fact]
    public async Task Concurrent_wrong_codes_are_capped_at_the_attempt_limit()
    {
        var client = _factory.CreateClient();
        var (email, _, code) = await RegisterAsync(client);
        var wrong = code == "000000" ? "111111" : "000000";

        // A burst of simultaneous guesses. With a read-modify-write counter they all read the
        // same count and all get checked; the conditional increment lets exactly MaxAttempts
        // through and refuses the rest.
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            _factory.CreateClient().PostAsJsonAsync("/api/auth/verify", new { email, code = wrong })));

        Assert.Equal(EmailVerification.MaxAttempts, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        Assert.Equal(20 - EmailVerification.MaxAttempts,
            responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = (await db.Users.SingleAsync(u => u.Email == email)).Id;
        var attempts = await db.EmailVerifications.Where(v => v.UserId == userId).Select(v => v.Attempts).SingleAsync();
        Assert.Equal(EmailVerification.MaxAttempts, attempts);

        // The right code is refused too once the budget is spent.
        var right = await client.PostAsJsonAsync("/api/auth/verify", new { email, code });
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

    /// <summary>
    /// Moves the account's codes into the past. There is no CreatedAt column: issue time is
    /// derived from ExpiresAt, so shifting ExpiresAt is exactly "this code is older".
    /// </summary>
    private async Task AgeCodesAsync(string email, TimeSpan by)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = (await db.Users.SingleAsync(u => u.Email == email)).Id;
        await db.EmailVerifications
            .Where(v => v.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ExpiresAt, v => v.ExpiresAt - by));
    }

    private async Task<int> CodeCountAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = (await db.Users.SingleAsync(u => u.Email == email)).Id;
        return await db.EmailVerifications.CountAsync(v => v.UserId == userId);
    }
}
