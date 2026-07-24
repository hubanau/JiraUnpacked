// tests/JiraUnpacked.Tests/Tenancy/TenantDeletionTestData.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Tenancy;

internal sealed record GraphSeed(Guid ConnectionId, Guid ProjectId);

internal static class TenantDeletionTestData
{
    internal static async Task<Guid> SeedUserAsync(JiraUnpackedApiFactory factory)
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.Users.Add(new User
        {
            Id = userId, Email = $"u-{unique}@x.com", Username = $"u-{unique}",
            PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
            Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return userId;
    }

    internal static async Task DeleteAccountAsync(JiraUnpackedApiFactory factory, Guid userId, Guid requestedBy)
    {
        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();
        var result = await svc.DeleteAsync(userId, requestedBy);
        Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
    }

    private static NpgsqlConnection Conn() => new(JiraUnpackedApiFactory.TestConnectionString);

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Seeds a representative multi-table graph for one tenant. The MODEL-level gate proves
    /// every tenant table cascades; this graph proves the cascade genuinely fires across
    /// connection-, project- and issue-scoped tables.
    /// </summary>
    internal static async Task<GraphSeed> SeedFullGraphAsync(Guid tenant)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var statusId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var userDictId = Guid.NewGuid();

        await using var conn = Conn();
        await conn.OpenAsync();

        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())",
            ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())",
            ("i", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'1','To Do','new')",
            ("i", statusId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""IssueTypes""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraIssueTypeId"",""Name"",""Class"") VALUES (@i,@t,@c,'1','Story','Other')",
            ("i", typeId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""JiraUsers""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraAccountId"",""DisplayName"") VALUES (@i,@t,@c,'u1','Alice')",
            ("i", userDictId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""StatusSemantics""(""Id"",""EndUserId"",""JiraProjectId"",""JiraStatusId"",""StatusName"",""Class"") VALUES (@i,@t,@p,'1','To Do','Other')",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""IssueTypeId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10001','ACME-1','s',@st,@ty,now(),now(),now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("st", statusId), ("ty", typeId));
        await Exec(conn, @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") VALUES (@i,@t,@p,'10001','h1','status','To Do','Done','Alice',now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001','{}'::jsonb,now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""RawChangelog""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001','{}'::jsonb,now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""JiraQueries""(""Id"",""EndUserId"",""JiraProjectId"",""Jql"",""CreatedAt"") VALUES (@i,@t,@p,'project = ACME',now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""FieldMappings""(""Id"",""EndUserId"",""JiraProjectId"",""Role"",""JiraFieldId"") VALUES (@i,@t,@p,'Assignee','customfield_10001')",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""Priorities""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraPriorityId"",""Name"") VALUES (@i,@t,@c,'1','High')",
            ("i", Guid.NewGuid()), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Sprints""(""Id"",""EndUserId"",""JiraProjectId"",""JiraSprintId"",""Name"",""State"") VALUES (@i,@t,@p,'1','Sprint 1','active')",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""Teams""(""Id"",""EndUserId"",""JiraProjectId"",""JiraTeamId"",""Name"") VALUES (@i,@t,@p,'1','Team Alpha')",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        // StartedAt is a fixed historical date, not now(): SyncRunRetentionTests also calls this
        // fixture and then seeds its own SyncRuns rows on the same project, asserting exact
        // counts and watermark-anchor selection by StartedAt recency. A "now()" row here would
        // either win the anchor race (displacing the row those tests expect to survive) or
        // inflate their "recent, untouched" counts. This date is older than any retention
        // cutoff exercised in tests, so it is always purge-eligible and never the newest.
        await Exec(conn, @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,@s,'Succeeded','Sync',10)",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId),
            ("s", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        return new GraphSeed(connId, projId);
    }

    internal static async Task<Guid> SeedRedeemedInviteCodeAsync(Guid redeemer)
    {
        var adminId = Guid.NewGuid();
        var codeId = Guid.NewGuid();
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""Users""(""Id"",""Email"",""Username"",""PasswordHash"",""Role"",""Status"",""CreatedAt"") VALUES (@i,@e,@u,'h','Admin','Active',now())",
            ("i", adminId), ("e", $"a-{adminId:N}@x.com"), ("u", $"a-{adminId:N}"));
        // Note: InviteCodes has no CreatedAt column (see InitialIdentity migration / model
        // snapshot) — omitted here, unlike the brief's literal text, to match the real schema.
        await Exec(conn, @"INSERT INTO ""InviteCodes""(""Id"",""Code"",""CreatedByAdminId"",""UsedByUserId"",""ExpiresAt"") VALUES (@i,@c,@a,@r,now() + interval '30 days')",
            ("i", codeId), ("c", $"CODE-{codeId:N}"), ("a", adminId), ("r", redeemer));
        return codeId;
    }

    /// <summary>Row counts per tenant-owned table for one tenant, read privileged for cross-tenant assertions.</summary>
    internal static async Task<IReadOnlyList<(string Table, int Count)>> CountsForTenantAsync(AppDbContext db, Guid tenant)
    {
        return new List<(string, int)>
        {
            ("JiraConnections",   await db.JiraConnections.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("JiraProjects",      await db.JiraProjects.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("Statuses",          await db.Statuses.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("IssueTypes",        await db.IssueTypes.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("JiraUsers",         await db.JiraUsers.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("StatusSemantics",   await db.StatusSemantics.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("Issues",            await db.Issues.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("IssueChangeHistory",await db.IssueChangeHistory.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("RawIssueSnapshots", await db.RawIssueSnapshots.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("RawChangelogs",     await db.RawChangelogs.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("JiraQueries",       await db.JiraQueries.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("FieldMappings",     await db.FieldMappings.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("Priorities",        await db.Priorities.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("Sprints",           await db.Sprints.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("Teams",             await db.Teams.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("SyncRuns",          await db.SyncRuns.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
        };
    }
}
