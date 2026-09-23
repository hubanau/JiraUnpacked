// tests/JiraUnpacked.Tests/Integration/IssueFilterGroupEndpointTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Boolean grouping end-to-end: POST api/projects/{projectId}/issues/query with a `filter` tree,
/// against FilterTestData's six-issue fixture. Every case asserts the exact seeded key set —
/// an Or that accidentally matched everything would look like a pass against a loose bound.
/// </summary>
public sealed class IssueFilterGroupEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueFilterGroupEndpointTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static object Cond(string field, string op, object? value = null, object? values = null) =>
        FilterTestData.Cond(field, op, value, values);

    private static object Group(string logic, object[]? conditions = null, object[]? groups = null) =>
        new { logic, conditions, groups };

    private static object Req(object filter, int page = 1, int pageSize = 50) =>
        new { filter, page, pageSize };

    private static string[] Keys(FilterTestData.PageDto page) =>
        page.Items.Select(i => i.JiraKey).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Or_across_two_different_fields_returns_the_union_of_both_arms()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        // status = In Progress  (ACME-1)   OR   assignee = Bob  (ACME-4, ACME-5, ACME-6)
        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(Group("Or", new[]
        {
            Cond("status", "Eq", value: "In Progress"),
            Cond("assignee", "Eq", value: "Bob"),
        })));

        // Strictly larger than either arm alone (1 and 3), strictly smaller than the project (6):
        // the union, and nothing else. A cross-field Or is exactly what a flat AND could not express.
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(new[] { "ACME-1", "ACME-4", "ACME-5", "ACME-6" }, Keys(page));
    }

    [Fact]
    public async Task And_of_an_or_group_with_a_sibling_condition_intersects_them()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        // (status = In Progress OR status = Done)  AND  assignee = Alice
        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(new
        {
            logic = "And",
            conditions = new[] { Cond("assignee", "Eq", value: "Alice") },
            groups = new[]
            {
                Group("Or", new[]
                {
                    Cond("status", "Eq", value: "In Progress"),
                    Cond("status", "Eq", value: "Done"),
                }),
            },
        }));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { "ACME-1", "ACME-2" }, Keys(page));
    }

    [Fact]
    public async Task Or_of_two_and_groups_returns_only_rows_satisfying_a_whole_arm()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        // (status = In Progress AND points >= 5)  OR  (parent = 10004 AND assignee = Bob)
        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(Group("Or", groups: new[]
        {
            Group("And", new[] { Cond("status", "Eq", value: "In Progress"), Cond("storyPoints", "Gte", value: 5) }),
            Group("And", new[] { Cond("parent", "Eq", value: "10004"), Cond("assignee", "Eq", value: "Bob") }),
        })));

        // ACME-4 is Bob's but has no parent, so it satisfies neither arm whole — the case that
        // separates a real Or-of-Ands from a flattened "any condition matches".
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { "ACME-1", "ACME-5", "ACME-6" }, Keys(page));
    }

    [Fact]
    public async Task Conditions_and_filter_together_returns_400()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/query", new
        {
            conditions = new[] { Cond("status", "Eq", value: "Done") },
            filter = Group("Or", new[] { Cond("assignee", "Eq", value: "Bob") }),
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not both", body);
    }

    [Fact]
    public async Task Empty_group_returns_400_rather_than_matching_everything()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        var resp = await FilterTestData.PostQueryAsync(client, projectId, Req(Group("And")));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("at least one condition or nested group",
            await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Depth_and_group_guards_are_enforced_over_the_request()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        // 4 levels: root > a > b > leaf-holder.
        object tooDeep = Group("And", new[] { Cond("status", "Eq", value: "Done") });
        for (var i = 0; i < 3; i++) tooDeep = Group("And", groups: new[] { tooDeep });

        var deep = await FilterTestData.PostQueryAsync(client, projectId, Req(tooDeep));
        Assert.Equal(HttpStatusCode.BadRequest, deep.StatusCode);
        Assert.Contains("nest at most 3 levels deep", await deep.Content.ReadAsStringAsync());

        // 10 sibling subgroups + the root = 11 groups.
        var tooMany = Group("Or", groups: Enumerable.Range(0, 10)
            .Select(_ => Group("And", new[] { Cond("status", "Eq", value: "Done") }))
            .ToArray());

        var many = await FilterTestData.PostQueryAsync(client, projectId, Req(tooMany));
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Contains("at most 10 groups", await many.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Injection_payload_inside_an_or_group_stays_inert()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        // The same payload IssueFilterSecurityTests proves inert as a lone condition, now OR-ed
        // with a condition that matches nothing: combining predicates must not open a path that
        // a single predicate closed. A leaked wildcard or a broken-out string would show up here
        // as extra rows, and a concatenated statement as a 500.
        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(Group("Or", new[]
        {
            Cond("summary", "Contains", value: "'; DROP TABLE \"Issues\"; --"),
            Cond("summary", "Contains", value: "%"),
        })));

        // "%" is escaped to a literal percent sign, which no seeded summary contains — so the
        // honest answer is zero rows, not "every row" (which is what an unescaped % would give).
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Disabled_parts_are_left_out_of_the_query()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        // Were the switched-off arm applied, Bob's three issues would join ACME-1.
        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(new
        {
            logic = "Or",
            conditions = new object[]
            {
                Cond("status", "Eq", value: "In Progress"),
                new { field = "assignee", op = "Eq", value = "Bob", disabled = true },
            },
        }));

        Assert.Equal(new[] { "ACME-1" }, Keys(page));
    }

    [Fact]
    public async Task A_filter_with_everything_switched_off_returns_every_issue()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(new
        {
            logic = "And",
            conditions = new object[] { new { field = "status", op = "Eq", value = "Done", disabled = true } },
        }));

        Assert.Equal(6, page.TotalCount);
    }

    [Fact]
    public async Task Three_levels_with_not_in_and_explicit_empty_select_exactly_the_intended_rows()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        //     assignee NotIn [Alice]                                             -> ACME-4, 5, 6 (ACME-3 has none: excluded)
        // AND ( parent IsNotNull                                                 -> ACME-5, 6
        //       OR ( summary NotContains "epic" AND status In [(Empty)] ) )      -> ACME-3, 5, 6
        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(new
        {
            logic = "And",
            conditions = new[] { Cond("assignee", "NotIn", values: new[] { "Alice" }) },
            groups = new[]
            {
                new
                {
                    logic = "Or",
                    conditions = new[] { Cond("parent", "IsNotNull") },
                    groups = new[]
                    {
                        Group("And", new[]
                        {
                            Cond("summary", "NotContains", value: "epic"),
                            Cond("status", "In", values: new object?[] { null }),
                        }),
                    },
                },
            },
        }));

        // ACME-4 is Bob's but is the epic and has no parent; ACME-3 fits the inner arm but has
        // no assignee, and "is none of" never matches an empty field.
        Assert.Equal(new[] { "ACME-5", "ACME-6" }, Keys(page));
    }
}
