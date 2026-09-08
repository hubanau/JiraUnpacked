// tests/JiraUnpacked.Tests/Auth/PasswordResetEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Auth;

/// <summary>
/// Password reset. A guessed or leaked reset challenge is full account takeover, so most of
/// what is asserted here is about what the endpoints REFUSE to reveal or allow.
/// </summary>
public sealed class PasswordResetEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public PasswordResetEndpointTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string OldPassword = "Correct-Horse-9!";
    private const string NewPassword = "Brand-New-Pass-7!";

    private async Task<string> SeedUserAsync(UserStatus status = UserStatus.Active)
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"reset-{unique}@example.com";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Email = email, Username = $"reset-{unique}",
            PasswordHash = hasher.Hash(OldPassword), Role = UserRole.EndUser,
            Status = status, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return email;
    }

    private CapturingEmailSender Mail => _factory.Services.GetRequiredService<CapturingEmailSender>();

    private Task<HttpResponseMessage> ForgotAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/auth/password/forgot", new { email });

    private Task<HttpResponseMessage> ResetAsync(HttpClient client, string token, string newPassword) =>
        client.PostAsJsonAsync("/api/auth/password/reset", new { token, newPassword });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { emailOrUsername = email, password });

    [Fact]
    public async Task The_request_endpoint_is_not_an_account_existence_oracle()
    {
        var client = _factory.CreateClient();

        var active = await SeedUserAsync();
        var blocked = await SeedUserAsync(UserStatus.Blocked);
        var pending = await SeedUserAsync(UserStatus.PendingVerification);
        var unknown = $"nobody-{Guid.NewGuid():N}@example.com";

        var responses = new List<(string Label, HttpResponseMessage Response)>();
        foreach (var (label, email) in new[]
                 { ("active", active), ("blocked", blocked), ("pending", pending), ("unknown", unknown) })
        {
            responses.Add((label, await ForgotAsync(client, email)));
        }

        // Identical STATUS and identical BODY. A status-only assertion would miss a message
        // that says "no such account", which is the same leak wearing a different hat.
        var bodies = new List<string>();
        foreach (var (label, response) in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Accepted,
                $"{label} answered {(int)response.StatusCode}");
            bodies.Add(await response.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies.Distinct());

        // And only the active user actually received anything.
        Assert.NotNull(Mail.LastResetLinkFor(active));
        Assert.Null(Mail.LastResetLinkFor(blocked));
        Assert.Null(Mail.LastResetLinkFor(pending));
        Assert.Null(Mail.LastResetLinkFor(unknown));
    }

    [Fact]
    public async Task A_reset_replaces_the_password()
    {
        var client = _factory.CreateClient();
        var email = await SeedUserAsync();

        await ForgotAsync(client, email);
        var token = Mail.LastResetTokenFor(email)!;

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token, NewPassword)).StatusCode);

        // Both directions: the old password must stop working, not merely the new one start.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, OldPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task A_reset_kills_sessions_that_already_existed()
    {
        var client = CookieAuth.Client(_factory);
        var email = await SeedUserAsync();

        var login = await LoginAsync(client, email, OldPassword);
        var refreshCookie = CookieAuth.RefreshCookie(login)!;

        // Refresh ONCE to obtain a rotated, still-unconsumed token. Refreshing twice with the
        // same cookie would consume it, and the final assertion would then pass because the
        // token was already spent rather than because the reset revoked it — which is exactly
        // how this test read before a mutation check caught it.
        var refreshed = await CookieAuth.PostAsync(client, "/api/auth/refresh", refreshCookie);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var live = CookieAuth.RefreshCookie(refreshed)!;
        Assert.NotEqual(refreshCookie, live);

        await ForgotAsync(client, email);
        await ResetAsync(client, Mail.LastResetTokenFor(email)!, NewPassword);

        // The step most implementations skip. A reset is usually prompted by someone else
        // having got in — leaving their session refreshable defeats the whole exercise.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await CookieAuth.PostAsync(client, "/api/auth/refresh", live)).StatusCode);
    }

    [Fact]
    public async Task A_token_works_once()
    {
        var client = _factory.CreateClient();
        var email = await SeedUserAsync();

        await ForgotAsync(client, email);
        var token = Mail.LastResetTokenFor(email)!;

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(client, token, "Another-Pass-11!")).StatusCode);

        // The second attempt must not have taken effect either.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Requesting_again_invalidates_the_previous_link()
    {
        var client = _factory.CreateClient();
        var email = await SeedUserAsync();

        await ForgotAsync(client, email);
        var first = Mail.LastResetTokenFor(email)!;

        await ForgotAsync(client, email);
        var second = Mail.LastResetTokenFor(email)!;
        Assert.NotEqual(first, second);

        // Otherwise an older email sitting in a mailbox stays a live key indefinitely.
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(client, first, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, second, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var client = _factory.CreateClient();
        var email = await SeedUserAsync();

        await ForgotAsync(client, email);
        var token = Mail.LastResetTokenFor(email)!;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hash = PasswordResetToken.HashToken(token);
            await db.PasswordResetTokens.Where(t => t.TokenHash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(client, token, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, OldPassword)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_is_refused_and_says_nothing_specific()
    {
        var client = _factory.CreateClient();

        var response = await ResetAsync(client, "not-a-real-token", NewPassword);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Unknown, expired and already-used share one message on purpose: each is a fact about
        // somebody else's account.
        Assert.Contains("invalid or has expired", body);
    }

    [Fact]
    public async Task A_weak_new_password_is_refused_and_the_old_one_still_works()
    {
        var client = _factory.CreateClient();
        var email = await SeedUserAsync();

        await ForgotAsync(client, email);
        var token = Mail.LastResetTokenFor(email)!;

        var response = await ResetAsync(client, token, "short");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("strength policy", await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, OldPassword)).StatusCode);

        // The token survives a fumbled password, so the user need not request a second email.
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task The_token_is_never_in_a_response_body_and_is_stored_only_as_a_hash()
    {
        var client = _factory.CreateClient();
        var email = await SeedUserAsync();

        var forgot = await ForgotAsync(client, email);
        var body = await forgot.Content.ReadAsStringAsync();
        var token = Mail.LastResetTokenFor(email)!;

        // Asserted on the raw JSON: the response reaches an unauthenticated caller who may not
        // own the address, so the token must travel only by email.
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.PasswordResetTokens
            .Where(t => t.TokenHash == PasswordResetToken.HashToken(token))
            .Select(t => t.TokenHash)
            .FirstOrDefaultAsync();

        // Database read access must not be enough to mint a working link.
        Assert.NotNull(stored);
        Assert.NotEqual(token, stored);
        Assert.False(await db.PasswordResetTokens.AnyAsync(t => t.TokenHash == token));
    }

    [Fact]
    public async Task The_link_points_at_the_configured_base_url_not_a_request_header()
    {
        var client = _factory.CreateClient();
        var email = await SeedUserAsync();

        // A forged Host must not steer where the victim's link points — that is host-header
        // injection, and it turns a legitimate email into a token handover.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password/forgot")
        {
            Content = JsonContent.Create(new { email }),
        };
        request.Headers.Host = "attacker.example.com";
        await client.SendAsync(request);

        var link = Mail.LastResetLinkFor(email)!;
        Assert.DoesNotContain("attacker.example.com", link, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("http://localhost:8080/reset-password?token=", link, StringComparison.Ordinal);
    }
}
