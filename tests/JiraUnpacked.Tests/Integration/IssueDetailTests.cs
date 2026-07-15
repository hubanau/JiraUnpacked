// tests/JiraUnpacked.Tests/Integration/IssueDetailTests.cs
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

public sealed class IssueDetailTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueDetailTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
    private sealed record ItemDto(string JiraIssueId, string JiraKey, string? Summary, string? Status, string? IssueType,
        string? Priority, string? Assignee, string? Sprint, string? Team, decimal? StoryPoints, string? ParentJiraIssueId,
        DateTime JiraCreatedAt, DateTime JiraUpdatedAt);
    private sealed record RefDto(string JiraIssueId, string JiraKey, string? Summary);
    private sealed record HistDto(string Field, string? FromValue, string? ToValue, string? Author, DateTime ChangedAt);
    private sealed record DetailDto(ItemDto Issue, RefDto? Parent, List<RefDto> Children, List<HistDto> ChangeHistory);

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

    // epic ACME-1 (10001), story ACME-2 (10002, parent 10001) + one status change-history row on the story.
    private static async Task<Guid> SeedAsync(Guid tenant)
    {
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10001','ACME-1','Epic',now(),now(),now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""ParentJiraIssueId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10002','ACME-2','Story','10001',now(),now(),now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") VALUES (@i,@t,@p,'10002','h1','status','To Do','In Progress','Alice',now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        return projId;
    }

    [Fact]
    public async Task Detail_returns_issue_with_history_parent_and_children()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedAsync(tenant);

        // Story: has a parent (epic) + a change-history row.
        var story = await (await client.GetAsync($"/api/projects/{projectId}/issues/10002")).Content.ReadFromJsonAsync<DetailDto>();
        Assert.Equal("ACME-2", story!.Issue.JiraKey);
        Assert.Equal("ACME-1", story.Parent!.JiraKey);
        var h = Assert.Single(story.ChangeHistory);
        Assert.Equal("status", h.Field);
        Assert.Equal("In Progress", h.ToValue);

        // Epic: no parent, one child (the story).
        var epic = await (await client.GetAsync($"/api/projects/{projectId}/issues/10001")).Content.ReadFromJsonAsync<DetailDto>();
        Assert.Null(epic!.Parent);
        Assert.Contains(epic.Children, ch => ch.JiraKey == "ACME-2");
    }

    [Fact]
    public async Task Unknown_issue_returns_404()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedAsync(tenant);
        var resp = await client.GetAsync($"/api/projects/{projectId}/issues/99999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
