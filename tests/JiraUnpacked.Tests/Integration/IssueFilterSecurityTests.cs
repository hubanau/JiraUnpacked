// tests/JiraUnpacked.Tests/Integration/IssueFilterSecurityTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// FR-E4 security-critical guarantees for POST api/projects/{projectId}/issues/query. These assert
/// behaviour that already exists after EF1-EF3 (no production code is added by this task) — they are
/// the regression net for the central safety property: user filter input never reaches a SQL string.
///   1. Tenant isolation: cross-tenant project id => 404; own-project results never leak another tenant's rows.
///   2. Injection-inert: a SQL-injection-shaped Contains value matches nothing and does not affect the
///      table; LIKE metacharacters (%, _) in a Contains value are escaped, not treated as wildcards.
///   3. Tenant-scoped dict resolution: a dictionary name that exists only in another tenant's data
///      resolves to no Id (and hence no matches) in the querying tenant's scope.
/// Every seed and assertion below is scoped to the test's own fresh JiraProjectId.
/// </summary>
public sealed class IssueFilterSecurityTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueFilterSecurityTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
    private sealed record ItemDto(string JiraIssueId, string JiraKey, string? Summary, string? Status, string? IssueType,
        string? Priority, string? Assignee, string? Sprint, string? Team, decimal? StoryPoints, string? ParentJiraIssueId,
        DateTime JiraCreatedAt, DateTime JiraUpdatedAt);
    private sealed record PageDto(List<ItemDto> Items, int Page, int PageSize, int TotalCount);

    // Creates an Active EndUser, returns (authed client, tenantId=userId). The tenant EndUserId == user.Id.
    private async Task<(HttpClient client, Guid tenantId)> AuthedTenantAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User { Id = userId, Email = $"u-{unique}@x.com", Username = $"u-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return (c, userId);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    // Seeds connection + project + one "In Progress" status + one issue in that status, scoped to a
    // fresh JiraProjectId for the given tenant. Returns (projectId, the seeded issue's JiraKey).
    private static async Task<(Guid projectId, string jiraKey)> SeedProjectWithOneInProgressIssueAsync(Guid tenant)
    {
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid(); var statusId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'10','In Progress','indeterminate')", ("i", statusId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10001','ACME-1','Seeded issue',@s,now(),now(),now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("s", statusId));
        return (projId, "ACME-1");
    }

    // ---- request-building + calling helpers ----

    private static object Cond(string field, string op, object? value = null, object? values = null) =>
        values is not null ? new { field, op, values }
        : value is not null ? new { field, op, value }
        : new { field, op };

    private static object Req(List<object> conditions, int page = 1, int pageSize = 50) =>
        new { conditions, page, pageSize };

    private static Task<HttpResponseMessage> PostQueryAsync(HttpClient client, Guid projectId, object request) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/issues/query", request);

    private static async Task<PageDto> QueryOkAsync(HttpClient client, Guid projectId, object request)
    {
        var resp = await PostQueryAsync(client, projectId, request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<PageDto>())!;
    }

    // ============================================================
    // 1. Tenant isolation
    // ============================================================

    [Fact]
    public async Task Cross_tenant_project_id_returns_404()
    {
        var (_, tenantA) = await AuthedTenantAsync();
        var (projectA, _) = await SeedProjectWithOneInProgressIssueAsync(tenantA);

        var (clientB, _) = await AuthedTenantAsync();
        // B POSTs a (well-formed, matching) filter at A's project id.
        var resp = await PostQueryAsync(clientB, projectA,
            Req(new() { Cond("status", "Eq", value: "In Progress") }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Filtering_own_project_never_returns_another_tenants_issues()
    {
        var (clientA, tenantA) = await AuthedTenantAsync();
        var (projectA, keyA) = await SeedProjectWithOneInProgressIssueAsync(tenantA);

        // B seeds its own project with its own In-Progress issue (same status name, disjoint tenant).
        var (_, tenantB) = await AuthedTenantAsync();
        await SeedProjectWithOneInProgressIssueAsync(tenantB);

        // A filters ITS OWN project with a condition that would also match B's issue if the base
        // tenant filter + RLS ever failed to hold.
        var page = await QueryOkAsync(clientA, projectA,
            Req(new() { Cond("status", "Eq", value: "In Progress") }));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(keyA, page.Items[0].JiraKey);
    }

    // ============================================================
    // 2. Injection-inert
    // ============================================================

    [Fact]
    public async Task Sql_injection_shaped_contains_value_matches_nothing_and_leaves_the_table_intact()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var (projectId, _) = await SeedProjectWithOneInProgressIssueAsync(tenant);

        var resp = await PostQueryAsync(client, projectId,
            Req(new() { Cond("summary", "Contains", value: "'; DROP TABLE \"Issues\"; --") }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = (await resp.Content.ReadFromJsonAsync<PageDto>())!;
        Assert.Equal(0, page.TotalCount);

        // Prove the Issues table still exists and is unaffected: a follow-up unfiltered query still
        // returns the seeded row (a dropped table would 500, not 200-with-the-same-count).
        var unfiltered = await QueryOkAsync(client, projectId, Req(new()));
        Assert.Equal(1, unfiltered.TotalCount);
    }

    [Fact]
    public async Task Percent_and_underscore_in_contains_value_are_escaped_not_treated_as_wildcards()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
            await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
            // ACME-1's summary is literally "50%_off"; ACME-2's is literally "50Xoff" — the string you'd
            // ALSO match if '%' and '_' were left as live LIKE wildcards instead of escaped.
            await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10001','ACME-1','50%_off',now(),now(),now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
            await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10002','ACME-2','50Xoff',now(),now(),now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        }

        var page = await QueryOkAsync(client, projId,
            Req(new() { Cond("summary", "Contains", value: "50%_off") }));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ACME-1", page.Items[0].JiraKey);
    }

    // ============================================================
    // 3. Tenant-scoped dict resolution
    // ============================================================

    [Fact]
    public async Task Status_name_that_exists_only_in_another_tenant_resolves_to_no_matches()
    {
        var (clientA, tenantA) = await AuthedTenantAsync();
        var (projectA, _) = await SeedProjectWithOneInProgressIssueAsync(tenantA);

        // B has a status name that does not exist anywhere in A's data.
        var (_, tenantB) = await AuthedTenantAsync();
        var bOnlyStatusName = $"OnlyInTenantB-{Guid.NewGuid():N}";
        var bConnId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", bConnId), ("t", tenantB));
            await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'99',@n,'new')", ("i", Guid.NewGuid()), ("t", tenantB), ("c", bConnId), ("n", bOnlyStatusName));
        }

        // A filters by B's status name against A's own project.
        var page = await QueryOkAsync(clientA, projectA,
            Req(new() { Cond("status", "Eq", value: bOnlyStatusName) }));

        Assert.Equal(0, page.TotalCount);
    }
}
