// tests/JiraUnpacked.Tests/Integration/GapCheckEndpointTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class GapCheckEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public GapCheckEndpointTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record InfoDto(string Id, string Name, string Description);
    private sealed record ChildDto(string JiraIssueId, string JiraKey, string? Summary, string? Status, string Class);
    private sealed record ViolationDto(string JiraIssueId, string JiraKey, string? Summary,
        string? Status, string Class, string? IssueType, List<ChildDto> Children);
    private sealed record PageDto(List<ViolationDto> Items, int Page, int PageSize, int TotalCount);
    private sealed record ResultDto(InfoDto Check, string Status, PageDto Violations);

    [Fact]
    public async Task Catalog_lists_both_checks()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        var checks = await (await client.GetAsync($"/api/projects/{seed.ProjectId}/checks"))
            .Content.ReadFromJsonAsync<List<InfoDto>>();

        Assert.Equal(2, checks!.Count);
        Assert.Contains(checks, c => c.Id == "open-epic-all-children-done");
        Assert.Contains(checks, c => c.Id == "done-epic-open-children");
    }

    [Fact]
    public async Task Flagship_reports_the_open_epic_whose_children_are_all_done()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        // Epic ACME-1 is To Do (Other); both children are Done => violation.
        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);
        await GapCheckTestData.SeedIssueAsync(seed, "10002", "ACME-2", "a", seed.StoryTypeId, seed.DoneStatusId, "10001");
        await GapCheckTestData.SeedIssueAsync(seed, "10003", "ACME-3", "b", seed.StoryTypeId, seed.DoneStatusId, "10001");
        // A childless epic must NOT be reported.
        await GapCheckTestData.SeedIssueAsync(seed, "10004", "ACME-4", "lonely", seed.EpicTypeId, seed.ToDoStatusId, null);

        var r = await (await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done"))
            .Content.ReadFromJsonAsync<ResultDto>();

        Assert.Equal("Ok", r!.Status);
        Assert.Equal(1, r.Violations.TotalCount);
        var v = Assert.Single(r.Violations.Items);
        Assert.Equal("ACME-1", v.JiraKey);
        Assert.Equal("Epic", v.IssueType);
        Assert.Equal(2, v.Children.Count);
    }

    [Fact]
    public async Task Inverse_reports_the_done_epic_with_an_open_child()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.DoneStatusId, null);
        await GapCheckTestData.SeedIssueAsync(seed, "10002", "ACME-2", "done", seed.StoryTypeId, seed.DoneStatusId, "10001");
        await GapCheckTestData.SeedIssueAsync(seed, "10003", "ACME-3", "open", seed.StoryTypeId, seed.ToDoStatusId, "10001");

        var r = await (await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/done-epic-open-children"))
            .Content.ReadFromJsonAsync<ResultDto>();

        var v = Assert.Single(r!.Violations.Items);
        Assert.Equal("ACME-1", v.JiraKey);
        var child = Assert.Single(v.Children);   // only the open child is evidence
        Assert.Equal("ACME-3", child.JiraKey);
    }

    [Fact]
    public async Task Reports_not_configured_when_no_issue_type_is_an_epic()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);   // nothing classified Epic
        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);
        await GapCheckTestData.SeedIssueAsync(seed, "10002", "ACME-2", "a", seed.StoryTypeId, seed.DoneStatusId, "10001");

        var r = await (await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done"))
            .Content.ReadFromJsonAsync<ResultDto>();

        Assert.Equal("NotConfigured", r!.Status);          // distinguishable from a clean run
        Assert.Equal(0, r.Violations.TotalCount);
        Assert.Empty(r.Violations.Items);
    }

    [Fact]
    public async Task Empty_project_returns_200_with_no_violations()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        var resp = await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);    // 200, NOT 404

        var r = await resp.Content.ReadFromJsonAsync<ResultDto>();
        Assert.Equal("Ok", r!.Status);
        Assert.Equal(0, r.Violations.TotalCount);
    }

    [Fact]
    public async Task Unknown_check_id_returns_404()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/no-such-check");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Pagination_params_are_clamped()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        // Two violating epics, each with a single done child.
        for (var n = 1; n <= 2; n++)
        {
            await GapCheckTestData.SeedIssueAsync(seed, $"1000{n}", $"ACME-{n}", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);
            await GapCheckTestData.SeedIssueAsync(seed, $"2000{n}", $"KID-{n}", "child", seed.StoryTypeId, seed.DoneStatusId, $"1000{n}");
        }

        // page < 1 floors to 1; pageSize > 100 caps at 100 (the response echoes the clamped values).
        var r = await (await client.GetAsync(
                $"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done?page=0&pageSize=500"))
            .Content.ReadFromJsonAsync<ResultDto>();

        Assert.Equal(1, r!.Violations.Page);
        Assert.Equal(100, r.Violations.PageSize);
        Assert.Equal(2, r.Violations.TotalCount);
        Assert.Equal(2, r.Violations.Items.Count);
    }
}
