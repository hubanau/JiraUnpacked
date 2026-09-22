// tests/JiraUnpacked.Tests/Integration/LifecycleTestData.cs
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

internal sealed record SeededConnection(Guid Tenant, Guid ConnectionId, Guid[] ProjectIds);

/// <summary>
/// Seeds connections and projects carrying a row in EVERY project- and connection-scoped table,
/// and counts them back with a privileged read — so a delete test can prove it removed exactly
/// what it should and nothing else. Fresh ids every call; counts are always scoped by the seeded
/// project or connection id, never by table totals, because the test database is shared and
/// never truncated.
/// </summary>
internal static class LifecycleTestData
{
    /// <summary>Tables keyed by JiraProjectId (JiraProjects itself is counted by Id).</summary>
    internal static readonly string[] ProjectTables =
    [
        "Issues", "IssueChangeHistory", "RawIssueSnapshots", "RawChangelog", "SyncRuns", "SavedFilters",
        "JiraQueries", "FieldMappings", "StatusSemantics", "Sprints", "Teams"
    ];

    /// <summary>Connection-scoped dictionaries, keyed by JiraConnectionId.</summary>
    internal static readonly string[] ConnectionTables = ["Statuses", "IssueTypes", "Priorities", "JiraUsers"];

    private static NpgsqlConnection Conn() => new(JiraUnpackedApiFactory.TestConnectionString);

    internal static async Task<SeededConnection> SeedConnectionAsync(Guid tenant, int projects = 2)
    {
        var connId = Guid.NewGuid();
        var statusId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var priorityId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var conn = Conn();
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x.atlassian.net','e@x.com','c',now())",
            ("i", connId), ("t", tenant));
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'1','To Do','new')",
            ("i", statusId), ("t", tenant), ("c", connId));
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""IssueTypes""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraIssueTypeId"",""Name"",""Class"") VALUES (@i,@t,@c,'1','Story','Other')",
            ("i", typeId), ("t", tenant), ("c", connId));
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""Priorities""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraPriorityId"",""Name"") VALUES (@i,@t,@c,'1','High')",
            ("i", priorityId), ("t", tenant), ("c", connId));
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""JiraUsers""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraAccountId"",""DisplayName"") VALUES (@i,@t,@c,'u1','Alice')",
            ("i", userId), ("t", tenant), ("c", connId));

        var ids = new Guid[projects];
        for (var n = 0; n < projects; n++)
        {
            var projId = ids[n] = Guid.NewGuid();
            var sprintId = Guid.NewGuid();
            var teamId = Guid.NewGuid();
            var parentId = Guid.NewGuid();

            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,@k,@j,60,now())",
                ("i", projId), ("t", tenant), ("c", connId), ("k", $"P{n}"), ("j", $"{1000 + n}"));
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""Sprints""(""Id"",""EndUserId"",""JiraProjectId"",""JiraSprintId"",""Name"",""State"") VALUES (@i,@t,@p,'1','Sprint 1','active')",
                ("i", sprintId), ("t", tenant), ("p", projId));
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""Teams""(""Id"",""EndUserId"",""JiraProjectId"",""JiraTeamId"",""Name"") VALUES (@i,@t,@p,'1','Team Alpha')",
                ("i", teamId), ("t", tenant), ("p", projId));

            // A parent and a child linked through the RESTRICT self-FK, each referencing every
            // dictionary — the shape that makes a naive delete fail.
            const string issueSql =
                @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""IssueTypeId"",""PriorityId"",""AssigneeId"",""SprintId"",""TeamId"",""ParentJiraIssueId"",""ParentIssueId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"")
                  VALUES (@i,@t,@p,@ji,@k,'s',@st,@ty,@pr,@as,@sp,@tm,@pji,@pid,now(),now(),now())";
            await MetricsTestData.Exec(conn, issueSql,
                ("i", parentId), ("t", tenant), ("p", projId), ("ji", "10001"), ("k", $"P{n}-1"),
                ("st", statusId), ("ty", typeId), ("pr", priorityId), ("as", userId), ("sp", sprintId), ("tm", teamId),
                ("pji", null), ("pid", null));
            await MetricsTestData.Exec(conn, issueSql,
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("ji", "10002"), ("k", $"P{n}-2"),
                ("st", statusId), ("ty", typeId), ("pr", priorityId), ("as", userId), ("sp", sprintId), ("tm", teamId),
                ("pji", "10001"), ("pid", parentId));

            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") VALUES (@i,@t,@p,'10001','h1','status','To Do','Done','Alice',now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001','{}'::jsonb,now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""RawChangelog""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001','{}'::jsonb,now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""JiraQueries""(""Id"",""EndUserId"",""JiraProjectId"",""Jql"",""CreatedAt"") VALUES (@i,@t,@p,'project = P',now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""FieldMappings""(""Id"",""EndUserId"",""JiraProjectId"",""Role"",""JiraFieldId"") VALUES (@i,@t,@p,'Assignee','assignee')",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""StatusSemantics""(""Id"",""EndUserId"",""JiraProjectId"",""JiraStatusId"",""StatusName"",""Class"") VALUES (@i,@t,@p,'1','To Do','Other')",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""FinishedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,now(),now(),'Succeeded','Sync',2)",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
            await MetricsTestData.Exec(conn,
                @"INSERT INTO ""SavedFilters""(""Id"",""EndUserId"",""JiraProjectId"",""Name"",""DefinitionJson"",""CreatedAt"",""UpdatedAt"") VALUES (@i,@t,@p,'Mine','{}'::jsonb,now(),now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        }

        return new SeededConnection(tenant, connId, ids);
    }

    /// <summary>Row counts for one project, read as the owner (bypasses RLS), scoped by its id.</summary>
    internal static async Task<Dictionary<string, long>> ProjectCountsAsync(Guid projectId)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        var counts = new Dictionary<string, long>
        {
            ["JiraProjects"] = await CountAsync(conn, "JiraProjects", "Id", projectId)
        };
        foreach (var table in ProjectTables)
            counts[table] = await CountAsync(conn, table, "JiraProjectId", projectId);
        return counts;
    }

    /// <summary>Row counts for the connection row and its dictionaries, scoped by its id.</summary>
    internal static async Task<Dictionary<string, long>> ConnectionCountsAsync(Guid connectionId)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        var counts = new Dictionary<string, long>
        {
            ["JiraConnections"] = await CountAsync(conn, "JiraConnections", "Id", connectionId)
        };
        foreach (var table in ConnectionTables)
            counts[table] = await CountAsync(conn, table, "JiraConnectionId", connectionId);
        return counts;
    }

    internal static async Task SeedRunningRunAsync(Guid tenant, Guid projectId) =>
        await SyncCancelTestData.SeedRunningRunAsync(tenant, projectId);

    internal static async Task<string> StoredTokenCiphertextAsync(Guid connectionId)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""ApiTokenEncrypted"" FROM ""JiraConnections"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", connectionId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountAsync(NpgsqlConnection conn, string table, string column, Guid id)
    {
        // Table and column names come from the fixed lists above, never from input.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT COUNT(*) FROM ""{table}"" WHERE ""{column}"" = @id";
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
