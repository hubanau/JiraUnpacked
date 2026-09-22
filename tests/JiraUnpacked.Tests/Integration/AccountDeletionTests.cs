// tests/JiraUnpacked.Tests/Integration/AccountDeletionTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Integration;

public sealed class AccountDeletionTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public AccountDeletionTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc);

    private async Task<(HttpClient client, Guid userId, string refreshCookie)> AuthedEndUserAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User
            {
                Id = userId, Email = $"u-{unique}@x.com", Username = $"u-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var c = CookieAuth.Client(_factory);
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return (c, userId, CookieAuth.RefreshCookie(login)!);
    }

    private async Task<HttpClient> AuthedAdminAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(), Email = $"a-{unique}@x.com", Username = $"a-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.Admin,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"a-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return c;
    }

    [Fact]
    public async Task Self_service_delete_removes_the_account_and_severs_the_refresh_chain()
    {
        var (client, _, refreshCookie) = await AuthedEndUserAsync();

        var resp = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Refresh tokens cascade away with the user, so the session cannot be extended.
        var refreshed = await CookieAuth.PostAsync(
            CookieAuth.Client(_factory), "/api/auth/refresh", refreshCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [Fact]
    public async Task The_still_valid_access_token_is_rejected_once_the_account_is_gone()
    {
        var (client, _, _) = await AuthedEndUserAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/connections")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/auth/me")).StatusCode);

        // The JWT is still inside its lifetime, but it names an account that no longer exists:
        // every authorized endpoint answers 401 — even the claims-only /me.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/connections")).StatusCode);
    }

    [Fact]
    public async Task Admin_can_delete_an_end_user()
    {
        var (_, endUserId, _) = await AuthedEndUserAsync();
        var admin = await AuthedAdminAsync();

        var resp = await admin.DeleteAsync($"/api/admin/users/{endUserId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Independently confirm the account is actually gone — a 204 alone would also be
        // returned by a service that deleted nothing.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Users.AnyAsync(u => u.Id == endUserId));
        }
    }

    [Fact]
    public async Task Admin_deleting_an_unknown_user_returns_404()
    {
        var admin = await AuthedAdminAsync();
        var resp = await admin.DeleteAsync($"/api/admin/users/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_accounts_are_not_deletable()
    {
        var admin = await AuthedAdminAsync();

        Guid otherAdminId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            otherAdminId = Guid.NewGuid();
            var unique = Guid.NewGuid().ToString("N");
            db.Users.Add(new User
            {
                Id = otherAdminId, Email = $"a2-{unique}@x.com", Username = $"a2-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.Admin,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resp = await admin.DeleteAsync($"/api/admin/users/{otherAdminId}");
        // 400 matches the existing block/unblock convention for the same condition.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task An_end_user_cannot_delete_another_account_through_the_admin_route()
    {
        var (client, _, _) = await AuthedEndUserAsync();
        var (_, victimId, _) = await AuthedEndUserAsync();

        var resp = await client.DeleteAsync($"/api/admin/users/{victimId}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
