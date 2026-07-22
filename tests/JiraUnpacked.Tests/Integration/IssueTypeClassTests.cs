// tests/JiraUnpacked.Tests/Integration/IssueTypeClassTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class IssueTypeClassTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueTypeClassTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ItemDto(string JiraIssueTypeId, string Name, string Class);

    [Fact]
    public async Task Lists_issue_types_defaulting_to_other()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        var items = await (await client.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types"))
            .Content.ReadFromJsonAsync<List<ItemDto>>();

        Assert.Equal(2, items!.Count);
        Assert.All(items, i => Assert.Equal("Other", i.Class));
        Assert.Contains(items, i => i.Name == "Epic" && i.JiraIssueTypeId == "1");
    }

    [Fact]
    public async Task Put_sets_a_class_and_is_additive()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        // Classify type "1" as Epic.
        var first = await client.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
            new { issueTypes = new[] { new { jiraIssueTypeId = "1", @class = "Epic" } } });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // A second PUT that names ONLY type "2" must leave type "1" alone (additive, not replace-all).
        var second = await client.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
            new { issueTypes = new[] { new { jiraIssueTypeId = "2", @class = "Other" } } });
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var items = await (await client.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types"))
            .Content.ReadFromJsonAsync<List<ItemDto>>();
        Assert.Equal("Epic", Assert.Single(items!, i => i.JiraIssueTypeId == "1").Class);
        Assert.Equal("Other", Assert.Single(items!, i => i.JiraIssueTypeId == "2").Class);
    }

    [Fact]
    public async Task Unknown_issue_type_id_returns_400()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        var resp = await client.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
            new { issueTypes = new[] { new { jiraIssueTypeId = "does-not-exist", @class = "Epic" } } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Tenant_cannot_read_or_write_another_tenants_issue_types()
    {
        var (_, tenantA) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenantA);
        var (clientB, _) = await GapCheckTestData.AuthedTenantAsync(_factory);

        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
                new { issueTypes = new[] { new { jiraIssueTypeId = "1", @class = "Epic" } } })).StatusCode);
    }
}
