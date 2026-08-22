// tests/JiraUnpacked.Tests/Integration/FilterTestData.cs
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
/// Shared auth + raw-SQL seeding for the FR-E4 filter tests. Seeding uses the superuser
/// connection (bypasses RLS) purely to build fixtures; every assertion reads through the API
/// as the owning tenant, and each call scopes its rows to a fresh JiraProjectId so parallel
/// test classes never collide.
///
/// The fixture is one project with six issues, newest-JiraUpdatedAt first:
///   ACME-1 (10001) In Progress, Alice, 5 pts, no parent,    "Fix login timeout",       updated Jan 5
///   ACME-2 (10002) Done,        Alice, 1 pt,  no parent,    "Update onboarding docs",  updated Jan 4
///   ACME-3 (10003) no status,   unassigned,   no parent,    "Investigate memory leak", updated Jan 3
///   ACME-4 (10004) no status,   Bob,          no parent,    "Parent epic for children", updated Jan 2
///   ACME-5 (10005) no status,   Bob,          parent=10004, "Child task one",          updated Jan 6
///   ACME-6 (10006) no status,   Bob,          parent=10004, "Child task two",          updated Jan 7
/// </summary>
internal static class FilterTestData
{
    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    internal sealed record ItemDto(
        string JiraIssueId, string JiraKey, string? Summary, string? Status, string? IssueType,
        string? Priority, string? Assignee, string? Sprint, string? Team, decimal? StoryPoints,
        string? ParentJiraIssueId, DateTime JiraCreatedAt, DateTime JiraUpdatedAt);

    internal sealed record PageDto(List<ItemDto> Items, int Page, int PageSize, int TotalCount);

    /// <summary>Creates an Active EndUser and logs in. The tenant EndUserId == user.Id.</summary>
    internal static async Task<(HttpClient client, Guid tenantId)> AuthedTenantAsync(JiraUnpackedApiFactory factory)
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
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

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tokens = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return (client, userId);
    }

    internal static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static DateTime D(int day) => new(2024, 1, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Seeds connection + project + dicts + the six issues. Returns the JiraProjectId.</summary>
    internal static async Task<Guid> SeedFilterFixtureAsync(Guid tenant)
    {
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        var inProgressId = Guid.NewGuid(); var doneId = Guid.NewGuid();
        var aliceId = Guid.NewGuid(); var bobId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();

        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'10','In Progress','indeterminate')", ("i", inProgressId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'11','Done','done')", ("i", doneId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""JiraUsers""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraAccountId"",""DisplayName"") VALUES (@i,@t,@c,'u1','Alice')", ("i", aliceId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""JiraUsers""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraAccountId"",""DisplayName"") VALUES (@i,@t,@c,'u2','Bob')", ("i", bobId), ("t", tenant), ("c", connId));

        async Task Issue(string jiraId, string key, string summary, Guid? status, Guid? assignee,
            decimal? points, string? parent, DateTime createdAt, DateTime updatedAt) =>
            await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""AssigneeId"",""StoryPoints"",""ParentJiraIssueId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,@ji,@k,@s,@st,@a,@sp,@par,@ca,@ua,now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("ji", jiraId), ("k", key), ("s", summary),
                ("st", status), ("a", assignee), ("sp", points), ("par", parent), ("ca", createdAt), ("ua", updatedAt));

        await Issue("10001", "ACME-1", "Fix login timeout", inProgressId, aliceId, 5m, null, D(1), D(5));
        await Issue("10002", "ACME-2", "Update onboarding docs", doneId, aliceId, 1m, null, D(1), D(4));
        await Issue("10003", "ACME-3", "Investigate memory leak", null, null, null, null, D(1), D(3));
        await Issue("10004", "ACME-4", "Parent epic for children", null, bobId, null, null, D(1), D(2));
        await Issue("10005", "ACME-5", "Child task one", null, bobId, null, "10004", D(1), D(6));
        await Issue("10006", "ACME-6", "Child task two", null, bobId, null, "10004", D(1), D(7));

        return projId;
    }

    // ---- request-building + calling helpers ----

    internal static object Cond(string field, string op, object? value = null, object? values = null) =>
        values is not null ? new { field, op, values }
        : value is not null ? new { field, op, value }
        : new { field, op };

    internal static object Req(List<object> conditions, int page = 1, int pageSize = 50) =>
        new { conditions, page, pageSize };

    internal static Task<HttpResponseMessage> PostQueryAsync(HttpClient client, Guid projectId, object request) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/issues/query", request);

    internal static async Task<PageDto> QueryOkAsync(HttpClient client, Guid projectId, object request)
    {
        var resp = await PostQueryAsync(client, projectId, request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<PageDto>())!;
    }
}
