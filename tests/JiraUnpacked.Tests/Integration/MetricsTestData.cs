// tests/JiraUnpacked.Tests/Integration/MetricsTestData.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>Seeded project context: ids the tests need to add issues/history to.</summary>
internal sealed record SeedContext(
    Guid Tenant, Guid ConnectionId, Guid ProjectId,
    Guid ToDoStatusId, Guid InProgressStatusId, Guid DoneStatusId);

/// <summary>
/// Shared auth + raw-SQL seeding for the FR-E2 metrics tests. Seeding uses the superuser
/// connection (bypasses RLS) purely to build fixtures; every assertion reads through the API
/// as the owning tenant. NOTE: no WireMock anywhere — metrics must need zero Jira calls.
/// </summary>
internal static class MetricsTestData
{
    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    /// <summary>Creates an Active EndUser and logs in. The tenant EndUserId == user.Id.</summary>
    internal static async Task<(HttpClient client, Guid tenantId)> AuthedTenantAsync(WebApplicationFactory<Program> factory)
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
    /// Seeds connection + project + three statuses ("To Do", "In Progress", "Done") and the
    /// matching StatusSemantics (Other / Active / Done). Each call makes a FRESH project id,
    /// so parallel tests never collide.
    /// </summary>
    internal static async Task<SeedContext> SeedProjectAsync(Guid tenant)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var todo = Guid.NewGuid();
        var wip = Guid.NewGuid();
        var done = Guid.NewGuid();

        await using var conn = Conn();
        await conn.OpenAsync();

        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())",
            ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())",
            ("i", projId), ("t", tenant), ("c", connId));

        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'1','To Do','new')",
            ("i", todo), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'2','In Progress','indeterminate')",
            ("i", wip), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'3','Done','done')",
            ("i", done), ("t", tenant), ("c", connId));

        // StatusSemantics.Class is stored as a string (HasConversion<string>).
        await SeedSemanticAsync(conn, tenant, projId, "1", "To Do", "Other");
        await SeedSemanticAsync(conn, tenant, projId, "2", "In Progress", "Active");
        await SeedSemanticAsync(conn, tenant, projId, "3", "Done", "Done");

        return new SeedContext(tenant, connId, projId, todo, wip, done);
    }

    private static Task SeedSemanticAsync(
        NpgsqlConnection conn, Guid tenant, Guid projId, string jiraStatusId, string name, string cls) =>
        Exec(conn, @"INSERT INTO ""StatusSemantics""(""Id"",""EndUserId"",""JiraProjectId"",""JiraStatusId"",""StatusName"",""Class"") VALUES (@i,@t,@p,@sid,@n,@c)",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("sid", jiraStatusId), ("n", name), ("c", cls));

    /// <summary>Repoints a status name to a different class — the FR-E2 "mapping edit" lever.</summary>
    internal static async Task SetSemanticClassAsync(SeedContext ctx, string jiraStatusId, string cls)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"UPDATE ""StatusSemantics"" SET ""Class"" = @c WHERE ""JiraProjectId"" = @p AND ""JiraStatusId"" = @sid",
            ("c", cls), ("p", ctx.ProjectId), ("sid", jiraStatusId));
    }

    /// <summary>Seeds a project-scoped team and returns its id, for issues that belong to one.</summary>
    internal static async Task<Guid> SeedTeamAsync(SeedContext ctx, string name)
    {
        var id = Guid.NewGuid();
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""Teams""(""Id"",""EndUserId"",""JiraProjectId"",""JiraTeamId"",""Name"") VALUES (@i,@t,@p,@jt,@n)",
            ("i", id), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("jt", id.ToString("N")[..12]), ("n", name));
        return id;
    }

    internal static async Task SeedIssueAsync(
        SeedContext ctx, string jiraIssueId, string jiraKey, string? summary,
        Guid? statusId, decimal? storyPoints, DateTime createdAt, Guid? teamId = null)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""TeamId"",""StoryPoints"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,@jid,@k,@s,@st,@tm,@sp,@c,@c,now())",
            ("i", Guid.NewGuid()), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("jid", jiraIssueId),
            ("k", jiraKey), ("s", summary), ("st", statusId), ("tm", teamId), ("sp", storyPoints), ("c", createdAt));
    }

    /// <summary>
    /// Seeds one changelog entry for an arbitrary field. The derivation stores every field Jira
    /// reports under its own name — "Sprint", "assignee", "Story Points" — so field-churn
    /// fixtures need the field to be a parameter rather than hardcoded to status.
    /// </summary>
    internal static async Task SeedFieldChangeAsync(
        SeedContext ctx, string jiraIssueId, string historyId, string field,
        string? from, string? to, DateTime changedAt)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") VALUES (@i,@t,@p,@jid,@h,@fld,@f,@to,'Alice',@at)",
            ("i", Guid.NewGuid()), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("jid", jiraIssueId),
            ("h", historyId), ("fld", field), ("f", from), ("to", to), ("at", changedAt));
    }

    /// <summary>Seeds one status transition (Field = "status", as the derivation stores it).</summary>
    internal static async Task SeedStatusChangeAsync(
        SeedContext ctx, string jiraIssueId, string historyId, string? from, string? to, DateTime changedAt)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") VALUES (@i,@t,@p,@jid,@h,'status',@f,@to,'Alice',@at)",
            ("i", Guid.NewGuid()), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("jid", jiraIssueId),
            ("h", historyId), ("f", from), ("to", to), ("at", changedAt));
    }
}
