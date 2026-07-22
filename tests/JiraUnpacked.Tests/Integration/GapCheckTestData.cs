// tests/JiraUnpacked.Tests/Integration/GapCheckTestData.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>Ids a seeded gap-check fixture hands back to the test.</summary>
internal sealed record CheckSeed(
    Guid Tenant, Guid ConnectionId, Guid ProjectId,
    Guid EpicTypeId, Guid StoryTypeId,
    Guid ToDoStatusId, Guid DoneStatusId);

/// <summary>
/// Shared auth + raw-SQL seeding for the FR-E3 gap-check tests. Seeding uses the superuser
/// connection (bypasses RLS) purely to build fixtures; every assertion reads through the API
/// as the owning tenant. No WireMock anywhere — gap-checks need zero Jira calls.
/// </summary>
internal static class GapCheckTestData
{
    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

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
        var c = factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return (c, userId);
    }

    internal static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static NpgsqlConnection Conn() => new(JiraUnpackedApiFactory.TestConnectionString);

    /// <summary>
    /// Seeds connection + project + two issue types ("Epic" and "Story", both Class='Other'
    /// initially) + two statuses ("To Do", "Done") + their StatusSemantics (Other / Done).
    /// Fresh ids every call, so parallel tests never collide.
    /// </summary>
    internal static async Task<CheckSeed> SeedAsync(Guid tenant)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var epicType = Guid.NewGuid();
        var storyType = Guid.NewGuid();
        var todo = Guid.NewGuid();
        var done = Guid.NewGuid();

        await using var conn = Conn();
        await conn.OpenAsync();

        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())",
            ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())",
            ("i", projId), ("t", tenant), ("c", connId));

        // Both types start unclassified ('Other'): tests opt in by classifying one as Epic.
        await Exec(conn, @"INSERT INTO ""IssueTypes""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraIssueTypeId"",""Name"",""Class"") VALUES (@i,@t,@c,'1','Epic','Other')",
            ("i", epicType), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""IssueTypes""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraIssueTypeId"",""Name"",""Class"") VALUES (@i,@t,@c,'2','Story','Other')",
            ("i", storyType), ("t", tenant), ("c", connId));

        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'1','To Do','new')",
            ("i", todo), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'2','Done','done')",
            ("i", done), ("t", tenant), ("c", connId));

        await Exec(conn, @"INSERT INTO ""StatusSemantics""(""Id"",""EndUserId"",""JiraProjectId"",""JiraStatusId"",""StatusName"",""Class"") VALUES (@i,@t,@p,'1','To Do','Other')",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""StatusSemantics""(""Id"",""EndUserId"",""JiraProjectId"",""JiraStatusId"",""StatusName"",""Class"") VALUES (@i,@t,@p,'2','Done','Done')",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));

        return new CheckSeed(tenant, connId, projId, epicType, storyType, todo, done);
    }

    /// <summary>Classifies an issue type directly in the DB (bypasses the API).</summary>
    internal static async Task SetIssueTypeClassAsync(CheckSeed seed, string jiraIssueTypeId, string cls)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"UPDATE ""IssueTypes"" SET ""Class"" = @c WHERE ""JiraConnectionId"" = @k AND ""JiraIssueTypeId"" = @i",
            ("c", cls), ("k", seed.ConnectionId), ("i", jiraIssueTypeId));
    }

    /// <summary>Repoints a status name to a different class — the FR-E3 "mapping edit" lever.</summary>
    internal static async Task SetStatusClassAsync(CheckSeed seed, string jiraStatusId, string cls)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"UPDATE ""StatusSemantics"" SET ""Class"" = @c WHERE ""JiraProjectId"" = @p AND ""JiraStatusId"" = @i",
            ("c", cls), ("p", seed.ProjectId), ("i", jiraStatusId));
    }

    internal static async Task SeedIssueAsync(
        CheckSeed seed, string jiraIssueId, string jiraKey, string? summary,
        Guid? issueTypeId, Guid? statusId, string? parentJiraIssueId)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""IssueTypeId"",""StatusId"",""ParentJiraIssueId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,@jid,@k,@s,@ty,@st,@par,now(),now(),now())",
            ("i", Guid.NewGuid()), ("t", seed.Tenant), ("p", seed.ProjectId), ("jid", jiraIssueId),
            ("k", jiraKey), ("s", summary), ("ty", issueTypeId), ("st", statusId), ("par", parentJiraIssueId));
    }

    /// <summary>
    /// Seeds a raw issue snapshot so a re-derive actually runs the dictionary upsert path
    /// (DerivationService.ResolveTypeAsync). Without a snapshot, /rederive is a no-op and any
    /// test asserting "classification survived" would pass vacuously.
    /// </summary>
    internal static async Task SeedRawSnapshotAsync(
        CheckSeed seed, string jiraIssueId, string jiraKey, string jiraIssueTypeId, string issueTypeName)
    {
        var payload = $"{{\"id\":\"{jiraIssueId}\",\"key\":\"{jiraKey}\",\"fields\":{{" +
                      $"\"summary\":\"seeded\"," +
                      $"\"created\":\"2024-01-01T00:00:00.000+0000\"," +
                      $"\"updated\":\"2024-01-01T00:00:00.000+0000\"," +
                      $"\"issuetype\":{{\"id\":\"{jiraIssueTypeId}\",\"name\":\"{issueTypeName}\"}}}}}}";

        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,@jid,@j::jsonb,now())",
            ("i", Guid.NewGuid()), ("t", seed.Tenant), ("p", seed.ProjectId), ("jid", jiraIssueId), ("j", payload));
    }
}
