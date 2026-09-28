// tests/JiraUnpacked.Tests/Integration/SmartSprintTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>Smart sprint values end to end: query, value list counts, and resolution at query time.</summary>
public sealed class SmartSprintTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public SmartSprintTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static object Smart(string key) => new { smart = key };
    private static object SprintIn(string op, params object?[] values) => new { field = "sprint", op, values };
    private static object SprintIs(string op, object value) => new { field = "sprint", op, value };

    private sealed record Seeded(HttpClient Client, SeedContext Ctx, Guid ActiveSprintId);

    /// <summary>S-A active, S-F future, S-C closed, S-N in a closed sprint NAMED "active", S-0 no sprint.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var active = await RicherFilterTestData.SeedSprintAsync(ctx, "Sprint 12", "active");
        var future = await RicherFilterTestData.SeedSprintAsync(ctx, "Sprint 13", "future");
        var closed = await RicherFilterTestData.SeedSprintAsync(ctx, "Sprint 11", "closed");
        var namedActive = await RicherFilterTestData.SeedSprintAsync(ctx, "active", "closed");
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-A", sprintId: active);
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-F", sprintId: future);
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-C", sprintId: closed);
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-N", sprintId: namedActive);
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-0");
        return new Seeded(client, ctx, active);
    }

    [Fact]
    public async Task Smart_values_resolve_from_sprint_state()
    {
        var s = await SeedAsync();
        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(s.Client, s.Ctx.ProjectId, c);

        Assert.Equal(new[] { "S-A" }, await Keys(SprintIn("In", Smart("active"))));
        Assert.Equal(new[] { "S-A", "S-F" }, await Keys(SprintIn("In", Smart("open"))));
        Assert.Equal(new[] { "S-C", "S-N" }, await Keys(SprintIn("In", Smart("closed"))));
        Assert.Equal(new[] { "S-F" }, await Keys(SprintIs("Eq", Smart("future"))));
        Assert.Equal(new[] { "S-A", "S-F" }, await Keys(SprintIn("NotIn", Smart("closed"))));
        Assert.Equal(new[] { "S-0", "S-A" }, await Keys(SprintIn("In", Smart("active"), null)));
    }

    [Fact]
    public async Task A_string_still_means_a_sprint_name()
    {
        var s = await SeedAsync();

        Assert.Equal(new[] { "S-N" }, await RicherFilterTestData.KeysAsync(s.Client, s.Ctx.ProjectId, SprintIn("In", "active")));
        Assert.Equal(new[] { "S-N" }, await RicherFilterTestData.KeysAsync(s.Client, s.Ctx.ProjectId, SprintIs("Eq", "active")));
    }

    [Fact]
    public async Task A_state_change_is_seen_without_editing_the_filter()
    {
        var s = await SeedAsync();
        await using (var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await conn.OpenAsync();
            await MetricsTestData.Exec(conn, @"UPDATE ""Sprints"" SET ""State"" = 'closed' WHERE ""Id"" = @i", ("i", s.ActiveSprintId));
        }

        // No active sprint any more: a real state, so an empty result — not an error.
        Assert.Empty(await RicherFilterTestData.KeysAsync(s.Client, s.Ctx.ProjectId, SprintIn("In", Smart("active"))));
    }

    [Fact]
    public async Task Smart_values_are_refused_off_sprint_and_when_unknown()
    {
        var s = await SeedAsync();

        Assert.Contains("only available on 'sprint'", await RicherFilterTestData.RejectionAsync(
            s.Client, s.Ctx.ProjectId, new { field = "status", op = "In", values = new[] { Smart("active") } }));
        Assert.Contains("'current'", await RicherFilterTestData.RejectionAsync(
            s.Client, s.Ctx.ProjectId, SprintIn("In", Smart("current"))));
    }

    [Fact]
    public async Task The_sprint_value_list_counts_each_smart_value_and_other_fields_have_none()
    {
        var s = await SeedAsync();

        var sprint = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Ctx.ProjectId}/filter-values?field=sprint");
        var smart = sprint.GetProperty("smart").EnumerateArray()
            .Select(x => (x.GetProperty("key").GetString(), x.GetProperty("label").GetString(), x.GetProperty("count").GetInt32()))
            .ToArray();
        Assert.Equal(new[]
        {
            ("active", "Active sprint", 1),
            ("future", "Future sprints", 1),
            ("open", "Open sprints (active + future)", 2),
            ("closed", "Closed sprints", 2),
        }, smart);

        var status = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Ctx.ProjectId}/filter-values?field=status");
        Assert.False(status.TryGetProperty("smart", out _));
    }
}
