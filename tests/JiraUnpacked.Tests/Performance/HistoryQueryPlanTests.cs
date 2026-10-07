using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit.Abstractions;

namespace JiraUnpacked.Tests.Performance;

/// <summary>
/// The history-conditions performance gate (spec §2): the whole-project list query — page 1,
/// count included — for each operator must finish under 2 s on the largest project.
///
/// Opt-in. Runs only when JU_PERF_DB holds a connection string, and refuses one whose session is
/// not read-only; without it the test returns at once, so the normal suite never touches a real
/// database. Every statement IssueQueryService sends is first run under
/// EXPLAIN (ANALYZE, BUFFERS) on the same connection, and the plans go to the test output.
/// The history pickers' reads (history-fields, history-values) are timed the same way and
/// recorded, but not gated: they run once per picker opening, not per list page.
/// </summary>
public sealed class HistoryQueryPlanTests
{
    private const double BudgetMs = 2000;
    private readonly ITestOutputHelper _out;

    public HistoryQueryPlanTests(ITestOutputHelper output) => _out = output;

    private sealed class FixedTenant : ICurrentTenant
    {
        public FixedTenant(Guid id) => EndUserId = id;
        public Guid? EndUserId { get; }
    }

    /// <summary>EXPLAIN ANALYZE of each command just before EF runs it; plans and execution times kept.</summary>
    private sealed class ExplainInterceptor : DbCommandInterceptor
    {
        public readonly List<(string Sql, IReadOnlyList<string> Plan, double Ms)> Statements = new();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await ExplainAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            await ExplainAsync(command, cancellationToken);
            return result;
        }

        private async Task ExplainAsync(DbCommand command, CancellationToken ct)
        {
            await using var explain = command.Connection!.CreateCommand();
            explain.Transaction = command.Transaction;
            explain.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + command.CommandText;
            foreach (NpgsqlParameter p in command.Parameters) explain.Parameters.Add(p.Clone());
            var plan = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) plan.Add(reader.GetString(0));
            var ms = plan
                .Select(l => Regex.Match(l, @"Execution Time: ([\d.]+) ms"))
                .Where(m => m.Success)
                .Sum(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            Statements.Add((command.CommandText, plan, ms));
        }
    }

    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    [Fact]
    public async Task Every_history_operator_lists_the_largest_project_under_two_seconds()
    {
        var connectionString = Environment.GetEnvironmentVariable("JU_PERF_DB");
        if (string.IsNullOrEmpty(connectionString))
        {
            _out.WriteLine("JU_PERF_DB is not set: the performance gate did not run.");
            return;
        }

        // ---- the read-only guard, and what to measure, on a plain connection ----
        Guid tenant, project;
        string status, sprint, author;
        string[] manySprints;
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            async Task<object?> Scalar(string sql, Guid? p = null)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                if (p is { } id) cmd.Parameters.AddWithValue("p", id);
                return await cmd.ExecuteScalarAsync();
            }

            // Never a write, by construction: refuse a session that could make one.
            Assert.Equal("on", (string?)await Scalar("SHOW default_transaction_read_only"));
            _out.WriteLine($"Connected as {await Scalar("SELECT current_user")} (read-only).");
            _out.WriteLine($"Running syncs: {await Scalar(@"SELECT count(*) FROM ""SyncRuns"" WHERE ""Status"" = 'Running'")}.");

            await using (var cmd = new NpgsqlCommand(
                @"SELECT ""EndUserId"", ""JiraProjectId"", count(*) FROM ""Issues"" GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 1", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                Assert.True(await r.ReadAsync(), "The database has no issues.");
                tenant = r.GetGuid(0);
                project = r.GetGuid(1);
                _out.WriteLine($"Largest project {project}: {r.GetInt64(2)} issues.");
            }
            _out.WriteLine($"History rows: {await Scalar(@"SELECT count(*) FROM ""IssueChangeHistory"" WHERE ""JiraProjectId"" = @p", project)}.");

            status = (string?)await Scalar(@"SELECT ""ToValue"" FROM ""IssueChangeHistory"" WHERE ""JiraProjectId"" = @p
                AND ""Field"" = 'status' AND ""ToValue"" IS NOT NULL GROUP BY 1 ORDER BY count(*) DESC LIMIT 1", project) ?? "Done";
            var multi = (string?)await Scalar(@"SELECT ""ToValue"" FROM ""IssueChangeHistory"" WHERE ""JiraProjectId"" = @p
                AND ""Field"" = 'Sprint' AND ""ToValue"" LIKE '%, %' GROUP BY 1 ORDER BY count(*) DESC LIMIT 1", project);
            sprint = multi?.Split(", ").Last() ?? "Sprint 1";
            author = (string?)await Scalar(@"SELECT ""Author"" FROM ""IssueChangeHistory"" WHERE ""JiraProjectId"" = @p
                AND ""Field"" = 'Sprint' AND ""Author"" IS NOT NULL GROUP BY 1 ORDER BY count(*) DESC LIMIT 1", project) ?? "nobody";

            // 50 sprint names, the most-used elements of the Sprint lists: the widest Was a picker builds.
            var names = new List<string>();
            await using (var cmd = new NpgsqlCommand(@"SELECT e FROM ""IssueChangeHistory"" h,
                    unnest(string_to_array(h.""ToValue"", ', ')) AS e
                WHERE h.""JiraProjectId"" = @p AND h.""Field"" = 'Sprint' AND e <> ''
                GROUP BY e ORDER BY count(*) DESC, e LIMIT 50", conn))
            {
                cmd.Parameters.AddWithValue("p", project);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) names.Add(r.GetString(0));
            }
            manySprints = names.Count > 0 ? names.ToArray() : new[] { sprint };
            _out.WriteLine($"Measuring with status '{status}', sprint '{sprint}', author '{author}', {manySprints.Length} sprints for the wide Was.");
        }

        AppDbContext NewContext(ExplainInterceptor explain) => new(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).AddInterceptors(explain).Options,
            new FixedTenant(tenant));

        var cases = new (string Name, FilterOperator Op, object Value)[]
        {
            ("Was status (current value too)", FilterOperator.Was,
                new { historyField = "status", values = new[] { status } }),
            ("Was Sprint (list elements)", FilterOperator.Was,
                new { historyField = "Sprint", values = new[] { sprint } }),
            ($"Was Sprint, {manySprints.Length} values", FilterOperator.Was,
                new { historyField = "Sprint", values = manySprints }),
            ("Changed Sprint", FilterOperator.Changed,
                new { historyField = "Sprint" }),
            ("Changed status to X by Y in the last 90 days", FilterOperator.Changed,
                new { historyField = "status", to = new[] { status }, by = new[] { author }, during = new { kind = "inLast", amount = 90, unit = "days" } }),
            ("ChangeCount Sprint >= 3", FilterOperator.ChangeCount,
                new { historyField = "Sprint", compare = "Gte", count = 3 }),
            ("ChangeCount Sprint = 0 in the last 90 days", FilterOperator.ChangeCount,
                new { historyField = "Sprint", compare = "Eq", count = 0, during = new { kind = "inLast", amount = 90, unit = "days" } }),
            ("ChangeCount status >= 2 in the last 365 days", FilterOperator.ChangeCount,
                new { historyField = "status", compare = "Gte", count = 2, during = new { kind = "inLast", amount = 365, unit = "days" } }),
        };

        var failures = new List<string>();
        foreach (var (name, op, value) in cases)
        {
            var explain = new ExplainInterceptor();
            await using var db = NewContext(explain);
            var service = new IssueQueryService(db,
                new IssueFilterCompiler(db, new IssueFilterTranslator(new FakeClock(DateTime.UtcNow))));

            var filter = new FilterGroup(FilterLogic.And, new List<FilterCondition> { new("history", op, J(value), null) }, null);
            var result = await service.QueryAsync(project, new IssueFilterRequest(null, filter, 1, 50));
            Assert.Null(result.Error);

            // The whole request: the project check, any name resolution, the count and page 1.
            var total = explain.Statements.Sum(s => s.Ms);
            _out.WriteLine($"== {name}: {result.Result!.TotalCount} matches, {total:F0} ms over {explain.Statements.Count} statements");
            foreach (var s in explain.Statements.Where(s => s.Sql.Contains("IssueChangeHistory")))
                foreach (var line in s.Plan) _out.WriteLine(line);
            if (total >= BudgetMs) failures.Add($"{name}: {total:F0} ms");
        }

        // ---- the pickers: recorded, not gated ----
        var pickers = new (string Name, Func<HistoryValueService, Task<int>> Run)[]
        {
            ("history-fields", async s => (await s.GetFieldsAsync(project)).Fields!.Count),
            ("history-values status", async s => (await s.GetValuesAsync(project, "status", "value")).Values!.Count),
            ("history-values Sprint", async s => (await s.GetValuesAsync(project, "Sprint", "value")).Values!.Count),
            ("history-values description", async s => (await s.GetValuesAsync(project, "description", "value")).Values!.Count),
            ("history-values Sprint authors", async s => (await s.GetValuesAsync(project, "Sprint", "author")).Values!.Count),
        };
        var slowPickers = new List<string>();
        foreach (var (name, run) in pickers)
        {
            var explain = new ExplainInterceptor();
            await using var db = NewContext(explain);
            var count = await run(new HistoryValueService(db));
            var total = explain.Statements.Sum(s => s.Ms);
            _out.WriteLine($"== picker {name}: {count} entries, {total:F0} ms over {explain.Statements.Count} statements");
            foreach (var s in explain.Statements.Where(s => s.Sql.Contains("IssueChangeHistory")))
                foreach (var line in s.Plan) _out.WriteLine(line);
            if (total >= BudgetMs) slowPickers.Add($"{name}: {total:F0} ms");
        }
        if (slowPickers.Count > 0) _out.WriteLine("NOTE (not gated): pickers over 2 s: " + string.Join("; ", slowPickers));

        Assert.True(failures.Count == 0, "Over the 2 s budget: " + string.Join("; ", failures));
    }
}
