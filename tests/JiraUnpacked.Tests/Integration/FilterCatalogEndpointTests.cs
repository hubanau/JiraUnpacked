// tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class FilterCatalogEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public FilterCatalogEndpointTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record FieldDto(string Name, string Type, List<string> Operators);
    private sealed record CatalogDto(List<FieldDto> Fields, int MaxConditions, int MaxInValues);

    private static readonly string[] ExpectedFieldNames =
    {
        "status", "issueType", "priority", "assignee", "sprint", "team",
        "storyPoints", "createdAt", "updatedAt", "summary", "jiraKey", "parent",
    };

    [Fact]
    public async Task Authenticated_get_returns_the_full_catalog_with_exact_operator_sets()
    {
        var (client, _) = await GapCheckTestData.AuthedTenantAsync(_factory);

        var response = await client.GetAsync("/api/filter-catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var catalog = await response.Content.ReadFromJsonAsync<CatalogDto>();
        Assert.NotNull(catalog);

        // All twelve fields, no more, no fewer, no duplicates: Count pins the
        // cardinality and SetEquals pins the membership, together ruling out
        // both an extra/missing field and a duplicate masking a missing one.
        Assert.Equal(12, catalog!.Fields.Count);
        Assert.True(
            ExpectedFieldNames.ToHashSet().SetEquals(catalog.Fields.Select(f => f.Name)),
            $"Expected exactly {string.Join(", ", ExpectedFieldNames)}, got {string.Join(", ", catalog.Fields.Select(f => f.Name))}");

        Assert.Equal(20, catalog.MaxConditions);
        Assert.Equal(100, catalog.MaxInValues);

        // Exact operator SETS (SetEquals, not Contains): a superset here would
        // pass a containment check while offering the client an operator the
        // server actually rejects, producing a runtime 400 this test would
        // never catch.
        var status = catalog.Fields.Single(f => f.Name == "status");
        Assert.True(new HashSet<string> { "Eq", "Neq", "In", "IsNull" }.SetEquals(status.Operators));

        var jiraKey = catalog.Fields.Single(f => f.Name == "jiraKey");
        Assert.True(new HashSet<string> { "Eq", "Contains", "StartsWith" }.SetEquals(jiraKey.Operators));
    }

    [Fact]
    public async Task Unauthenticated_get_returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/filter-catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
