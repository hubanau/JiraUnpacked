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
    private sealed record CatalogDto(
        List<FieldDto> Fields, int MaxConditions, int MaxInValues, int MaxGroups, int MaxDepth);

    private static readonly string[] ExpectedFieldNames =
    {
        "status", "issueType", "priority", "assignee", "sprint", "team",
        "storyPoints", "createdAt", "updatedAt", "summary", "jiraKey", "parent",
    };

    // Pins the emitted `type` string per field, matched exactly (not by
    // containment) against the full field set above. The client's FieldType
    // union ('DictName' | 'Decimal' | 'DateTime' | 'Text' | 'ParentRef' — see
    // src/JiraUnpacked.Web/src/api/issues.ts) is hand-typed to mirror
    // whatever FilterField.cs's internal FieldType enum serialises to
    // (FilterCatalogService projects it via f.Type.ToString()), so nothing
    // else in the build would catch a renamed enum member — this dictionary
    // is the only thing standing between that rename and a client-side
    // isDictionaryField/typeFor break that ships silently.
    private static readonly IReadOnlyDictionary<string, string> ExpectedFieldTypes =
        new Dictionary<string, string>
        {
            ["status"] = "DictName",
            ["issueType"] = "DictName",
            ["priority"] = "DictName",
            ["assignee"] = "DictName",
            ["sprint"] = "DictName",
            ["team"] = "DictName",
            ["storyPoints"] = "Decimal",
            ["createdAt"] = "DateTime",
            ["updatedAt"] = "DateTime",
            ["summary"] = "Text",
            ["jiraKey"] = "Text",
            ["parent"] = "ParentRef",
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

        // Every guard the server enforces is served, so no client has to hand-maintain one.
        // These values are the contract, not an implementation detail: a client sizes its
        // editor from them and a change here is a change clients must see.
        Assert.Equal(20, catalog.MaxConditions);
        Assert.Equal(100, catalog.MaxInValues);
        Assert.Equal(10, catalog.MaxGroups);
        Assert.Equal(3, catalog.MaxDepth);

        // Exact operator SETS (SetEquals, not Contains): a superset here would
        // pass a containment check while offering the client an operator the
        // server actually rejects, producing a runtime 400 this test would
        // never catch.
        var status = catalog.Fields.Single(f => f.Name == "status");
        Assert.True(new HashSet<string> { "Eq", "Neq", "In", "NotIn", "IsNull", "IsNotNull" }.SetEquals(status.Operators));

        var jiraKey = catalog.Fields.Single(f => f.Name == "jiraKey");
        Assert.True(new HashSet<string> { "Eq", "In", "NotIn", "Contains", "StartsWith" }.SetEquals(jiraKey.Operators));

        var storyPoints = catalog.Fields.Single(f => f.Name == "storyPoints");
        Assert.True(new HashSet<string> { "Eq", "Neq", "Lt", "Lte", "Gt", "Gte", "IsNull", "IsNotNull" }.SetEquals(storyPoints.Operators));

        var summary = catalog.Fields.Single(f => f.Name == "summary");
        Assert.True(new HashSet<string> { "Contains", "NotContains", "StartsWith", "IsNull", "IsNotNull" }.SetEquals(summary.Operators));

        var parent = catalog.Fields.Single(f => f.Name == "parent");
        Assert.True(new HashSet<string> { "Eq", "IsNull", "IsNotNull" }.SetEquals(parent.Operators));

        // Exact per-field `type`, and — via SetEquals against
        // ExpectedFieldTypes.Values.Distinct() — the exact distinct set of
        // types the catalog ever emits. Renaming a FieldType member (or
        // reassigning a field to a different one) fails this immediately,
        // whether or not it happens to collide with an existing string.
        foreach (var field in catalog.Fields)
        {
            Assert.True(
                ExpectedFieldTypes.TryGetValue(field.Name, out var expectedType),
                $"Unexpected field '{field.Name}' with no expected type entry.");
            Assert.Equal(expectedType, field.Type);
        }
        Assert.True(
            ExpectedFieldTypes.Values.ToHashSet().SetEquals(catalog.Fields.Select(f => f.Type)),
            $"Expected exactly the types {string.Join(", ", ExpectedFieldTypes.Values.Distinct())}, " +
            $"got {string.Join(", ", catalog.Fields.Select(f => f.Type).Distinct())}");
    }

    [Fact]
    public async Task Unauthenticated_get_returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/filter-catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
