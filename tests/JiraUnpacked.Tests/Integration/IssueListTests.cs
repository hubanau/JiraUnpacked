// tests/JiraUnpacked.Tests/Integration/IssueListTests.cs
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

public sealed class IssueListTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueListTests(JiraUnpackedApiFactory f) => _factory = f;
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

    // Seeds connection + project + a few dicts + two issues (epic ACME-1, story ACME-2 w/ parent) via raw SQL
    // (superuser conn bypasses RLS). Returns the projectId.
    private static async Task<Guid> SeedProjectWithIssuesAsync(Guid tenant)
    {
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        var statusId = Guid.NewGuid(); var typeId = Guid.NewGuid(); var userDictId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'10','In Progress','indeterminate')", ("i", statusId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""IssueTypes""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraIssueTypeId"",""Name"") VALUES (@i,@t,@c,'1','Story')", ("i", typeId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""JiraUsers""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraAccountId"",""DisplayName"") VALUES (@i,@t,@c,'u1','Alice')", ("i", userDictId), ("t", tenant), ("c", connId));
        // epic ACME-1 (older update), story ACME-2 (newer update, parent 10001, resolved dict FKs)
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10001','ACME-1','Epic',timestamptz '2024-01-01 00:00+00',timestamptz '2024-01-01 00:00+00',now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""IssueTypeId"",""AssigneeId"",""StoryPoints"",""ParentJiraIssueId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10002','ACME-2','Story',@s,@ty,@u,3,'10001',timestamptz '2024-01-02 00:00+00',timestamptz '2024-01-03 00:00+00',now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("s", statusId), ("ty", typeId), ("u", userDictId));
        return projId;
    }

    [Fact]
    public async Task Lists_issues_paginated_with_resolved_names_newest_first()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedProjectWithIssuesAsync(tenant);

        var page1 = await (await client.GetAsync($"/api/projects/{projectId}/issues?page=1&pageSize=1")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal(2, page1!.TotalCount);
        Assert.Single(page1.Items);
        Assert.Equal("ACME-2", page1.Items[0].JiraKey);      // newest JiraUpdatedAt first
        Assert.Equal("In Progress", page1.Items[0].Status);  // resolved dict name
        Assert.Equal("Story", page1.Items[0].IssueType);
        Assert.Equal("Alice", page1.Items[0].Assignee);
        Assert.Equal(3m, page1.Items[0].StoryPoints);
        Assert.Equal("10001", page1.Items[0].ParentJiraIssueId);

        var page2 = await (await client.GetAsync($"/api/projects/{projectId}/issues?page=2&pageSize=1")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal("ACME-1", page2!.Items[0].JiraKey);
        Assert.Null(page2.Items[0].Status); // epic had no status FK -> null name
    }

    [Fact]
    public async Task Unknown_project_returns_404()
    {
        var (client, _) = await AuthedTenantAsync();
        var resp = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/issues");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Tenant_cannot_list_another_tenants_issues()
    {
        var (_, tenantA) = await AuthedTenantAsync();
        var projectId = await SeedProjectWithIssuesAsync(tenantA);
        var (clientB, _) = await AuthedTenantAsync();
        // B asks for A's project id -> filtered out -> 404 (no existence leak)
        var resp = await clientB.GetAsync($"/api/projects/{projectId}/issues");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Pagination_params_are_clamped()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedProjectWithIssuesAsync(tenant);

        // pageSize > 100 is capped to 100; page < 1 is floored to 1 (response echoes the clamped values).
        var page = await (await client.GetAsync($"/api/projects/{projectId}/issues?page=0&pageSize=500")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal(1, page!.Page);
        Assert.Equal(100, page.PageSize);
        Assert.Equal(2, page.TotalCount); // both seeded issues returned within the (clamped) page
        Assert.Equal(2, page.Items.Count);
    }
}
