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
}
