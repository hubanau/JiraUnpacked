// tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// History conditions end to end: the issue query (and export) against seeded changelog rows,
/// through the API as the owning tenant, with the translator's clock pinned to a Wednesday noon.
/// </summary>
public sealed class HistoryConditionTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly JiraUnpackedApiFactory _factory;
    private WebApplicationFactory<Program> _app = null!;

    public HistoryConditionTests(JiraUnpackedApiFactory f) => _factory = f;

    public async Task InitializeAsync()
    {
        await _factory.EnsureDatabaseAsync();   // migrate BEFORE any host is built
        _app = RicherFilterTestData.WithFixedClock(_factory, Now);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static object History(string op, object value) => new { field = "history", op, value };

    private async Task<(HttpClient client, SeedContext ctx)> TenantAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_app);
        return (client, await MetricsTestData.SeedProjectAsync(tenant));
    }

    [Fact]
    public async Task Changed_matches_the_issues_whose_field_changed_and_runs_as_sql()
    {
        var (client, ctx) = await TenantAsync();
        await RicherFilterTestData.SeedIssueAsync(ctx, "H-1");
        await RicherFilterTestData.SeedIssueAsync(ctx, "H-2");
        await RicherFilterTestData.SeedIssueAsync(ctx, "H-3");
        await HistoryTestData.SeedChangeAsync(ctx, "H-1", "Sprint", null, "S1");
        await HistoryTestData.SeedChangeAsync(ctx, "H-2", "status", "To Do", "Done");
        await HistoryTestData.SeedChangeAsync(ctx, "H-3", "sprint", null, "S1");   // another field: names are exact

        Assert.Equal(new[] { "H-1" }, await RicherFilterTestData.KeysAsync(
            client, ctx.ProjectId, History("Changed", new { historyField = "Sprint" })));
    }

    [Fact]
    public async Task Another_tenants_history_never_matches()
    {
        var (client, ctx) = await TenantAsync();
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_app);
        await RicherFilterTestData.SeedIssueAsync(ctx, "T-1");
        // Same project id, same issue id — only the tenant differs.
        await HistoryTestData.SeedChangeAsync(ctx, "T-1", "Sprint", null, "S1", asTenant: other);

        Assert.Empty(await RicherFilterTestData.KeysAsync(
            client, ctx.ProjectId, History("Changed", new { historyField = "Sprint" })));
    }

    [Fact]
    public async Task A_bad_history_condition_is_a_400_naming_the_problem()
    {
        var (client, ctx) = await TenantAsync();
        Task<string> Title(params object[] c) => RicherFilterTestData.RejectionAsync(client, ctx.ProjectId, c);

        Assert.Contains("historyField", await Title(History("Changed", new { })));
        var six = Enumerable.Repeat(History("Changed", new { historyField = "Sprint" }), 6).ToArray();
        Assert.Equal("A filter may have at most 5 history conditions.", await Title(six));
    }

    [Fact]
    public async Task Was_matches_history_elements_and_the_current_value()
    {
        var (client, ctx) = await TenantAsync();
        var s101 = await RicherFilterTestData.SeedSprintAsync(ctx, "PLAT 101", "closed");
        await RicherFilterTestData.SeedIssueAsync(ctx, "W-1");                       // carried through PLAT 101
        await RicherFilterTestData.SeedIssueAsync(ctx, "W-2", sprintId: s101);       // in PLAT 101 now, never moved
        await RicherFilterTestData.SeedIssueAsync(ctx, "W-3");                       // only ever in PLAT 1010
        await RicherFilterTestData.SeedIssueAsync(ctx, "W-4", statusId: ctx.DoneStatusId);
        await HistoryTestData.SeedChangeAsync(ctx, "W-1", "Sprint", "PLAT 100, PLAT 101", "PLAT 102");
        await HistoryTestData.SeedChangeAsync(ctx, "W-3", "Sprint", null, "PLAT 1010");

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        Assert.Equal(new[] { "W-1", "W-2" }, await Keys(History("Was", new { historyField = "Sprint", values = new[] { "PLAT 101" } })));
        // Never changed status, but "was" Done because it is Done now.
        Assert.Equal(new[] { "W-4" }, await Keys(History("Was", new { historyField = "status", values = new[] { "Done" } })));
    }

    [Fact]
    public async Task Wildcards_in_a_value_are_literal_in_sql()
    {
        var (client, ctx) = await TenantAsync();
        var stored = new Dictionary<string, string>
        {
            ["L-1"] = "a, 100%_done, b",      // the real value, in the middle
            ["L-2"] = "100%_done, b",         // first
            ["L-3"] = "a, 100%_done",         // last
            ["L-4"] = "a, 100xyzdone, b",     // what an unescaped LIKE would also match
            ["L-5"] = "100xyzdone, b",
            ["L-6"] = "a, 100xyzdone",
        };
        foreach (var (key, value) in stored)
        {
            await RicherFilterTestData.SeedIssueAsync(ctx, key);
            await HistoryTestData.SeedChangeAsync(ctx, key, "Fix Version", null, value);
        }

        Assert.Equal(new[] { "L-1", "L-2", "L-3" }, await RicherFilterTestData.KeysAsync(client, ctx.ProjectId,
            History("Was", new { historyField = "Fix Version", values = new[] { "100%_done" } })));
    }

    [Fact]
    public async Task Changed_from_to_and_by_must_hold_on_one_change()
    {
        var (client, ctx) = await TenantAsync();
        foreach (var key in new[] { "P-1", "P-2", "P-3" }) await RicherFilterTestData.SeedIssueAsync(ctx, key);
        await HistoryTestData.SeedChangeAsync(ctx, "P-1", "status", "To Do", "Done", author: "Bob");
        await HistoryTestData.SeedChangeAsync(ctx, "P-2", "status", "To Do", "In Progress", author: "Bob");
        await HistoryTestData.SeedChangeAsync(ctx, "P-2", "status", "In Progress", "Done", author: "Alice");
        await HistoryTestData.SeedChangeAsync(ctx, "P-3", "assignee", null, "Bob");

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        Assert.Equal(new[] { "P-1", "P-2" }, await Keys(History("Changed", new { historyField = "status", to = new[] { "Done" } })));
        Assert.Equal(new[] { "P-1" }, await Keys(History("Changed", new { historyField = "status", to = new[] { "Done" }, by = new[] { "Bob" } })));
        Assert.Equal(new[] { "P-3" }, await Keys(History("Changed", new { historyField = "assignee", from = new string?[] { null } })));
    }

    [Fact]
    public async Task Bad_value_lists_are_400s()
    {
        var (client, ctx) = await TenantAsync();
        Task<string> Title(object c) => RicherFilterTestData.RejectionAsync(client, ctx.ProjectId, c);

        Assert.Contains("(Empty) is not one", await Title(History("Changed", new { historyField = "status", by = new string?[] { null } })));
        Assert.Contains("at most 100 values", await Title(History("Was", new
        {
            historyField = "status", values = Enumerable.Range(0, 101).Select(i => $"v{i}").ToArray(),
        })));
        Assert.Contains("'Was' needs 'values'", await Title(History("Was", new { historyField = "status" })));
    }

    [Fact]
    public async Task ChangeCount_and_windows_end_to_end_with_never_changed_issues_counting_zero()
    {
        var (client, ctx) = await TenantAsync();
        foreach (var key in new[] { "C-0", "C-1", "C-3" }) await RicherFilterTestData.SeedIssueAsync(ctx, key);
        await HistoryTestData.SeedChangeAsync(ctx, "C-1", "Sprint", null, "S1", at: Now.AddDays(-100));
        for (var n = 0; n < 3; n++)
            await HistoryTestData.SeedChangeAsync(ctx, "C-3", "Sprint", $"S{n}", $"S{n + 1}", at: Now.AddDays(-5 - n));

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        Assert.Equal(new[] { "C-3" }, await Keys(History("ChangeCount", new { historyField = "Sprint", compare = "Gte", count = 3 })));
        Assert.Equal(new[] { "C-0" }, await Keys(History("ChangeCount", new { historyField = "Sprint", compare = "Eq", count = 0 })));
        Assert.Equal(new[] { "C-0", "C-1" }, await Keys(History("ChangeCount", new
        {
            historyField = "Sprint", compare = "Eq", count = 0, during = new { kind = "inLast", amount = 30, unit = "days" },
        })));
        // This month in Warsaw starts Sep 30 22:00Z: C-3's Oct 1 and Oct 2 changes are in it, C-1's is not.
        Assert.Equal(new[] { "C-3" }, await Keys(History("Changed", new
        {
            historyField = "Sprint", during = new { kind = "inPeriod", period = "thisMonth", timeZone = "Europe/Warsaw" },
        })));
    }

    [Fact]
    public async Task Another_tenants_history_never_counts()
    {
        var (client, ctx) = await TenantAsync();
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_app);
        await RicherFilterTestData.SeedIssueAsync(ctx, "N-1");
        await HistoryTestData.SeedChangeAsync(ctx, "N-1", "Sprint", null, "S1");
        for (var i = 0; i < 4; i++)
            await HistoryTestData.SeedChangeAsync(ctx, "N-1", "Sprint", "S1", "S2", asTenant: other);

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        Assert.Equal(new[] { "N-1" }, await Keys(History("ChangeCount", new { historyField = "Sprint", compare = "Eq", count = 1 })));
        Assert.Empty(await Keys(History("ChangeCount", new { historyField = "Sprint", compare = "Gte", count = 2 })));
    }

    [Fact]
    public async Task Export_applies_each_history_operator_like_the_list()
    {
        var (client, ctx) = await TenantAsync();
        foreach (var key in new[] { "E-1", "E-2" }) await RicherFilterTestData.SeedIssueAsync(ctx, key);
        await HistoryTestData.SeedChangeAsync(ctx, "E-1", "status", "To Do", "Blocked");
        await HistoryTestData.SeedChangeAsync(ctx, "E-1", "status", "Blocked", "To Do");

        foreach (var condition in new[]
        {
            History("Was", new { historyField = "status", values = new[] { "Blocked" } }),
            History("Changed", new { historyField = "status", during = new { kind = "inLast", amount = 90, unit = "days" } }),
            History("ChangeCount", new { historyField = "status", compare = "Gte", count = 2 }),
        })
        {
            var filter = JsonSerializer.Serialize(new { logic = "And", conditions = new[] { condition } });
            var csv = await client.GetStringAsync(
                $"/api/projects/{ctx.ProjectId}/issues/export.csv?cols=jiraKey&filter={Uri.EscapeDataString(filter)}");
            Assert.Equal(new[] { "Key", "E-1" }, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        }
    }

    [Fact]
    public async Task Bad_counts_and_windows_are_400s()
    {
        var (client, ctx) = await TenantAsync();
        Task<string> Title(object c) => RicherFilterTestData.RejectionAsync(client, ctx.ProjectId, c);

        Assert.Contains("'compare' must be one of", await Title(History("ChangeCount", new { historyField = "Sprint", compare = "About", count = 2 })));
        Assert.Contains("from 0 to 10000", await Title(History("ChangeCount", new { historyField = "Sprint", compare = "Gte", count = 10001 })));
        Assert.Contains("Unknown time zone", await Title(History("Changed", new
        {
            historyField = "Sprint", during = new { kind = "inPeriod", period = "today", timeZone = "Nope/Nowhere" },
        })));
        Assert.Contains("'from' is after 'to'", await Title(History("Changed", new
        {
            historyField = "Sprint", during = new { kind = "between", from = "2026-09-30", to = "2026-09-01" },
        })));
    }

    [Fact]
    public async Task Was_empty_end_to_end_on_postgres_for_history_only_and_current_value_fields()
    {
        var (client, ctx) = await TenantAsync();
        foreach (var key in new[] { "Y-1", "Y-2", "Y-3" }) await RicherFilterTestData.SeedIssueAsync(ctx, key);
        await RicherFilterTestData.SeedIssueAsync(ctx, "Y-4", statusId: ctx.DoneStatusId);
        await HistoryTestData.SeedChangeAsync(ctx, "Y-1", "Team", null, "Red");    // NULL from
        await HistoryTestData.SeedChangeAsync(ctx, "Y-2", "Team", "", "Blue");     // '' from
        await HistoryTestData.SeedChangeAsync(ctx, "Y-3", "Team", "Red", "Blue");  // neither
        await HistoryTestData.SeedChangeAsync(ctx, "Y-4", "status", "To Do", "Blocked");

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        // A field the issue does not hold: history alone.
        Assert.Equal(new[] { "Y-1", "Y-2" }, await Keys(History("Was", new { historyField = "Team", values = new string?[] { null } })));
        // A field it does: no status NOW, or any status row that has/had none.
        Assert.Equal(new[] { "Y-1", "Y-2", "Y-3" }, await Keys(History("Was", new { historyField = "status", values = new string?[] { null } })));
        // Blocked or (Empty): Y-4 is Done now but was Blocked.
        Assert.Equal(new[] { "Y-1", "Y-2", "Y-3", "Y-4" },
            await Keys(History("Was", new { historyField = "status", values = new string?[] { "Blocked", null } })));
        Assert.Equal(new[] { "Y-1", "Y-2" }, await Keys(History("Changed", new { historyField = "Team", to = new[] { "Red", "Blue" }, from = new string?[] { null, "" } })));
    }

    [Fact]
    public async Task Labels_split_on_spaces_and_sprints_on_commas_in_sql()
    {
        var (client, ctx) = await TenantAsync();
        foreach (var key in new[] { "B-1", "B-2", "B-3", "B-4" }) await RicherFilterTestData.SeedIssueAsync(ctx, key);
        await HistoryTestData.SeedChangeAsync(ctx, "B-1", "labels", null, "DataOps ansible tested");
        await HistoryTestData.SeedChangeAsync(ctx, "B-2", "labels", null, "DataOps ansible2 tested");
        await HistoryTestData.SeedChangeAsync(ctx, "B-3", "Sprint", null, "PLAT 100, PLAT 101");
        await HistoryTestData.SeedChangeAsync(ctx, "B-4", "labels", "ansible", "DataOps");

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        Assert.Equal(new[] { "B-1", "B-4" }, await Keys(History("Was", new { historyField = "labels", values = new[] { "ansible" } })));
        Assert.Empty(await Keys(History("Was", new { historyField = "labels", values = new[] { "ans" } })));
        Assert.Equal(new[] { "B-3" }, await Keys(History("Was", new { historyField = "Sprint", values = new[] { "PLAT 101" } })));
        Assert.Empty(await Keys(History("Was", new { historyField = "Sprint", values = new[] { "101" } })));
    }
}
