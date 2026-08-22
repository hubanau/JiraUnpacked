// tests/JiraUnpacked.Tests/Integration/FilteredMetricsTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// POST api/projects/{projectId}/metrics/* — the curated metrics over a filtered population.
///
/// The filter selects which issues COUNT, not which numbers survive. Every case asserts exact
/// seeded numbers and pins the unfiltered report alongside, so a filter that silently did
/// nothing would fail rather than look like agreement.
/// </summary>
public sealed class FilteredMetricsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public FilteredMetricsTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static DateTime D(int day) => new(2024, 3, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Two issues that both completed, and one that never started:
    ///   ACME-1  "alpha work"  Done   3 pts  created Mar 1, active Mar 2, done Mar 4  (2 days)
    ///   ACME-2  "beta work"   Done   5 pts  created Mar 1, active Mar 2, done Mar 8  (6 days)
    ///   ACME-3  "alpha idle"  To Do  1 pt   created Mar 1, never moved
    /// A summary filter of "alpha" selects ACME-1 and ACME-3 — one completed, one not — so
    /// every metric changes in a different, checkable direction.
    /// </summary>
    private async Task<(HttpClient client, SeedContext ctx)> SeedAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        await MetricsTestData.SeedIssueAsync(ctx, "1", "ACME-1", "alpha work", ctx.DoneStatusId, 3m, D(1));
        await MetricsTestData.SeedIssueAsync(ctx, "2", "ACME-2", "beta work", ctx.DoneStatusId, 5m, D(1));
        await MetricsTestData.SeedIssueAsync(ctx, "3", "ACME-3", "alpha idle", ctx.ToDoStatusId, 1m, D(1));

        await MetricsTestData.SeedStatusChangeAsync(ctx, "1", "h1", "To Do", "In Progress", D(2));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "1", "h2", "In Progress", "Done", D(4));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "2", "h3", "To Do", "In Progress", D(2));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "2", "h4", "In Progress", "Done", D(8));

        return (client, ctx);
    }

    private static object Summary(string contains) =>
        new { conditions = new[] { new { field = "summary", op = "Contains", value = contains } } };

    private static async Task<T> PostAsync<T>(HttpClient client, Guid projectId, string metric, object body)
    {
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/metrics/{metric}", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, Guid projectId, string metric)
    {
        var resp = await client.GetAsync($"/api/projects/{projectId}/metrics/{metric}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Story_counts_totals_describe_the_filtered_population()
    {
        var (client, ctx) = await SeedAsync();

        var all = await GetAsync<StoryCountsReport>(client, ctx.ProjectId, "story-counts");
        Assert.Equal(3, all.TotalIssues);
        Assert.Equal(9m, all.TotalStoryPoints);

        var alpha = await PostAsync<StoryCountsReport>(
            client, ctx.ProjectId, "story-counts", Summary("alpha"));

        // ACME-1 (3 pts, Done) + ACME-3 (1 pt, To Do). The headline totals must describe the
        // same population as the bars, or a reader comparing them by eye gets a wrong sum.
        Assert.Equal(2, alpha.TotalIssues);
        Assert.Equal(4m, alpha.TotalStoryPoints);
        Assert.Equal(2, alpha.ByStatus.Count);
        Assert.Equal(1, alpha.ByStatus.Single(s => s.Status == "Done").Count);
        Assert.Equal(1, alpha.ByStatus.Single(s => s.Status == "To Do").Count);
    }

    [Fact]
    public async Task Time_in_progress_averages_only_the_filtered_completions()
    {
        var (client, ctx) = await SeedAsync();

        // Unfiltered: (2 days + 6 days) / 2 = 4.
        var all = await GetAsync<TimeInProgressReport>(client, ctx.ProjectId, "time-in-progress");
        Assert.Equal(2, all.CompletedIssuesConsidered);
        Assert.Equal(4d, all.AverageDays!.Value, 3);

        // "alpha" keeps only ACME-1 (2 days); ACME-3 never started and is excluded as before.
        var alpha = await PostAsync<TimeInProgressReport>(
            client, ctx.ProjectId, "time-in-progress", Summary("alpha"));
        Assert.Equal(1, alpha.CompletedIssuesConsidered);
        Assert.Equal(2d, alpha.AverageDays!.Value, 3);
        Assert.Equal(1, alpha.IssuesExcluded);
    }

    [Fact]
    public async Task Average_days_is_null_not_zero_when_the_filter_excludes_every_completion()
    {
        var (client, ctx) = await SeedAsync();

        // "idle" matches only ACME-3, which never completed.
        var report = await PostAsync<TimeInProgressReport>(
            client, ctx.ProjectId, "time-in-progress", Summary("idle"));

        // The whole reason the API returns null here: rendering 0 would tell a team their cycle
        // time is instant. A filter that empties the population must not change that answer.
        Assert.Null(report.AverageDays);
        Assert.Equal(0, report.CompletedIssuesConsidered);
        Assert.Equal(1, report.IssuesExcluded);
    }

    [Fact]
    public async Task Throughput_counts_only_the_filtered_completions_in_each_bucket()
    {
        var (client, ctx) = await SeedAsync();

        var window = new { bucket = "day", from = D(1), to = D(10) };

        var all = await PostAsync<ThroughputReport>(client, ctx.ProjectId, "throughput", window);
        Assert.Equal(2, all.Buckets.Sum(b => b.Completed));
        Assert.Equal(1, all.Buckets.Single(b => b.PeriodStart == D(4)).Completed);
        Assert.Equal(1, all.Buckets.Single(b => b.PeriodStart == D(8)).Completed);

        var alpha = await PostAsync<ThroughputReport>(client, ctx.ProjectId, "throughput", new
        {
            bucket = "day", from = D(1), to = D(10),
            conditions = new[] { new { field = "summary", op = "Contains", value = "alpha" } },
        });

        // ACME-1's completion survives, ACME-2's does not; and the emptied bucket is still
        // emitted as zero, because "nothing was completed" is a finding, not an absence of data.
        Assert.Equal(1, alpha.Buckets.Sum(b => b.Completed));
        Assert.Equal(1, alpha.Buckets.Single(b => b.PeriodStart == D(4)).Completed);
        Assert.Equal(0, alpha.Buckets.Single(b => b.PeriodStart == D(8)).Completed);
    }

    [Fact]
    public async Task Status_aging_narrows_both_its_page_and_its_histogram()
    {
        var (client, ctx) = await SeedAsync();

        // Only ACME-3 is not Done, so unfiltered aging has exactly one row.
        var all = await GetAsync<StatusAgingReport>(client, ctx.ProjectId, "status-aging");
        Assert.Equal(1, all.Items.TotalCount);
        Assert.Equal(1, all.Histogram.Sum(b => b.Count));

        // "beta" matches only ACME-2, which IS Done — so the aging population empties.
        var beta = await PostAsync<StatusAgingReport>(
            client, ctx.ProjectId, "status-aging", Summary("beta"));

        Assert.Equal(0, beta.Items.TotalCount);
        Assert.Empty(beta.Items.Items);

        // All five bands are still emitted, at zero — the histogram describes the filtered
        // population rather than disappearing along with it.
        Assert.Equal(5, beta.Histogram.Count);
        Assert.Equal(0, beta.Histogram.Sum(b => b.Count));
    }

    [Fact]
    public async Task Posting_no_filter_matches_the_unfiltered_get()
    {
        var (client, ctx) = await SeedAsync();

        var get = await GetAsync<StoryCountsReport>(client, ctx.ProjectId, "story-counts");
        var post = await PostAsync<StoryCountsReport>(client, ctx.ProjectId, "story-counts", new { });

        Assert.Equal(get.TotalIssues, post.TotalIssues);
        Assert.Equal(get.TotalStoryPoints, post.TotalStoryPoints);
        Assert.Equal(get.ByStatus.Count, post.ByStatus.Count);
    }

    [Fact]
    public async Task Filter_validation_matches_the_issue_query()
    {
        var (client, ctx) = await SeedAsync();

        var unknownField = await client.PostAsJsonAsync(
            $"/api/projects/{ctx.ProjectId}/metrics/story-counts",
            new { conditions = new[] { new { field = "secret", op = "Eq", value = "x" } } });
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        Assert.Contains("Unknown field 'secret'", await unknownField.Content.ReadAsStringAsync());

        var both = await client.PostAsJsonAsync(
            $"/api/projects/{ctx.ProjectId}/metrics/story-counts",
            new
            {
                conditions = new[] { new { field = "summary", op = "Contains", value = "alpha" } },
                filter = new { logic = "Or", conditions = new[] { new { field = "summary", op = "Contains", value = "beta" } } },
            });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Contains("not both", await both.Content.ReadAsStringAsync());

        var emptyGroup = await client.PostAsJsonAsync(
            $"/api/projects/{ctx.ProjectId}/metrics/status-aging",
            new { filter = new { logic = "And" } });
        Assert.Equal(HttpStatusCode.BadRequest, emptyGroup.StatusCode);
        Assert.Contains("at least one condition or nested group",
            await emptyGroup.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Throughput_window_validation_keeps_its_error_body_shape_on_the_post_route()
    {
        var (client, ctx) = await SeedAsync();

        var badBucket = await client.PostAsJsonAsync(
            $"/api/projects/{ctx.ProjectId}/metrics/throughput", new { bucket = "fortnight" });
        Assert.Equal(HttpStatusCode.BadRequest, badBucket.StatusCode);
        Assert.Contains("bucket must be one of: day, week, month",
            await badBucket.Content.ReadAsStringAsync());

        var reversed = await client.PostAsJsonAsync(
            $"/api/projects/{ctx.ProjectId}/metrics/throughput", new { from = D(10), to = D(1) });
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Contains("from must not be later than to", await reversed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_bad_filter_at_another_tenants_project_returns_404_not_400()
    {
        var (_, ctx) = await SeedAsync();
        var (clientB, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        // Project check first: 400 here would confirm A's project id exists.
        var resp = await clientB.PostAsJsonAsync(
            $"/api/projects/{ctx.ProjectId}/metrics/story-counts",
            new { conditions = new[] { new { field = "secret", op = "Eq", value = "x" } } });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
