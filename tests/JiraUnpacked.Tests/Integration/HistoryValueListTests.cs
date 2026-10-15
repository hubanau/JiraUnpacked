// tests/JiraUnpacked.Tests/Integration/HistoryValueListTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The two value lists behind the history row: the fields this project has history for, and
/// one field's values (list elements split out) or authors. Tenant-scoped, 404 for a project
/// that is not the caller's — like filter-values.
/// </summary>
public sealed class HistoryValueListTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public HistoryValueListTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record NameCount(string Name, int Count);
    private sealed record FieldsDto(List<NameCount> Fields);
    private sealed record ValuesDto(List<NameCount> Values, int EmptyCount);

    private async Task<(HttpClient client, SeedContext ctx)> TenantAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        return (client, await MetricsTestData.SeedProjectAsync(tenant));
    }

    private static async Task<T> OkAsync<T>(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Fields_are_listed_most_changed_first_with_their_row_counts()
    {
        var (client, ctx) = await TenantAsync();
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_factory);
        foreach (var (field, n) in new[] { ("status", 2), ("Sprint", 3), ("labels", 2) })
            for (var i = 0; i < n; i++) await HistoryTestData.SeedChangeAsync(ctx, "1", field, null, "x");
        // Another tenant's rows under this project id are never counted.
        for (var i = 0; i < 5; i++) await HistoryTestData.SeedChangeAsync(ctx, "1", "status", null, "x", asTenant: other);

        var dto = await OkAsync<FieldsDto>(client, $"/api/projects/{ctx.ProjectId}/history-fields");
        Assert.Equal(new[] { new NameCount("Sprint", 3), new NameCount("labels", 2), new NameCount("status", 2) }, dto.Fields);
    }

    [Fact]
    public async Task At_most_200_fields_are_listed()
    {
        var (client, ctx) = await TenantAsync();
        await HistoryTestData.SeedManyFieldsAsync(ctx, 201);

        var dto = await OkAsync<FieldsDto>(client, $"/api/projects/{ctx.ProjectId}/history-fields");
        Assert.Equal(200, dto.Fields.Count);
    }

    [Fact]
    public async Task Values_split_jiras_lists_and_count_from_and_to_alike()
    {
        var (client, ctx) = await TenantAsync();
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_factory);
        await HistoryTestData.SeedChangeAsync(ctx, "1", "Sprint", null, "PLAT 100");
        await HistoryTestData.SeedChangeAsync(ctx, "1", "Sprint", "PLAT 100", "PLAT 100, PLAT 101", author: "Bob");
        await HistoryTestData.SeedChangeAsync(ctx, "2", "Sprint", "", "plat 99", author: null);
        await HistoryTestData.SeedChangeAsync(ctx, "2", "Sprint", null, "Secret", author: "Mallory", asTenant: other);

        var values = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=Sprint&kind=value");
        Assert.Equal(new[] { new NameCount("PLAT 100", 3), new NameCount("PLAT 101", 1), new NameCount("plat 99", 1) }, values.Values);
        Assert.Equal(2, values.EmptyCount);   // the null and the "" FromValue

        var authors = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=Sprint&kind=author");
        Assert.Equal(new[] { new NameCount("Alice", 1), new NameCount("Bob", 1) }, authors.Values);
        Assert.Equal(1, authors.EmptyCount);

        // kind defaults to value.
        var byDefault = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=Sprint");
        Assert.Equal(values.Values, byDefault.Values);
    }

    [Fact]
    public async Task Lists_are_split_with_the_separator_the_filter_matches_by()
    {
        var (client, ctx) = await TenantAsync();
        // Jira joins labels with a space, everything else with ", ". Sprint names may hold spaces;
        // a label cannot, and a label list must not be offered as one "a b" entry.
        await HistoryTestData.SeedChangeAsync(ctx, "1", "labels", "ansible", "ansible tested");
        await HistoryTestData.SeedChangeAsync(ctx, "1", "Sprint", "PLAT 100", "PLAT 100, PLAT 101");

        var labels = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=labels");
        Assert.Equal(new[] { new NameCount("ansible", 2), new NameCount("tested", 1) }, labels.Values);

        var sprints = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=Sprint");
        Assert.Equal(new[] { new NameCount("PLAT 100", 2), new NameCount("PLAT 101", 1) }, sprints.Values);
    }

    [Fact]
    public async Task A_stored_value_longer_than_a_filter_value_can_be_is_not_listed()
    {
        var (client, ctx) = await TenantAsync();
        // A description's text is no value to pick: nothing longer than 255 characters can be a
        // filter value. The bound is on the raw stored value, before any list split.
        var longest = new string('a', 255);
        var tooLong = new string('b', 250) + ", " + new string('c', 4);
        await HistoryTestData.SeedChangeAsync(ctx, "1", "description", null, longest);
        await HistoryTestData.SeedChangeAsync(ctx, "1", "description", longest, tooLong);
        await HistoryTestData.SeedChangeAsync(ctx, "2", "description", tooLong, "short");

        var dto = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=description");
        Assert.Equal(new[] { new NameCount(longest, 2), new NameCount("short", 1) }, dto.Values);
        Assert.Equal(1, dto.EmptyCount);   // the null FromValue; a long value is not (Empty)
    }

    [Fact]
    public async Task At_most_1000_values_are_listed()
    {
        var (client, ctx) = await TenantAsync();
        await HistoryTestData.SeedManyValuesAsync(ctx, "labels", 1001);

        var dto = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=labels");
        Assert.Equal(1000, dto.Values.Count);
        Assert.Equal(1001, dto.EmptyCount);   // every FromValue is null
    }

    [Fact]
    public async Task An_unknown_field_is_an_empty_list_and_bad_requests_are_400()
    {
        var (client, ctx) = await TenantAsync();

        var none = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=Nope");
        Assert.Empty(none.Values);
        Assert.Equal(0, none.EmptyCount);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/projects/{ctx.ProjectId}/history-values?field=%20")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/projects/{ctx.ProjectId}/history-values?field=Sprint&kind=colour")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_or_foreign_project_is_404_before_anything_else()
    {
        var (client, _) = await TenantAsync();
        var (_, otherTenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var foreign = await MetricsTestData.SeedProjectAsync(otherTenant);

        foreach (var id in new[] { Guid.NewGuid(), foreign.ProjectId })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{id}/history-fields")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{id}/history-values?field=Sprint")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{id}/history-values?field=%20")).StatusCode);
        }
    }
}
