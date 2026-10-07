// tests/JiraUnpacked.Tests/Integration/RelativeDateQueryTests.cs
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Relative dates end to end: query and export resolve them on the server, against the server's
/// clock — pinned here to a Wednesday noon — so every expectation is exact.
/// </summary>
public sealed class RelativeDateQueryTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly JiraUnpackedApiFactory _factory;
    private WebApplicationFactory<Program> _app = null!;

    public RelativeDateQueryTests(JiraUnpackedApiFactory f) => _factory = f;

    public async Task InitializeAsync()
    {
        await _factory.EnsureDatabaseAsync();   // migrate BEFORE any host is built
        _app = RicherFilterTestData.WithFixedClock(_factory, Now);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static object InLast(string op, int amount, string unit) =>
        new { field = "updatedAt", op, value = new { amount, unit } };

    private static object InPeriod(string period, string? timeZone = null) =>
        timeZone is null
            ? new { field = "updatedAt", op = "InPeriod", value = (object)new { period } }
            : new { field = "updatedAt", op = "InPeriod", value = (object)new { period, timeZone } };

    private static object Between(object? from, object? to) =>
        new { field = "updatedAt", op = "Between", values = new[] { from, to } };

    /// <summary>R-1 yesterday, R-2 on Aug 20 (noon UTC), R-3 in 2025.</summary>
    private async Task<(HttpClient client, SeedContext ctx)> SeedAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_app);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await RicherFilterTestData.SeedIssueAsync(ctx, "R-1", Now.AddDays(-1));
        await RicherFilterTestData.SeedIssueAsync(ctx, "R-2", Now.AddDays(-34));
        await RicherFilterTestData.SeedIssueAsync(ctx, "R-3", Now.AddDays(-400));
        return (client, ctx);
    }

    [Fact]
    public async Task Relative_conditions_are_resolved_against_the_servers_clock()
    {
        var (client, ctx) = await SeedAsync();
        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);

        Assert.Equal(new[] { "R-1" }, await Keys(InLast("InLast", 14, "days")));
        Assert.Equal(new[] { "R-2", "R-3" }, await Keys(InLast("NotInLast", 14, "days")));
        Assert.Equal(new[] { "R-1" }, await Keys(InPeriod("thisMonth")));
        Assert.Equal(new[] { "R-2" }, await Keys(InPeriod("lastMonth", "Europe/Warsaw")));
        // A date-only end includes the whole of Aug 20, where R-2 sits at noon.
        Assert.Equal(new[] { "R-2" }, await Keys(Between("2026-08-01", "2026-08-20")));
        Assert.Equal(new[] { "R-2", "R-3" }, await Keys(Between(null, "2026-08-20")));
    }

    [Fact]
    public async Task A_bad_relative_value_is_a_400_naming_the_problem()
    {
        var (client, ctx) = await SeedAsync();
        Task<string> Title(object c) => RicherFilterTestData.RejectionAsync(client, ctx.ProjectId, c);

        Assert.Contains("Nope/Nowhere", await Title(InPeriod("today", "Nope/Nowhere")));
        Assert.Contains("Unknown time zone", await Title(InPeriod("today", "../../etc/passwd")));
        Assert.Contains("amount", await Title(InLast("InLast", 0, "days")));
        Assert.Contains("at least one bound", await Title(Between(null, null)));
        Assert.Contains("'from' is after 'to'", await Title(Between("2026-09-30", "2026-09-01")));
    }

    [Fact]
    public async Task Export_resolves_relative_conditions_with_the_same_clock()
    {
        var (client, ctx) = await SeedAsync();
        var filter = JsonSerializer.Serialize(new
        {
            logic = "And",
            conditions = new[] { new { field = "updatedAt", op = "InLast", value = new { amount = 14, unit = "days" } } },
        });

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?cols=jiraKey&filter={Uri.EscapeDataString(filter)}");

        Assert.Equal(new[] { "Key", "R-1" }, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }
}
