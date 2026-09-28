// tests/JiraUnpacked.Tests/Integration/StatusCategoryTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// statusCategory end to end. SeedProjectAsync's statuses carry the categories "new" (To Do),
/// "indeterminate" (In Progress) and "done" (Done) — shown as stored, whatever Jira sent.
/// </summary>
public sealed class StatusCategoryTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public StatusCategoryTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ValueDto(string Name, int Count);
    private sealed record ValuesDto(string Field, List<ValueDto> Values, int EmptyCount);

    private static object Is(string op, string value) => new { field = "statusCategory", op, value };
    private static object Any(string op, params string?[] values) => new { field = "statusCategory", op, values };
    private static object Empty(string op) => new { field = "statusCategory", op };

    /// <summary>C-T To Do, C-P In Progress, C-D Done, C-L a status with no category, C-0 no status.</summary>
    private async Task<(HttpClient client, SeedContext ctx)> SeedAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var limbo = await RicherFilterTestData.SeedStatusAsync(ctx, "Limbo", null);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-T", statusId: ctx.ToDoStatusId);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-P", statusId: ctx.InProgressStatusId);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-D", statusId: ctx.DoneStatusId);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-L", statusId: limbo);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-0");
        return (client, ctx);
    }

    [Fact]
    public async Task Filters_by_category_with_the_empty_rule()
    {
        var (client, ctx) = await SeedAsync();
        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);

        Assert.Equal(new[] { "C-T" }, await Keys(Is("Eq", "new")));
        Assert.Equal(new[] { "C-D", "C-P" }, await Keys(Is("Neq", "new")));
        Assert.Equal(new[] { "C-0", "C-D", "C-L" }, await Keys(Any("In", "done", null)));
        Assert.Equal(new[] { "C-P", "C-T" }, await Keys(Any("NotIn", "done")));
        Assert.Equal(new[] { "C-0", "C-L" }, await Keys(Empty("IsNull")));
        Assert.Equal(new[] { "C-D", "C-P", "C-T" }, await Keys(Empty("IsNotNull")));
    }

    [Fact]
    public async Task The_value_list_counts_categories_and_both_kinds_of_empty()
    {
        var (client, ctx) = await SeedAsync();

        var body = await client.GetFromJsonAsync<ValuesDto>($"/api/projects/{ctx.ProjectId}/filter-values?field=statusCategory");

        Assert.Equal(new[] { new ValueDto("done", 1), new ValueDto("indeterminate", 1), new ValueDto("new", 1) }, body!.Values);
        Assert.Equal(2, body.EmptyCount);   // C-L (no category) and C-0 (no status)
    }

    [Fact]
    public async Task The_list_carries_the_category_and_sorts_by_it()
    {
        var (client, ctx) = await SeedAsync();

        var resp = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query",
            new { sortBy = "statusCategory", sortDescending = false });
        var page = (await resp.Content.ReadFromJsonAsync<PagedResult<IssueListItem>>())!;

        Assert.Equal(new[] { "C-D", "C-P", "C-T" }, page.Items.Take(3).Select(i => i.JiraKey));
        Assert.Equal("done", page.Items.Single(i => i.JiraKey == "C-D").StatusCategory);
        Assert.Null(page.Items.Single(i => i.JiraKey == "C-L").StatusCategory);
    }

    [Fact]
    public async Task The_export_has_a_category_column()
    {
        var (client, ctx) = await SeedAsync();

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?cols=jiraKey,statusCategory&sortBy=jiraKey&sortDescending=false");

        Assert.StartsWith("Key,Category\r\n", csv);
        Assert.Contains("C-D,done\r\n", csv);
    }
}
