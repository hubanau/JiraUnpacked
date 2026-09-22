using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Auth;

public sealed class RegistrationEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public RegistrationEndpointTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> SeedInviteAsync(bool expired = false, bool used = false)
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
            ExpiresAt = expired ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(7),
            UsedByUserId = used ? admin.Id : null,
            UsedAt = used ? DateTime.UtcNow : null
        });
        await db.SaveChangesAsync();
        return code;
    }

    private static object RegisterBody(string inviteCode, string email, string username, string password) =>
        new { inviteCode, email, username, password };

    [Fact]
    public async Task Register_WithValidInvite_CreatesPendingUser_ConsumesInvite_AndSendsCode()
    {
        var code = await SeedInviteAsync();
        var unique = Guid.NewGuid().ToString("N");
        var email = $"newuser-{unique}@example.com";
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, email, $"newuser-{unique[..20]}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        Assert.Equal(UserStatus.PendingVerification, user.Status);
        Assert.Equal(UserRole.EndUser, user.Role);

        var invite = await db.InviteCodes.SingleAsync(c => c.Code == code);
        Assert.Equal(user.Id, invite.UsedByUserId);

        var sender = _factory.Services.GetRequiredService<CapturingEmailSender>();
        Assert.False(string.IsNullOrEmpty(sender.LastCodeFor(email)));
    }

    [Fact]
    public async Task Register_NormalizesEmail()
    {
        var code = await SeedInviteAsync();
        var unique = Guid.NewGuid().ToString("N");
        var mixedCase = $"NewUser-{unique}@Example.COM";
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, mixedCase, $"newuser-{unique[..20]}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Users.AnyAsync(u => u.Email == $"newuser-{unique}@example.com"));
    }

    [Fact]
    public async Task Register_WithInvalidInvite_Returns422_AndCreatesNoUser()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"newuser-{unique}@example.com";
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody("does-not-exist", email, $"newuser-{unique[..20]}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task Register_WithExpiredInvite_Returns422()
    {
        var code = await SeedInviteAsync(expired: true);
        var unique = Guid.NewGuid().ToString("N");
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, $"newuser-{unique}@example.com", $"newuser-{unique[..20]}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithUsedInvite_Returns422()
    {
        var code = await SeedInviteAsync(used: true);
        var unique = Guid.NewGuid().ToString("N");
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, $"newuser-{unique}@example.com", $"newuser-{unique[..20]}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithWeakPassword_Returns400()
    {
        var code = await SeedInviteAsync();
        var unique = Guid.NewGuid().ToString("N");
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, $"newuser-{unique}@example.com", $"newuser-{unique[..20]}", "weak"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_Returns409()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"dupe-{unique}@example.com";

        var code1 = await SeedInviteAsync();
        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code1, email, $"first-{unique[..20]}", "Correct-Horse-9!"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var code2 = await SeedInviteAsync();
        var second = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code2, email.ToUpperInvariant(), $"second-{unique[..20]}", "Correct-Horse-9!"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Register_WithDuplicateUsername_Returns409()
    {
        var unique = Guid.NewGuid().ToString("N");
        var username = $"dupe-{unique[..20]}";

        var code1 = await SeedInviteAsync();
        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code1, $"first-{unique}@example.com", username, "Correct-Horse-9!"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var code2 = await SeedInviteAsync();
        var second = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code2, $"second-{unique}@example.com", username, "Correct-Horse-9!"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Alice <alice@example.com>")]
    [InlineData("alice@")]
    [InlineData("@example.com")]
    [InlineData("two@@example.com")]
    public async Task Register_WithInvalidEmail_Returns400_AndLeavesTheInviteUnused(string email)
    {
        var code = await SeedInviteAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, email, $"newuser-{Guid.NewGuid().ToString("N")[..20]}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("valid email", await response.Content.ReadAsStringAsync());
        await AssertInviteUnusedAsync(code);
    }

    [Fact]
    public async Task Register_WithOverlongEmail_Returns400()
    {
        var code = await SeedInviteAsync();
        var email = new string('a', 243) + "@example.com"; // 255 characters: one over the limit
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, email, $"newuser-{Guid.NewGuid().ToString("N")[..20]}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertInviteUnusedAsync(code);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("alice@example.com")]
    [InlineData("has space")]
    [InlineData("bang!")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33 characters
    public async Task Register_WithInvalidUsername_Returns400_AndLeavesTheInviteUnused(string username)
    {
        var code = await SeedInviteAsync();
        var email = $"newuser-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, email, username, "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Username is not valid", await response.Content.ReadAsStringAsync());
        await AssertInviteUnusedAsync(code);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email));
    }

    private async Task AssertInviteUnusedAsync(string code)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invite = await db.InviteCodes.SingleAsync(c => c.Code == code);
        Assert.Null(invite.UsedByUserId);
    }
}
