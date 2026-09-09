// tests/JiraUnpacked.Tests/Integration/FieldChurnTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Field churn — "which stories keep getting pushed to the next sprint". The question JQL cannot
/// ask: Jira has a CHANGED predicate but no way to COUNT changes, so the answer exists only in
/// the ingested changelog.
/// </summary>
public sealed class FieldChurnTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public FieldChurnTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ItemDto(string JiraIssueId, string JiraKey, string? Summary, string? IssueType,
        string? CurrentStatus, string Class, string? Team, int Changes, int DistinctValues,
        DateTime FirstChangedAt, DateTime LastChangedAt);
    private sealed record BandDto(string Band, int Count);
    private sealed record PageDto(List<ItemDto> Items, int Page, int PageSize, int TotalCount);
    private sealed record ReportDto(string Field, List<string> AvailableFields, PageDto Items, List<BandDto> Histogram);

    private static async Task<ReportDto> GetAsync(HttpClient client, Guid projectId, string query = "")
    {
        var resp = await client.GetAsync($"/api/projects/{projectId}/metrics/field-churn{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ReportDto>())!;
    }

    /// <summary>Moves an issue between sprints, one changelog entry per move.</summary>
    private static async Task SprintMovesAsync(SeedContext ctx, string issueId, params string[] sprints)
    {
        var at = DateTime.UtcNow.AddDays(-sprints.Length);
        for (var i = 0; i < sprints.Length; i++)
            await MetricsTestData.SeedFieldChangeAsync(
                ctx, issueId, $"{issueId}-h{i}", "Sprint",
                i == 0 ? null : sprints[i - 1], sprints[i], at.AddDays(i));
    }

    [Fact]
    public async Task Ranks_issues_by_how_often_the_field_changed_worst_first()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "carried three times", ctx.InProgressStatusId, null, now.AddDays(-60));
        await SprintMovesAsync(ctx, "10001", "S1", "S2", "S3");

        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "carried twice", ctx.InProgressStatusId, null, now.AddDays(-60));
        await SprintMovesAsync(ctx, "10002", "S1", "S2");

        // One move only: below the default threshold of 2, so not churn at all.
        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "planned once", ctx.InProgressStatusId, null, now.AddDays(-60));
        await SprintMovesAsync(ctx, "10003", "S1");

        var r = await GetAsync(client, ctx.ProjectId);

        Assert.Equal("Sprint", r.Field);
        Assert.Equal(2, r.Items.TotalCount);
        Assert.Equal(new[] { "ACME-1", "ACME-2" }, r.Items.Items.Select(i => i.JiraKey));
        Assert.Equal(3, r.Items.Items[0].Changes);
        Assert.Equal(2, r.Items.Items[1].Changes);
        Assert.Equal("In Progress", r.Items.Items[0].CurrentStatus);
        Assert.Equal("Active", r.Items.Items[0].Class);
    }

    [Fact]
    public async Task Each_row_carries_the_issue_s_team()
    {
        // Sprint churn is a delivery-health signal, and the first question about a list of
        // repeatedly-carried stories is whose they are.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        var cicd = await MetricsTestData.SeedTeamAsync(ctx, "CI/CD");

        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "owned", ctx.InProgressStatusId, null, now.AddDays(-60), cicd);
        await SprintMovesAsync(ctx, "10001", "S1", "S2");

        // No team is a real state — an issue nobody has assigned to one, or a project that maps
        // no Team field — and must come back as null rather than being dropped from the report.
        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "unowned", ctx.InProgressStatusId, null, now.AddDays(-60));
        await SprintMovesAsync(ctx, "10002", "S1", "S2");

        var r = await GetAsync(client, ctx.ProjectId);

        Assert.Equal("CI/CD", Assert.Single(r.Items.Items, i => i.JiraKey == "ACME-1").Team);
        Assert.Null(Assert.Single(r.Items.Items, i => i.JiraKey == "ACME-2").Team);
        Assert.Equal(2, r.Items.TotalCount);
    }

    [Fact]
    public async Task Distinct_values_separate_a_march_through_sprints_from_a_toggle()
    {
        // Both issues changed sprint three times, but one only ever sat in two sprints. Ranking
        // on the raw count alone would call these the same problem; they are not.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "marched", ctx.InProgressStatusId, null, now.AddDays(-60));
        await SprintMovesAsync(ctx, "10001", "S1", "S2", "S3");

        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "toggled", ctx.InProgressStatusId, null, now.AddDays(-60));
        await SprintMovesAsync(ctx, "10002", "S1", "S2", "S1");

        var r = await GetAsync(client, ctx.ProjectId);
        var marched = Assert.Single(r.Items.Items, i => i.JiraKey == "ACME-1");
        var toggled = Assert.Single(r.Items.Items, i => i.JiraKey == "ACME-2");

        Assert.Equal(3, marched.Changes);
        Assert.Equal(3, marched.DistinctValues);
        Assert.Equal(3, toggled.Changes);
        Assert.Equal(2, toggled.DistinctValues);
    }

    [Fact]
    public async Task Only_active_work_counts_unless_asked_otherwise()
    {
        // The default population is Active. A story carried five times and then finished is
        // history; one still In Progress is a problem you can still act on.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "still going", ctx.InProgressStatusId, null, now.AddDays(-60));
        await SprintMovesAsync(ctx, "10001", "S1", "S2");

        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "shipped", ctx.DoneStatusId, null, now.AddDays(-60));
        await SprintMovesAsync(ctx, "10002", "S1", "S2", "S3", "S4");

        var active = await GetAsync(client, ctx.ProjectId);
        Assert.Equal(new[] { "ACME-1" }, active.Items.Items.Select(i => i.JiraKey));

        // Asked for explicitly, the Done issue comes back — and outranks the active one.
        var both = await GetAsync(client, ctx.ProjectId, "?statusClasses=Active&statusClasses=Done");
        Assert.Equal(new[] { "ACME-2", "ACME-1" }, both.Items.Items.Select(i => i.JiraKey));
    }

    [Fact]
    public async Task The_threshold_and_the_field_are_both_selectable()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "reassigned a lot", ctx.InProgressStatusId, null, now.AddDays(-60));
        await SprintMovesAsync(ctx, "10001", "S1", "S2");
        for (var i = 0; i < 3; i++)
            await MetricsTestData.SeedFieldChangeAsync(ctx, "10001", $"a{i}", "assignee", "Alice", "Bob", now.AddDays(-i - 1));

        // minChanges=3 drops the 2-change sprint churn.
        Assert.Empty((await GetAsync(client, ctx.ProjectId, "?minChanges=3")).Items.Items);

        // A different field is a different question over the same table — the reason this is not
        // a denormalised per-field counter column.
        var assignee = await GetAsync(client, ctx.ProjectId, "?field=assignee&minChanges=3");
        Assert.Equal(3, Assert.Single(assignee.Items.Items).Changes);
        Assert.Equal("assignee", assignee.Field);

        // Whatever the chosen field, the caller is told which fields actually have history here.
        Assert.Contains("Sprint", assignee.AvailableFields);
        Assert.Contains("assignee", assignee.AvailableFields);
    }

    [Fact]
    public async Task The_histogram_spans_the_whole_population_not_the_page()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        for (var n = 0; n < 3; n++)
        {
            var id = $"1000{n}";
            await MetricsTestData.SeedIssueAsync(ctx, id, $"ACME-{n}", "s", ctx.InProgressStatusId, null, now.AddDays(-60));
            await SprintMovesAsync(ctx, id, "S1", "S2");
        }

        var r = await GetAsync(client, ctx.ProjectId, "?pageSize=1");

        Assert.Single(r.Items.Items);
        Assert.Equal(3, r.Items.TotalCount);
        // All five bands always emitted, counted over all three issues rather than the one shown.
        Assert.Equal(5, r.Histogram.Count);
        Assert.Equal(3, Assert.Single(r.Histogram, b => b.Band == "2").Count);
        Assert.Equal(0, Assert.Single(r.Histogram, b => b.Band == "5+").Count);
    }

    [Fact]
    public async Task A_project_with_no_history_for_the_field_is_empty_not_an_error()
    {
        // A team that has never moved a sprint is a legitimate answer, not a failure — and the
        // caller still learns which fields DO have history.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "s", ctx.InProgressStatusId, null, DateTime.UtcNow.AddDays(-10));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "In Progress", DateTime.UtcNow.AddDays(-1));

        var r = await GetAsync(client, ctx.ProjectId);

        Assert.Empty(r.Items.Items);
        Assert.Equal(0, r.Items.TotalCount);
        Assert.Equal(new[] { "status" }, r.AvailableFields);
    }

    [Fact]
    public async Task An_unknown_status_class_is_rejected_and_an_unknown_project_is_not_found()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var bad = await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/field-churn?statusClasses=Sideways");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // The project check runs first, so a bad class aimed at someone else's project still 404s
        // rather than confirming the project exists.
        var missing = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/metrics/field-churn?statusClasses=Sideways");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_project_is_not_found()
    {
        var (_, tenantA) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctxA = await MetricsTestData.SeedProjectAsync(tenantA);
        await MetricsTestData.SeedIssueAsync(ctxA, "10001", "ACME-1", "s", ctxA.InProgressStatusId, null, DateTime.UtcNow.AddDays(-60));
        await SprintMovesAsync(ctxA, "10001", "S1", "S2", "S3");

        var (clientB, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        var resp = await clientB.GetAsync($"/api/projects/{ctxA.ProjectId}/metrics/field-churn");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
