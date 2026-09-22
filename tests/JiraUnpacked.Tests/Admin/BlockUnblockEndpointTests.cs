using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Admin;

public sealed class BlockUnblockEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public BlockUnblockEndpointTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    private const string Password = "Correct-Horse-9!";

    private async Task<(Guid id, string email)> SeedUserAsync(UserRole role)
    {
        var unique = Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"user-{unique}@example.com",
            Username = $"user-{unique}",
            PasswordHash = hasher.Hash(Password),
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Id, user.Email);
    }

    private async Task<TokenResponseDto> LoginAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password = Password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<TokenResponseDto>())!;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, email) = await SeedUserAsync(UserRole.Admin);
        var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    [Fact]
    public async Task Block_SetsBlocked_RevokesRefreshTokens_AndStopsAuth()
    {
        var (endUserId, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        // An active session before the block, so the assertions below prove the block
        // actually severs it rather than that it was never established.
        var endUserClient = Integration.CookieAuth.Client(_factory);
        var loginResponse = await endUserClient.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = endUserEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var endUserRefreshCookie = Integration.CookieAuth.RefreshCookie(loginResponse)!;

        var admin = await AdminClientAsync();
        var block = await admin.PostAsync($"/api/admin/users/{endUserId}/block", content: null);
        Assert.Equal(HttpStatusCode.NoContent, block.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == endUserId);
            Assert.Equal(UserStatus.Blocked, user.Status);
            Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == endUserId && t.RevokedAt == null));
        }

        var reLogin = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = endUserEmail, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, reLogin.StatusCode);

        var refresh = await Integration.CookieAuth.PostAsync(
            Integration.CookieAuth.Client(_factory), "/api/auth/refresh", endUserRefreshCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Block_RejectsTheStillValidAccessToken_OnReadsAndOnSync()
    {
        var (endUserId, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var seed = await GapCheckTestData.SeedAsync(endUserId);

        var endUser = _factory.CreateClient();
        var tokens = await LoginAsync(endUser, endUserEmail);
        endUser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        // The token works before the block, on the very project the sync below targets.
        Assert.Equal(HttpStatusCode.OK, (await endUser.GetAsync("/api/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await endUser.GetAsync($"/api/projects/{seed.ProjectId}/sync-runs")).StatusCode);

        var admin = await AdminClientAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/api/admin/users/{endUserId}/block", content: null)).StatusCode);

        // Same access token, well inside its lifetime: FR-A5 says it no longer authenticates.
        Assert.Equal(HttpStatusCode.Unauthorized, (await endUser.GetAsync("/api/connections")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await endUser.PostAsync($"/api/projects/{seed.ProjectId}/sync", content: null)).StatusCode);

        // ...and "cannot trigger syncs" means nothing was queued, not merely a 401 on the way out.
        // Read as the owner role: the app role's RLS would hide the rows and make this vacuous.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new JiraUnpacked.Infrastructure.Tenancy.NullCurrentTenant());
        Assert.False(await db.SyncRuns.IgnoreQueryFilters().AnyAsync(r => r.JiraProjectId == seed.ProjectId));
    }

    [Fact]
    public async Task Unblock_ThenFreshLogin_WorksAgain()
    {
        var (endUserId, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var endUser = _factory.CreateClient();
        var before = await LoginAsync(endUser, endUserEmail);
        endUser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", before.AccessToken);

        var admin = await AdminClientAsync();
        await admin.PostAsync($"/api/admin/users/{endUserId}/block", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await endUser.GetAsync("/api/connections")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/api/admin/users/{endUserId}/unblock", content: null)).StatusCode);

        var fresh = _factory.CreateClient();
        var after = await LoginAsync(fresh, endUserEmail);
        fresh.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", after.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/connections")).StatusCode);
    }

    [Fact]
    public async Task Block_LeavesAnonymousEndpointsReachable_WithTheStaleToken()
    {
        var (endUserId, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var endUser = _factory.CreateClient();
        var tokens = await LoginAsync(endUser, endUserEmail);
        endUser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var admin = await AdminClientAsync();
        await admin.PostAsync($"/api/admin/users/{endUserId}/block", content: null);

        // A browser still holding the old token must get the login endpoint's own answer (the
        // uniform "invalid credentials" for a blocked account), not the middleware's rejection,
        // and health stays anonymous.
        Assert.Equal(HttpStatusCode.OK, (await endUser.GetAsync("/health/live")).StatusCode);
        var login = await endUser.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = endUserEmail, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Contains("Invalid credentials", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unblock_RestoresLogin()
    {
        var (endUserId, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var admin = await AdminClientAsync();

        await admin.PostAsync($"/api/admin/users/{endUserId}/block", content: null);
        var unblock = await admin.PostAsync($"/api/admin/users/{endUserId}/unblock", content: null);
        Assert.Equal(HttpStatusCode.NoContent, unblock.StatusCode);

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = endUserEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Block_NonExistentUser_Returns404()
    {
        var admin = await AdminClientAsync();
        var block = await admin.PostAsync($"/api/admin/users/{Guid.NewGuid()}/block", content: null);
        Assert.Equal(HttpStatusCode.NotFound, block.StatusCode);
    }

    [Fact]
    public async Task Block_AnAdmin_Returns400()
    {
        var (adminId, _) = await SeedUserAsync(UserRole.Admin);
        var admin = await AdminClientAsync();
        var block = await admin.PostAsync($"/api/admin/users/{adminId}/block", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, block.StatusCode);
    }

    [Fact]
    public async Task Block_AsEndUser_Returns403()
    {
        var (targetId, _) = await SeedUserAsync(UserRole.EndUser);
        var (_, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, endUserEmail);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var block = await client.PostAsync($"/api/admin/users/{targetId}/block", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, block.StatusCode);
    }

    [Fact]
    public async Task Block_Anonymous_Returns401()
    {
        var (targetId, _) = await SeedUserAsync(UserRole.EndUser);
        var block = await _factory.CreateClient().PostAsync($"/api/admin/users/{targetId}/block", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, block.StatusCode);
    }
}
