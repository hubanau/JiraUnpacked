// tests/JiraUnpacked.Tests/Integration/IssueFilterTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

using PageDto = JiraUnpacked.Tests.Integration.FilterTestData.PageDto;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// FR-E4: POST api/projects/{projectId}/issues/query — per-operator, validation and 404 coverage.
/// Fixture (SeedFilterFixtureAsync) seeds one project with six issues, ordered newest-JiraUpdatedAt-first:
///   ACME-1 (10001) In Progress, Alice, 5 pts,  no parent,  summary "Fix login timeout",       updated Jan 5
///   ACME-2 (10002) Done,        Alice, 1 pt,   no parent,  summary "Update onboarding docs",  updated Jan 4
///   ACME-3 (10003) no status,   unassigned,    no parent,  summary "Investigate memory leak", updated Jan 3
///   ACME-4 (10004) no status,   Bob,           no parent (parent of 5 &amp; 6), summary "Parent epic for children", updated Jan 2
///   ACME-5 (10005) no status,   Bob,           parent=10004, summary "Child task one",        updated Jan 6
///   ACME-6 (10006) no status,   Bob,           parent=10004, summary "Child task two",        updated Jan 7
/// Every test asserts the exact seeded match set, not a loose bound.
/// </summary>
public sealed class IssueFilterTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueFilterTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Auth, seeding and request-building live in FilterTestData so the boolean-group tests
    // exercise the SAME fixture rather than a second copy of it that could drift.
    private Task<(HttpClient client, Guid tenantId)> AuthedTenantAsync() =>
        FilterTestData.AuthedTenantAsync(_factory);

    private static Task<Guid> SeedFilterFixtureAsync(Guid tenant) =>
        FilterTestData.SeedFilterFixtureAsync(tenant);

    private static object Cond(string field, string op, object? value = null, object? values = null) =>
        FilterTestData.Cond(field, op, value, values);

    private static object Req(List<object> conditions, int page = 1, int pageSize = 50) =>
        FilterTestData.Req(conditions, page, pageSize);

    private static Task<HttpResponseMessage> PostQueryAsync(HttpClient client, Guid projectId, object request) =>
        FilterTestData.PostQueryAsync(client, projectId, request);

    private static Task<PageDto> QueryOkAsync(HttpClient client, Guid projectId, object request) =>
        FilterTestData.QueryOkAsync(client, projectId, request);

    private static Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps) =>
        FilterTestData.Exec(conn, sql, ps);

    // ---- per-operator coverage ----

    [Fact]
    public async Task Status_eq_returns_only_the_matching_issue()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("status", "Eq", value: "In Progress") }));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ACME-1", page.Items[0].JiraKey);
    }

    [Fact]
    public async Task Status_in_returns_the_matching_set()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("status", "In", values: new[] { "In Progress", "Done" }) }));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { "ACME-1", "ACME-2" }, page.Items.Select(i => i.JiraKey)); // newest-updated first
    }

    [Fact]
    public async Task Status_neq_includes_an_unassigned_issue()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("status", "Neq", value: "Done") }));

        // Everything except the Done issue (ACME-2): ACME-1,3,4,5,6 — including unassigned ACME-3.
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(new[] { "ACME-1", "ACME-3", "ACME-4", "ACME-5", "ACME-6" },
            page.Items.Select(i => i.JiraKey).OrderBy(k => k));
        Assert.Contains(page.Items, i => i.JiraKey == "ACME-3" && i.Assignee is null);
    }

    [Fact]
    public async Task Assignee_isnull_returns_only_unassigned()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("assignee", "IsNull") }));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ACME-3", page.Items[0].JiraKey);
    }

    [Fact]
    public async Task StoryPoints_gte_filters_numerically()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("storyPoints", "Gte", value: 3) }));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ACME-1", page.Items[0].JiraKey); // 5 pts; ACME-2's 1 pt is excluded
    }

    [Fact]
    public async Task UpdatedAt_range_via_gte_and_lte_is_anded()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new()
            {
                Cond("updatedAt", "Gte", value: "2024-01-03T00:00:00Z"),
                Cond("updatedAt", "Lte", value: "2024-01-05T00:00:00Z"),
            }));

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { "ACME-1", "ACME-2", "ACME-3" }, page.Items.Select(i => i.JiraKey)); // newest first
    }

    [Fact]
    public async Task Summary_contains_is_case_insensitive()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("summary", "Contains", value: "LOGIN") })); // upper-case needle

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ACME-1", page.Items[0].JiraKey);
    }

    [Fact]
    public async Task Summary_startswith_filters()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("summary", "StartsWith", value: "Investigate") }));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ACME-3", page.Items[0].JiraKey);
    }

    [Fact]
    public async Task JiraKey_eq_filters_exact()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("jiraKey", "Eq", value: "ACME-4") }));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ACME-4", page.Items[0].JiraKey);
    }

    [Fact]
    public async Task Parent_isnull_returns_top_level_only()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("parent", "IsNull") }));

        Assert.Equal(4, page.TotalCount);
        Assert.Equal(new[] { "ACME-1", "ACME-2", "ACME-3", "ACME-4" }, page.Items.Select(i => i.JiraKey)); // newest first
    }

    [Fact]
    public async Task Parent_eq_returns_the_children()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("parent", "Eq", value: "10004") }));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { "ACME-6", "ACME-5" }, page.Items.Select(i => i.JiraKey)); // newest first
    }

    [Fact]
    public async Task Empty_conditions_equals_the_unfiltered_list_count()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var filtered = await QueryOkAsync(client, projectId, Req(new()));

        var unfiltered = await (await client.GetAsync($"/api/projects/{projectId}/issues?page=1&pageSize=50"))
            .Content.ReadFromJsonAsync<PageDto>();

        Assert.Equal(6, filtered.TotalCount);
        Assert.Equal(unfiltered!.TotalCount, filtered.TotalCount);
    }

    [Fact]
    public async Task Paging_and_totalcount_are_correct_over_a_filtered_set()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        // parent IsNull -> 4 matches (ACME-1,2,3,4), newest-updated first; page 2 of size 2 -> ACME-3, ACME-4.
        var page2 = await QueryOkAsync(client, projectId,
            Req(new() { Cond("parent", "IsNull") }, page: 2, pageSize: 2));

        Assert.Equal(4, page2.TotalCount);
        Assert.Equal(2, page2.Page);
        Assert.Equal(2, page2.PageSize);
        Assert.Equal(new[] { "ACME-3", "ACME-4" }, page2.Items.Select(i => i.JiraKey));
    }

    // ---- folded-in fix: dict-field name resolution must be case-insensitive like the catalog ----

    [Fact]
    public async Task Dict_field_casing_does_not_break_name_resolution()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        // "Status" (non-canonical casing) passes the case-insensitive catalog lookup; the resolver must
        // still be handed the canonical "status" key, or this silently returns zero rows.
        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("Status", "Eq", value: "In Progress") }));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ACME-1", page.Items[0].JiraKey);
    }

    // ---- folded-in fix: storyPoints Neq on a NULL decimal (EF null-semantics rewrites `!= 5` to also
    // ---- match NULLs — the same "Neq includes unassigned" rule the dict fields follow) ----

    [Fact]
    public async Task StoryPoints_neq_includes_the_null_points_issue()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
            await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
            await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StoryPoints"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,@ji,@k,@s,@sp,now(),now(),now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("ji", "10001"), ("k", "ACME-1"), ("s", "Has points"), ("sp", 5m));
            await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StoryPoints"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,@ji,@k,@s,@sp,now(),now(),now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("ji", "10002"), ("k", "ACME-2"), ("s", "No points"), ("sp", (decimal?)null));
        }

        var page = await QueryOkAsync(client, projId, Req(new() { Cond("storyPoints", "Neq", value: 5) }));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("ACME-2", page.Items[0].JiraKey); // NULL-points issue included; the 5-points issue excluded
    }

    // ---- 400 validation cases ----

    [Fact]
    public async Task Unknown_field_returns_400()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var resp = await PostQueryAsync(client, projectId, Req(new() { Cond("bogus", "Eq", value: "x") }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Disallowed_operator_for_field_returns_400()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        // createdAt only allows Lt/Lte/Gt/Gte.
        var resp = await PostQueryAsync(client, projectId,
            Req(new() { Cond("createdAt", "Contains", value: "2024-01-01T00:00:00Z") }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Value_type_mismatch_returns_400()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        // storyPoints requires a numeric value; "abc" is a string.
        var resp = await PostQueryAsync(client, projectId,
            Req(new() { Cond("storyPoints", "Gte", value: "abc") }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Too_many_conditions_returns_400()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var conditions = Enumerable.Range(0, 21).Select(n => Cond("jiraKey", "Eq", value: $"K{n}")).ToList();
        var resp = await PostQueryAsync(client, projectId, Req(conditions));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Too_many_in_values_returns_400()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var values = Enumerable.Range(0, 101).Select(n => $"V{n}").ToArray();
        var resp = await PostQueryAsync(client, projectId,
            Req(new() { Cond("status", "In", values: values) }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- 404 ----

    [Fact]
    public async Task Unknown_project_returns_404()
    {
        var (client, _) = await AuthedTenantAsync();

        var resp = await PostQueryAsync(client, Guid.NewGuid(), Req(new()));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
