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
            await HistoryTestData.SeedChangeAsync(ctx, key, "labels", null, value);
        }

        Assert.Equal(new[] { "L-1", "L-2", "L-3" }, await RicherFilterTestData.KeysAsync(client, ctx.ProjectId,
            History("Was", new { historyField = "labels", values = new[] { "100%_done" } })));
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
}
