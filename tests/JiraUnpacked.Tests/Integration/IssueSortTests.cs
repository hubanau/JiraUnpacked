// tests/JiraUnpacked.Tests/Integration/IssueSortTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Sorting the issue list. The name arrives from a URL, so the interesting properties are that an
/// unknown one is refused rather than ignored, and that every sort is a TOTAL order.
/// </summary>
public sealed class IssueSortTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueSortTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_unknown_sort_name_is_refused_rather_than_quietly_ignored()
    {
        // A silent fallback to the default would show plausible rows in the wrong order, and
        // nothing on screen would say the sort had not happened.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{ctx.ProjectId}/issues/query",
            new { sortBy = "; DROP TABLE \"Issues\"--" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Sorts_by_the_named_column_in_both_directions()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await Seed(ctx, "A-1", 1);
        await Seed(ctx, "A-2", 5);
        await Seed(ctx, "A-3", 3);

        var asc = await PointsAsync(client, ctx.ProjectId, descending: false);
        var desc = await PointsAsync(client, ctx.ProjectId, descending: true);

        Assert.Equal(new decimal?[] { 1, 3, 5 }, asc);
        Assert.Equal(new decimal?[] { 5, 3, 1 }, desc);
    }

    [Fact]
    public async Task Sorts_by_a_dictionary_display_name_not_its_surrogate_id()
    {
        // Ordering by StatusId would order by a random GUID: plausible-looking, and wrong in a
        // way nobody would notice from one page.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await Seed(ctx, "A-1", 1, ctx.InProgressStatusId);  // "In Progress"
        await Seed(ctx, "A-2", 2, ctx.DoneStatusId);        // "Done"
        await Seed(ctx, "A-3", 3, ctx.ToDoStatusId);        // "To Do"

        var page = await QueryAsync(client, ctx.ProjectId, new { sortBy = "status", sortDescending = false });

        Assert.Equal(new[] { "Done", "In Progress", "To Do" }, page.Items.Select(i => i.Status));
    }

    [Fact]
    public async Task Ties_are_broken_by_key_so_pages_do_not_overlap()
    {
        // Without a total order Postgres may place a tied row on two pages and another on none.
        // The reader sees a duplicate and a silent omission, and no error anywhere.
        //
        // Seeded in DESCENDING key order on purpose. Every row ties on storyPoints, so with no
        // tiebreaker Postgres hands them back in physical order -- which, if they were inserted
        // ascending, would coincidentally equal key order and the test would pass against code
        // that has no tiebreaker at all. It did, until this seeded backwards.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        for (var i = 9; i >= 0; i--) await Seed(ctx, $"TIE-{i:D2}", 3);

        var first = await KeysAsync(client, ctx.ProjectId, page: 1);
        var second = await KeysAsync(client, ctx.ProjectId, page: 2);

        Assert.Equal(new[] { "TIE-00", "TIE-01", "TIE-02", "TIE-03", "TIE-04" }, first);
        Assert.Equal(new[] { "TIE-05", "TIE-06", "TIE-07", "TIE-08", "TIE-09" }, second);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public async Task Defaults_to_newest_updated_first_when_no_sort_is_asked_for()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(ctx, "1", "OLD-1", null, null, null, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await MetricsTestData.SeedIssueAsync(ctx, "2", "NEW-1", null, null, null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var page = await QueryAsync(client, ctx.ProjectId, new { });

        Assert.Equal("NEW-1", page.Items.First().JiraKey);
    }

    private static Task Seed(SeedContext ctx, string key, decimal points, Guid? statusId = null) =>
        MetricsTestData.SeedIssueAsync(
            ctx, Guid.NewGuid().ToString("N")[..10], key, key, statusId, points,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private static async Task<PagedResult<IssueListItem>> QueryAsync(HttpClient client, Guid projectId, object body)
    {
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/query", body);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PagedResult<IssueListItem>>())!;
    }

    private static async Task<decimal?[]> PointsAsync(HttpClient client, Guid projectId, bool descending)
    {
        var page = await QueryAsync(client, projectId, new { sortBy = "storyPoints", sortDescending = descending });
        return page.Items.Select(i => i.StoryPoints).ToArray();
    }

    private static async Task<string[]> KeysAsync(HttpClient client, Guid projectId, int page)
    {
        var result = await QueryAsync(client, projectId,
            new { sortBy = "storyPoints", sortDescending = true, page, pageSize = 5 });
        return result.Items.Select(i => i.JiraKey).ToArray();
    }
}
