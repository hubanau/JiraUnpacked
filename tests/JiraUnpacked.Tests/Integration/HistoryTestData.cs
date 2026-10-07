// tests/JiraUnpacked.Tests/Integration/HistoryTestData.cs
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Changelog fixtures for the history-condition tests. Seeding uses the superuser connection
/// (bypasses RLS) purely to build fixtures; every assertion reads through the API as the
/// owning tenant, and every test works in a fresh project.
/// </summary>
internal static class HistoryTestData
{
    private static NpgsqlConnection Conn() => new(JiraUnpackedApiFactory.TestConnectionString);

    /// <summary>
    /// One changelog row. <paramref name="asTenant"/> writes it under ANOTHER tenant's id while
    /// keeping this project's id and the issue id — exactly the row a missing tenant filter
    /// inside the subquery would leak.
    /// </summary>
    internal static async Task SeedChangeAsync(
        SeedContext ctx, string jiraIssueId, string field, string? from, string? to,
        string? author = "Alice", DateTime? at = null, Guid? asTenant = null)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"")
              VALUES (@i,@t,@p,@jid,@h,@fld,@f,@to,@a,@at)",
            ("i", Guid.NewGuid()), ("t", asTenant ?? ctx.Tenant), ("p", ctx.ProjectId), ("jid", jiraIssueId),
            ("h", Guid.NewGuid().ToString("N")[..16]), ("fld", field), ("f", from), ("to", to), ("a", author),
            ("at", at ?? new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary><paramref name="count"/> distinct fields ("F0001"…), one row each, in one statement.</summary>
    internal static Task SeedManyFieldsAsync(SeedContext ctx, int count) => BulkAsync(ctx,
        @"SELECT gen_random_uuid(), @t, @p, 'BULK', 'f-' || g, 'F' || lpad(g::text, 4, '0'), NULL, 'x', 'Alice', now()
          FROM generate_series(1, @n) AS g",
        ("n", count));

    /// <summary><paramref name="count"/> rows of one field with distinct ToValues ("V0001"…) and no FromValue.</summary>
    internal static Task SeedManyValuesAsync(SeedContext ctx, string field, int count) => BulkAsync(ctx,
        @"SELECT gen_random_uuid(), @t, @p, 'BULK', 'v-' || g, @field, NULL, 'V' || lpad(g::text, 4, '0'), 'Alice', now()
          FROM generate_series(1, @n) AS g",
        ("n", count), ("field", field));

    private static async Task BulkAsync(SeedContext ctx, string select, params (string, object?)[] ps)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") "
            + select,
            ps.Append(("t", (object?)ctx.Tenant)).Append(("p", (object?)ctx.ProjectId)).ToArray());
    }
}
