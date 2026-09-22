using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Integration;

public sealed class AuthEndpointsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public AuthEndpointsTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc);

    private async Task<(string login, string password)> SeedUserAsync(UserStatus status = UserStatus.Active)
    {
        const string password = "Correct-Horse-9!";
        var unique = Guid.NewGuid().ToString("N");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"user-{unique}@example.com",
            Username = $"user-{unique}",
            PasswordHash = hasher.Hash(password),
            Role = UserRole.EndUser,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (user.Email, password);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokens()
    {
        var (login, password) = await SeedUserAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TokenResponseDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(CookieAuth.RefreshCookie(response)));
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var (login, _) = await SeedUserAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WhenBlocked_Returns401()
    {
        var (login, password) = await SeedUserAsync(UserStatus.Blocked);
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithValidToken_Returns200()
    {
        var (login, password) = await SeedUserAsync();
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });
        var tokens = await loginResponse.Content.ReadFromJsonAsync<TokenResponseDto>();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        var meResponse = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    /// <summary>Logs in and returns the refresh token as the cookie carries it.</summary>
    private async Task<string> LoginAndGetRefreshCookieAsync()
    {
        var (login, password) = await SeedUserAsync();
        var client = CookieAuth.Client(_factory);
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });
        return CookieAuth.RefreshCookie(response)!;
    }

    [Fact]
    public async Task Refresh_WithValidCookie_RotatesAndReturnsANewCookie()
    {
        var refresh = await LoginAndGetRefreshCookieAsync();
        var client = CookieAuth.Client(_factory);

        var response = await CookieAuth.PostAsync(client, "/api/auth/refresh", refresh);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = CookieAuth.RefreshCookie(response);
        Assert.False(string.IsNullOrWhiteSpace(rotated));
        Assert.NotEqual(refresh, rotated);
    }

    [Fact]
    public async Task Refresh_WithUnknownToken_Returns401()
    {
        var client = CookieAuth.Client(_factory);

        var response = await CookieAuth.PostAsync(client, "/api/auth/refresh", "not-a-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithNoCookieAtAll_Returns401()
    {
        var client = CookieAuth.Client(_factory);

        var response = await CookieAuth.PostAsync(client, "/api/auth/refresh", cookie: null);

        // Reported identically to an invalid token: the difference would tell a
        // caller whether a cookie they hold is a real one.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithRotatedToken_Returns401()
    {
        var refresh = await LoginAndGetRefreshCookieAsync();
        var client = CookieAuth.Client(_factory);

        await CookieAuth.PostAsync(client, "/api/auth/refresh", refresh);

        var reuse = await CookieAuth.PostAsync(client, "/api/auth/refresh", refresh);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Logout_ThenRefresh_Returns401_AndIsIdempotent()
    {
        var refresh = await LoginAndGetRefreshCookieAsync();
        var client = CookieAuth.Client(_factory);

        var logout1 = await CookieAuth.PostAsync(client, "/api/auth/logout", refresh);
        Assert.Equal(HttpStatusCode.NoContent, logout1.StatusCode);

        var logout2 = await CookieAuth.PostAsync(client, "/api/auth/logout", refresh);
        Assert.Equal(HttpStatusCode.NoContent, logout2.StatusCode);

        var refreshed = await CookieAuth.PostAsync(client, "/api/auth/refresh", refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [Fact]
    public async Task Refresh_WhenUserBlocked_Returns401()
    {
        var (login, password) = await SeedUserAsync();
        var client = CookieAuth.Client(_factory);
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });
        var refresh = CookieAuth.RefreshCookie(loginResponse)!;

        // Block the user after login (synchronous Single/SaveChanges via System.Linq).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = db.Users.Single(u => u.Email == login);
            user.Status = UserStatus.Blocked;
            db.SaveChanges();
        }

        var response = await CookieAuth.PostAsync(client, "/api/auth/refresh", refresh);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task SeedRawUserAsync(string email, string username, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Email = email, Username = username,
            PasswordHash = hasher.Hash(password), Role = UserRole.EndUser,
            Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Login_WithAnEmail_NeverMatchesAUsernameThatLooksLikeThatEmail()
    {
        // Registration now refuses an '@' in a username, so this collision can only come from
        // data written before that rule: seed it directly. Alice's email login must reach Alice's
        // account every time, never the squatter's — whichever row Postgres happens to return
        // first, which is exactly what the old OR-match left to chance.
        var unique = Guid.NewGuid().ToString("N");
        var aliceEmail = $"alice-{unique}@example.com";
        await SeedRawUserAsync($"squatter-{unique}@example.com", aliceEmail, "Squatter-Pass-9!");
        await SeedRawUserAsync(aliceEmail, $"alice-{unique[..20]}", "Alice-Password-9!");

        var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            var alice = await client.PostAsJsonAsync("/api/auth/login",
                new { emailOrUsername = aliceEmail, password = "Alice-Password-9!" });
            Assert.Equal(HttpStatusCode.OK, alice.StatusCode);
        }

        var squatter = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = aliceEmail, password = "Squatter-Pass-9!" });
        Assert.Equal(HttpStatusCode.Unauthorized, squatter.StatusCode);
    }

    [Fact]
    public async Task Login_WithAUsername_MatchesTheUsernameExactly()
    {
        var unique = Guid.NewGuid().ToString("N");
        var username = $"Bob-{unique[..20]}";
        await SeedRawUserAsync($"bob-{unique}@example.com", username, "Bob-Password-9!");
        var client = _factory.CreateClient();

        var exact = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = username, password = "Bob-Password-9!" });
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);

        var otherCase = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = username.ToLowerInvariant(), password = "Bob-Password-9!" });
        Assert.Equal(HttpStatusCode.Unauthorized, otherCase.StatusCode);
    }
}
