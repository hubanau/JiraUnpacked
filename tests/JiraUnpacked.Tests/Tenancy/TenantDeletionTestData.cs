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
        };
    }
}
