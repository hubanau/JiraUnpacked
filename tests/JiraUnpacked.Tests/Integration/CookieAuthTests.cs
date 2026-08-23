// tests/JiraUnpacked.Tests/Integration/CookieAuthTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Api.Security;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The refresh token lives in an httpOnly cookie and nowhere else. These tests pin the
/// properties that make that true, rather than the plumbing that happens to implement it.
/// </summary>
public sealed class CookieAuthTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public CookieAuthTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Password = "Correct-Horse-9!";

    private async Task<string> SeedUserAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Email = $"c-{unique}@x.com", Username = $"c-{unique}",
            PasswordHash = hasher.Hash(Password), Role = UserRole.EndUser,
            Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return $"c-{unique}@x.com";
    }

    private async Task<(HttpClient client, HttpResponseMessage login)> LoginAsync()
    {
        var email = await SeedUserAsync();
        var client = CookieAuth.Client(_factory);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, login);
    }

    [Fact]
    public async Task No_auth_response_body_ever_contains_the_refresh_token()
    {
        var (client, login) = await LoginAsync();

        var loginBody = await login.Content.ReadAsStringAsync();
        var loginCookie = CookieAuth.RefreshCookie(login)!;

        var refresh = await CookieAuth.PostAsync(client, "/api/auth/refresh", loginCookie);
        var refreshBody = await refresh.Content.ReadAsStringAsync();
        var rotatedCookie = CookieAuth.RefreshCookie(refresh)!;

        // Asserted on the RAW JSON, not a typed DTO. A typed assertion cannot see a property
        // the DTO does not declare, which is exactly how this regression would come back: add
        // the field to the response record and every typed test keeps passing.
        Assert.DoesNotContain("refreshToken", loginBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(loginCookie, loginBody, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", refreshBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rotatedCookie, refreshBody, StringComparison.Ordinal);

        // The access token IS in the body — otherwise the assertions above could be passing
        // because the bodies are empty.
        Assert.Contains("accessToken", loginBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accessToken", refreshBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_cookie_is_httponly_samesite_strict_and_scoped_to_the_auth_path()
    {
        var (_, login) = await LoginAsync();

        var header = CookieAuth.SetCookieHeader(login);
        Assert.NotNull(header);

        Assert.Contains("httponly", header!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"path={RefreshTokenCookie.Path}", header, StringComparison.OrdinalIgnoreCase);
        // An expiry, so the browser discards it when the server would reject it anyway.
        Assert.Contains("expires=", header, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/logout")]
    public async Task The_cookie_endpoints_refuse_a_request_without_the_xhr_header(string path)
    {
        var (client, login) = await LoginAsync();
        var cookie = CookieAuth.RefreshCookie(login)!;

        var response = await CookieAuth.PostWithoutXhrHeaderAsync(client, path, cookie);

        // Second CSRF layer behind SameSite=Strict: a cross-site form post cannot set a header
        // at all, and a cross-site fetch that sets one triggers a preflight that fails.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(RefreshTokenCookie.RequiredHeader, await response.Content.ReadAsStringAsync());

        // And the token is still usable — the guard rejected the request without consuming it.
        var allowed = await CookieAuth.PostAsync(client, "/api/auth/refresh", cookie);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Logout_clears_the_cookie_as_well_as_revoking_the_token()
    {
        var (client, login) = await LoginAsync();
        var cookie = CookieAuth.RefreshCookie(login)!;

        var logout = await CookieAuth.PostAsync(client, "/api/auth/logout", cookie);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // Revoking alone is not enough: a browser still holding the cookie would re-present a
        // dead token on every later call. The deletion must repeat the path, or it clears a
        // different cookie and leaves the real one in place.
        var header = CookieAuth.SetCookieHeader(logout);
        Assert.NotNull(header);
        Assert.Contains($"path={RefreshTokenCookie.Path}", header!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(CookieAuth.RefreshCookie(logout));
    }

    [Fact]
    public async Task A_rejected_refresh_also_clears_the_cookie()
    {
        var client = CookieAuth.Client(_factory);

        var response = await CookieAuth.PostAsync(client, "/api/auth/refresh", "not-a-real-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Whatever the browser holds cannot work, so leaving it there just means every later
        // request re-presents a token that can only fail.
        Assert.NotNull(CookieAuth.SetCookieHeader(response));
        Assert.Null(CookieAuth.RefreshCookie(response));
    }

    [Fact]
    public void Secure_can_be_turned_off_for_a_plain_http_deployment_but_only_deliberately()
    {
        // The local Docker stack serves http://localhost. Browsers disagree about Secure
        // cookies there — Chrome allows them, Safari historically did not — so leaving Secure
        // on makes sign-in work in one browser and fail in another. Opting out has to be an
        // explicit setting, never a default, because over a real network it puts the refresh
        // token on the wire in clear text.
        Assert.False(RefreshTokenCookie.ShouldRequireSecure(
            isDevelopment: false, secureCookiesEnabled: false));

        // Omitting the flag must keep the safe answer.
        Assert.True(RefreshTokenCookie.ShouldRequireSecure(isDevelopment: false));
    }

    [Fact]
    public void Secure_is_required_everywhere_except_development()
    {
        // The suite and local development both run over plain HTTP, so Secure cannot be
        // asserted through a request. It is a pure function for exactly that reason —
        // and this is the test that stops it being switched off for production too.
        Assert.True(RefreshTokenCookie.ShouldRequireSecure(isDevelopment: false));
        Assert.False(RefreshTokenCookie.ShouldRequireSecure(isDevelopment: true));
    }
}
