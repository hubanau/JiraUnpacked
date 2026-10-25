// tests/JiraUnpacked.Tests/Integration/IssueJqlEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// POST /issues/jql end to end, as the owning tenant: the tenant's own ids and mappings, the link
/// to its Jira, the same 404s and 400s as the query — and never another tenant's rows.
/// </summary>
public sealed class IssueJqlEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public IssueJqlEndpointTests(JiraUnpackedApiFactory f) => _factory = f;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record WarningDto(string Message);

    private sealed record JqlDto(string Jql, string Url, List<WarningDto> Warnings);

    private static object Cond(string field, string op, object? value = null) => new { field, op, value };

    private static object Filter(params object[] conditions) => new { logic = "And", conditions };

    private static Task<HttpResponseMessage> PostJqlAsync(HttpClient client, Guid projectId, object body) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/issues/jql", body);

    private static async Task<JqlDto> JqlAsync(HttpClient client, Guid projectId, object body)
    {
        var resp = await PostJqlAsync(client, projectId, body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JqlDto>())!;
    }

    private static async Task<string> TitleAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    /// <summary>How many issues the LIST returns for this condition — the behaviour the JQL's names must agree with.</summary>
    private static async Task<int> ListCountAsync(HttpClient client, Guid projectId, object condition)
    {
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/query", new { filter = Filter(condition) });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalCount").GetInt32();
    }

    private static bool Before(string text, string first, string second) =>
        text.IndexOf(first, StringComparison.Ordinal) < text.IndexOf(second, StringComparison.Ordinal);

    [Fact]
    public async Task Translates_with_this_tenants_ids_and_links_to_the_connections_jira()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await JqlTestData.SetBaseUrlAsync(ctx, "https://acme.atlassian.net/");
        await JqlTestData.SeedUserAsync(ctx, "Alice", "acc-alice");
        await JqlTestData.SeedSprintAsync(ctx, "Sprint 7", "107");
        await JqlTestData.SeedTeamAsync(ctx, "Core", "team-1");
        await JqlTestData.SeedMappingAsync(ctx, "Team", "customfield_10001");
        await JqlTestData.SeedMappingAsync(ctx, "StoryPoints", "customfield_10016");

        var body = await JqlAsync(client, ctx.ProjectId, new
        {
            filter = new
            {
                logic = "And",
                conditions = new[]
                {
                    Cond("assignee", "Eq", "Alice"), Cond("sprint", "Eq", "Sprint 7"),
                    Cond("team", "Eq", "Core"), Cond("storyPoints", "Gte", 3),
                },
            },
            sortBy = "priority",
            sortDescending = false,
        });

        const string expected =
            "project = \"ACME\" AND (assignee = \"acc-alice\" AND sprint = 107 AND cf[10001] = \"team-1\" AND cf[10016] >= 3) ORDER BY priority ASC, key ASC";
        Assert.Equal(expected, body.Jql);
        // One slash before issues/, whatever the stored base URL ends with; the JQL fully encoded.
        Assert.Equal("https://acme.atlassian.net/issues/?jql=" + Uri.EscapeDataString(expected), body.Url);
        Assert.DoesNotContain(" ", body.Url);
        Assert.DoesNotContain("\"", body.Url);
        Assert.Contains("every sprint it has been in", Assert.Single(body.Warnings).Message);
    }

    [Theory]
    [InlineData("https://acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net/")]
    [InlineData("https://acme.atlassian.net//")]
    public async Task The_link_has_exactly_one_slash_before_issues_whatever_the_base_url_ends_with(string baseUrl)
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await JqlTestData.SetBaseUrlAsync(ctx, baseUrl);

        var body = await JqlAsync(client, ctx.ProjectId, new { });

        Assert.Equal("https://acme.atlassian.net/issues/?jql=" + Uri.EscapeDataString(body.Jql), body.Url);
    }

    [Fact]
    public async Task A_name_several_rows_share_lists_every_id_in_a_stable_order()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await JqlTestData.SeedMappingAsync(ctx, "Team", "customfield_10001");
        // Two Jira accounts named Sam, two boards' "Sprint 7", two teams named Core — seeded
        // out of id order so the order in the JQL cannot be insertion order by accident.
        await JqlTestData.SeedUserAsync(ctx, "Sam", "acc-sam-b");
        await JqlTestData.SeedUserAsync(ctx, "Sam", "acc-sam-a");
        await JqlTestData.SeedSprintAsync(ctx, "Sprint 7", "207");
        await JqlTestData.SeedSprintAsync(ctx, "Sprint 7", "107");
        await JqlTestData.SeedTeamAsync(ctx, "Core", "team-b");
        await JqlTestData.SeedTeamAsync(ctx, "Core", "team-a");

        var body = await JqlAsync(client, ctx.ProjectId, new
        {
            filter = Filter(Cond("assignee", "Eq", "Sam"), Cond("sprint", "Eq", "Sprint 7"), Cond("team", "Eq", "Core")),
        });

        foreach (var (a, b) in new[] { ("\"acc-sam-a\"", "\"acc-sam-b\""), ("107", "207"), ("\"team-a\"", "\"team-b\"") })
        {
            Assert.Contains(a, body.Jql);
            Assert.Contains(b, body.Jql);
            Assert.True(Before(body.Jql, a, b), $"{a} should precede {b} in {body.Jql}");
        }
        Assert.DoesNotContain(body.Warnings, w => w.Message.StartsWith("Left out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Names_match_exactly_as_the_list_matches_them_so_a_different_case_is_not_that_row()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await JqlTestData.SeedMappingAsync(ctx, "Team", "customfield_10001");
        var alice = await JqlTestData.SeedUserAsync(ctx, "Alice", "acc-alice");
        var sprint = await JqlTestData.SeedSprintAsync(ctx, "Sprint 7", "107");
        var core = await JqlTestData.SeedTeamAsync(ctx, "Core", "team-1");
        await JqlTestData.SeedIssueAsync(ctx, "ACME-1", assigneeId: alice, sprintId: sprint, teamId: core);

        foreach (var (field, exact, lowered, id) in new[]
        {
            ("assignee", "Alice", "alice", "acc-alice"),
            ("sprint", "Sprint 7", "sprint 7", "107"),
            ("team", "Core", "core", "team-1"),
        })
        {
            // The list: the exact name finds the issue, the same name in another case finds none…
            Assert.Equal(1, await ListCountAsync(client, ctx.ProjectId, Cond(field, "Eq", exact)));
            Assert.Equal(0, await ListCountAsync(client, ctx.ProjectId, Cond(field, "Eq", lowered)));

            // …and the JQL agrees: the exact name becomes the row's id, the other case is no row at all.
            var hit = await JqlAsync(client, ctx.ProjectId, new { filter = Filter(Cond(field, "Eq", exact)) });
            Assert.Contains(id, hit.Jql);
            var miss = await JqlAsync(client, ctx.ProjectId, new { filter = Filter(Cond(field, "Eq", lowered)) });
            Assert.DoesNotContain(id, miss.Jql);
            Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", miss.Jql);
            Assert.NotEmpty(miss.Warnings);
        }
    }

    [Fact]
    public async Task Another_tenants_accounts_sprints_teams_and_mappings_are_never_used()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        // Same connection, same project, same names — only the tenant differs.
        await JqlTestData.SeedUserAsync(ctx, "Alice", "acc-other", asTenant: other);
        await JqlTestData.SeedSprintAsync(ctx, "Sprint 7", "999", asTenant: other);
        await JqlTestData.SeedTeamAsync(ctx, "Core", "team-other", asTenant: other);
        await JqlTestData.SeedMappingAsync(ctx, "Team", "customfield_10001", asTenant: other);

        var body = await JqlAsync(client, ctx.ProjectId, new
        {
            filter = new
            {
                logic = "And",
                conditions = new[] { Cond("assignee", "Eq", "Alice"), Cond("sprint", "Eq", "Sprint 7"), Cond("team", "Eq", "Core") },
            },
        });

        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", body.Jql);
        foreach (var leaked in new[] { "acc-other", "999", "team-other", "cf[" })
            Assert.DoesNotContain(leaked, body.Jql);
        Assert.Equal(3, body.Warnings.Count(w => w.Message.StartsWith("Left out", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Another_tenants_row_sharing_a_name_with_this_tenants_adds_only_this_tenants_id()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await JqlTestData.SeedUserAsync(ctx, "Alice", "acc-mine");
        await JqlTestData.SeedUserAsync(ctx, "Alice", "acc-other", asTenant: other);
        await JqlTestData.SeedSprintAsync(ctx, "Sprint 7", "107");
        await JqlTestData.SeedSprintAsync(ctx, "Sprint 7", "999", asTenant: other);

        var body = await JqlAsync(client, ctx.ProjectId, new
        {
            filter = Filter(Cond("assignee", "Eq", "Alice"), Cond("sprint", "Eq", "Sprint 7")),
        });

        Assert.Contains("acc-mine", body.Jql);
        Assert.Contains("107", body.Jql);
        Assert.DoesNotContain("acc-other", body.Jql);
        Assert.DoesNotContain("999", body.Jql);
    }

    [Fact]
    public async Task An_unknown_or_another_tenants_project_is_404()
    {
        var (client, _) = await MetricsTestData.AuthedTenantAsync(_factory);
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_factory);
        var foreign = await MetricsTestData.SeedProjectAsync(other);

        Assert.Equal(HttpStatusCode.NotFound, (await PostJqlAsync(client, Guid.NewGuid(), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostJqlAsync(client, foreign.ProjectId, new { })).StatusCode);
    }

    [Fact]
    public async Task An_invalid_filter_or_sort_is_the_same_400_the_query_gives()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var bad = new object[]
        {
            new { filter = new { logic = "And", conditions = new[] { Cond("bogus", "Eq", "x") } } },
            new { filter = new { logic = "And", conditions = Array.Empty<object>() } },
            new { filter = new { logic = "And", conditions = new[] { Cond("storyPoints", "Eq", "three") } } },
            new { sortBy = "bogus" },
        };
        foreach (var body in bad)
        {
            var query = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query", body);
            var jql = await PostJqlAsync(client, ctx.ProjectId, body);
            Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, jql.StatusCode);
            Assert.Equal(await TitleAsync(query), await TitleAsync(jql));
        }
    }

    [Fact]
    public async Task No_filter_or_a_switched_off_one_is_the_whole_project()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", (await JqlAsync(client, ctx.ProjectId, new { })).Jql);
        var off = await JqlAsync(client, ctx.ProjectId, new
        {
            filter = new { logic = "And", conditions = new[] { Cond("status", "Eq", "Done") }, disabled = true },
        });
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", off.Jql);
        Assert.Empty(off.Warnings);
    }
}
