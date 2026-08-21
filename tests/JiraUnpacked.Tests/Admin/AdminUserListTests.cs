using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Admin;

public sealed class AdminUserListTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public AdminUserListTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    private const string Password = "Correct-Horse-9!";

    private async Task<(Guid id, string email)> SeedUserAsync(
        UserRole role, DateTime? createdAt = null, Guid? id = null)
    {
        var unique = Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Id = id ?? Guid.NewGuid(),
            Email = $"user-{unique}@example.com",
            Username = $"user-{unique}",
            PasswordHash = hasher.Hash(Password),
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = createdAt ?? DateTime.UtcNow
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

    [Fact] // The Admin policy is the ONLY thing between an End User and every account.
    public async Task End_user_cannot_list_users()
    {
        var (_, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, endUserEmail);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Response_never_contains_a_password_hash()
    {
        await SeedUserAsync(UserRole.EndUser);
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        // Asserted on the RAW JSON, not a typed DTO: a typed assertion cannot
        // catch an extra serialised property.
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$2a$", raw); // a bcrypt hash prefix, in case the name changes
    }

    [Fact]
    public async Task Admin_sees_users_paged()
    {
        await SeedUserAsync(UserRole.EndUser);
        var admin = await AdminClientAsync(); // seeds a second user (the admin itself)

        var response = await admin.GetAsync("/api/admin/users?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminUserListItem>>();
        Assert.NotNull(result);
        Assert.Single(result!.Items);
        Assert.True(result.TotalCount >= 2);
        Assert.Equal(1, result.Page);
        Assert.Equal(1, result.PageSize);
    }

    // UserAdminService.ListAsync now clamps page/pageSize through the same
    // DefaultPageSize/MaxPageSize consts IssueQueryService, GapCheckRunner,
    // and MetricsQueryService use — these pin the three clamp boundaries
    // through the actual endpoint rather than just the constant values.

    [Fact]
    public async Task Page_below_one_clamps_to_one()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync("/api/admin/users?page=0&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminUserListItem>>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.Page);
    }

    [Fact]
    public async Task PageSize_below_one_clamps_to_default_of_fifty()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync("/api/admin/users?page=1&pageSize=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminUserListItem>>();
        Assert.NotNull(result);
        Assert.Equal(50, result!.PageSize);
    }

    [Fact]
    public async Task PageSize_above_max_clamps_to_one_hundred()
    {
        var admin = await AdminClientAsync();

        var response = await admin.GetAsync("/api/admin/users?page=1&pageSize=1000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminUserListItem>>();
        Assert.NotNull(result);
        Assert.Equal(100, result!.PageSize);
    }

    [Fact]
    public async Task Lists_users_ordered_by_created_at_desc_then_id_ascending_on_ties()
    {
        var admin = await AdminClientAsync();

        // Far-future timestamps guarantee these rank ahead of every other
        // row this shared, never-truncated-between-tests database
        // accumulates (see JiraUnpackedApiFactory.EnsureDatabaseAsync —
        // it migrates, it does not reset) without needing to assume
        // anything about total row count or exact page position.
        var farFuture = new DateTime(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (newestId, _) = await SeedUserAsync(UserRole.EndUser, farFuture.AddDays(2));
        var (olderId, _) = await SeedUserAsync(UserRole.EndUser, farFuture.AddDays(1));
        // Same CreatedAt on both — only the Id tie-break can order these two.
        // Postgres compares uuid byte-for-byte in the order the canonical
        // string is written, so this literal pair is ordered the same way
        // as the string comparison "...0001" < "...0002" suggests.
        var (tieLowId, _) = await SeedUserAsync(
            UserRole.EndUser, farFuture, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var (tieHighId, _) = await SeedUserAsync(
            UserRole.EndUser, farFuture, Guid.Parse("00000000-0000-0000-0000-000000000002"));

        var response = await admin.GetAsync("/api/admin/users?page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PagedResult<AdminUserListItem>>();
        Assert.NotNull(result);

        var order = result!.Items.Select(u => u.Id).ToList();
        var positions = new[] { newestId, olderId, tieLowId, tieHighId }
            .Select(id => order.IndexOf(id))
            .ToList();

        Assert.All(positions, p => Assert.True(p >= 0, "a seeded far-future user is missing from the response"));
        Assert.True(positions[0] < positions[1], "CreatedAt DESC: the newer row must come first");
        Assert.True(positions[1] < positions[2], "CreatedAt DESC: rows with a later CreatedAt must come first");
        Assert.True(positions[2] < positions[3], "tie-break: Id ascending when CreatedAt is equal");
    }
}
