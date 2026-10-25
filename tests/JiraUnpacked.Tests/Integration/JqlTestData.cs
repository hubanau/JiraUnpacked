// tests/JiraUnpacked.Tests/Integration/JqlTestData.cs
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The rows the JQL lookup reads. Seeding uses the superuser connection (bypasses RLS) purely to
/// build fixtures; every assertion reads through the API as the owning tenant. <c>asTenant</c>
/// writes a row under ANOTHER tenant's id while keeping this project's connection and project id —
/// exactly the row a missing tenant filter would leak into the JQL.
/// </summary>
internal static class JqlTestData
{
    internal static async Task<Guid> SeedUserAsync(SeedContext ctx, string displayName, string accountId, Guid? asTenant = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync(@"INSERT INTO ""JiraUsers""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraAccountId"",""DisplayName"")
                    VALUES (@i,@t,@c,@a,@n)",
            ("i", id), ("t", asTenant ?? ctx.Tenant), ("c", ctx.ConnectionId), ("a", accountId), ("n", displayName));
        return id;
    }

    internal static async Task<Guid> SeedSprintAsync(SeedContext ctx, string name, string jiraSprintId, Guid? asTenant = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync(@"INSERT INTO ""Sprints""(""Id"",""EndUserId"",""JiraProjectId"",""JiraSprintId"",""Name"",""State"")
                    VALUES (@i,@t,@p,@s,@n,'active')",
            ("i", id), ("t", asTenant ?? ctx.Tenant), ("p", ctx.ProjectId), ("s", jiraSprintId), ("n", name));
        return id;
    }

    internal static async Task<Guid> SeedTeamAsync(SeedContext ctx, string name, string jiraTeamId, Guid? asTenant = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync(@"INSERT INTO ""Teams""(""Id"",""EndUserId"",""JiraProjectId"",""JiraTeamId"",""Name"")
                    VALUES (@i,@t,@p,@j,@n)",
            ("i", id), ("t", asTenant ?? ctx.Tenant), ("p", ctx.ProjectId), ("j", jiraTeamId), ("n", name));
        return id;
    }

    /// <summary><paramref name="role"/> as stored: "Team", "StoryPoints", …</summary>
    internal static Task SeedMappingAsync(SeedContext ctx, string role, string jiraFieldId, Guid? asTenant = null) =>
        ExecAsync(@"INSERT INTO ""FieldMappings""(""Id"",""EndUserId"",""JiraProjectId"",""Role"",""JiraFieldId"")
                    VALUES (@i,@t,@p,@r,@f)",
            ("i", Guid.NewGuid()), ("t", asTenant ?? ctx.Tenant), ("p", ctx.ProjectId), ("r", role), ("f", jiraFieldId));

    internal static Task SetBaseUrlAsync(SeedContext ctx, string baseUrl) =>
        ExecAsync(@"UPDATE ""JiraConnections"" SET ""BaseUrl"" = @u WHERE ""Id"" = @c",
            ("u", baseUrl), ("c", ctx.ConnectionId));

    /// <summary>One issue in this project, so the list's own filter has something to match (or not) by name.</summary>
    internal static Task SeedIssueAsync(
        SeedContext ctx, string key, Guid? assigneeId = null, Guid? sprintId = null, Guid? teamId = null) =>
        ExecAsync(@"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""AssigneeId"",""SprintId"",""TeamId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"")
                    VALUES (@i,@t,@p,@jid,@k,@a,@s,@tm,now(),now(),now())",
            ("i", Guid.NewGuid()), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("jid", key), ("k", key),
            ("a", assigneeId), ("s", sprintId), ("tm", teamId));

    private static async Task ExecAsync(string sql, params (string, object?)[] ps)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn, sql, ps);
    }
}
