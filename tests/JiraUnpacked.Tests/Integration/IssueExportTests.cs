// tests/JiraUnpacked.Tests/Integration/IssueExportTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// CSV export. The properties worth pinning are that it exports exactly what the list shows, and
/// that a summary written by a stranger cannot execute in the reader's spreadsheet.
/// </summary>
public sealed class IssueExportTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueExportTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Exports_exactly_the_rows_the_filtered_list_reports()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        for (var i = 0; i < 3; i++) await Seed(ctx, $"DONE-{i}", ctx.DoneStatusId);
        for (var i = 0; i < 2; i++) await Seed(ctx, $"TODO-{i}", ctx.ToDoStatusId);

        var filter = "[{\"field\":\"status\",\"op\":\"Eq\",\"value\":\"Done\"}]";

        var listed = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query", new
        {
            filter = new
            {
                logic = "And",
                conditions = new[] { new { field = "status", op = "Eq", value = "Done" } },
            },
        });
        var total = (await listed.Content.ReadFromJsonAsync<PagedResult<IssueListItem>>())!.TotalCount;

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?filter={Uri.EscapeDataString(filter)}");

        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, total);
        Assert.Equal(total + 1, rows.Length);   // + header
    }

    [Fact]
    public async Task A_summary_that_looks_like_a_formula_cannot_execute_in_a_spreadsheet()
    {
        // End to end, not just the unit: the escaping has to survive projection and streaming.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(
            ctx, "10001", "ACME-1", "=HYPERLINK(\"http://evil\",\"click\")",
            null, null, DateTime.UtcNow);

        var csv = await client.GetStringAsync($"/api/projects/{ctx.ProjectId}/issues/export.csv");

        Assert.Contains("\"'=HYPERLINK", csv);
        Assert.DoesNotContain(",=HYPERLINK", csv);
    }

    [Fact]
    public async Task A_summary_containing_a_comma_and_a_quote_stays_one_field()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(
            ctx, "10001", "ACME-1", "fix \"login\", urgently", null, null, DateTime.UtcNow);

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?cols=jiraKey,summary");

        Assert.Contains("ACME-1,\"fix \"\"login\"\", urgently\"", csv);
    }

    [Fact]
    public async Task Exports_only_the_requested_columns_in_catalog_order()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await Seed(ctx, "ACME-1", ctx.ToDoStatusId);

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?cols=status,jiraKey");

        Assert.StartsWith("Key,Status\r\n", csv);
    }

    [Fact]
    public async Task Refuses_an_unreadable_filter_rather_than_failing_with_a_500()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/issues/export.csv?filter=%7Bnot-json");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_malformed_legacy_array_filter_rather_than_failing_with_a_500()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?filter={Uri.EscapeDataString("[bad")}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Refuses_an_unknown_sort_column()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/issues/export.csv?sortBy=nonsense");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task An_unknown_project_is_404()
    {
        var (client, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        var resp = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/issues/export.csv");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Names_the_file_after_the_project_so_two_exports_do_not_collide()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/issues/export.csv");

        Assert.Contains("ACME-issues-", resp.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Exports_a_filter_tree_and_ignores_the_legacy_logic_param()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        for (var i = 0; i < 3; i++) await Seed(ctx, $"DONE-{i}", ctx.DoneStatusId);
        for (var i = 0; i < 2; i++) await Seed(ctx, $"TODO-{i}", ctx.ToDoStatusId);
        await MetricsTestData.SeedIssueAsync(ctx, "90001", "NONE-0", "no status", null, null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Done OR (Empty), with a switched-off To Do arm that must stay out. `logic=And` belongs
        // to the old array format; applied to this tree it would turn the Or into an And and
        // export nothing.
        var tree = """{"logic":"Or","conditions":[{"field":"status","op":"Eq","value":"Done"},{"field":"status","op":"In","values":[null]},{"field":"status","op":"Eq","value":"To Do","disabled":true}]}""";

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?filter={Uri.EscapeDataString(tree)}&logic=And&cols=jiraKey");

        var keys = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(new[] { "DONE-0", "DONE-1", "DONE-2", "NONE-0" }, keys);
    }

    [Fact]
    public async Task A_legacy_array_filter_still_honours_its_logic_param()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        for (var i = 0; i < 3; i++) await Seed(ctx, $"DONE-{i}", ctx.DoneStatusId);
        for (var i = 0; i < 2; i++) await Seed(ctx, $"TODO-{i}", ctx.ToDoStatusId);

        var legacy = """[{"field":"status","op":"Eq","value":"Done"},{"field":"status","op":"Eq","value":"To Do"}]""";

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?filter={Uri.EscapeDataString(legacy)}&logic=Or&cols=jiraKey");

        Assert.Equal(5 + 1, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);   // + header
    }

    private static Task Seed(SeedContext ctx, string key, Guid statusId) =>
        MetricsTestData.SeedIssueAsync(
            ctx, Guid.NewGuid().ToString("N")[..10], key, key, statusId, null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
}
