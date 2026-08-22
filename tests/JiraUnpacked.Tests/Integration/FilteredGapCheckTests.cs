// tests/JiraUnpacked.Tests/Integration/FilteredGapCheckTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// POST api/projects/{projectId}/checks/{checkId} — the same curated checks, restricted to the
/// issues a filter selects.
///
/// The rule under test: the filter chooses which issues are EXAMINED, never the evidence used
/// to judge them. Applying the filter to the check's context instead would let a filter hide
/// the children that make an epic a violation and report it clean — a green result to a
/// question nobody asked, which is the worst failure a report here can produce.
/// </summary>
public sealed class FilteredGapCheckTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public FilteredGapCheckTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Check = "done-epic-open-children";

    /// <summary>A Done epic with one open child, both with distinct jiraKeys to filter on.</summary>
    private async Task<(HttpClient client, CheckSeed seed)> SeedDoneEpicWithOpenChildAsync(bool classifyEpic = true)
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        if (classifyEpic) await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        await GapCheckTestData.SeedIssueAsync(
            seed, "100", "ACME-EPIC", "A finished epic", seed.EpicTypeId, seed.DoneStatusId, null);
        await GapCheckTestData.SeedIssueAsync(
            seed, "200", "ACME-CHILD", "Still open", seed.StoryTypeId, seed.ToDoStatusId, "100");

        return (client, seed);
    }

    private static async Task<GapCheckResult> RunAsync(
        HttpClient client, CheckSeed seed, object? filter = null)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{seed.ProjectId}/checks/{Check}", filter ?? new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<GapCheckResult>())!;
    }

    private static object FilterOn(string jiraKey) =>
        new { conditions = new[] { new { field = "jiraKey", op = "Eq", value = jiraKey } } };

    [Fact]
    public async Task Epic_is_still_a_violation_when_the_filter_excludes_its_only_open_child()
    {
        var (client, seed) = await SeedDoneEpicWithOpenChildAsync();

        // The filter selects the EPIC only. Its open child is excluded — and the epic must
        // still be reported, still carrying that child as the evidence.
        var result = await RunAsync(client, seed, FilterOn("ACME-EPIC"));

        Assert.Equal("Ok", result.Status);
        Assert.Equal(1, result.Violations.TotalCount);

        var violation = Assert.Single(result.Violations.Items);
        Assert.Equal("ACME-EPIC", violation.JiraKey);

        // The load-bearing assertion. Filtering the check's CONTEXT rather than its subjects
        // would leave this list empty, the epic would fall out of the violations entirely, and
        // a broken epic would be reported clean.
        var child = Assert.Single(violation.Children);
        Assert.Equal("ACME-CHILD", child.JiraKey);
    }

    [Fact]
    public async Task Filtering_to_a_non_subject_returns_no_violations_while_unfiltered_returns_one()
    {
        var (client, seed) = await SeedDoneEpicWithOpenChildAsync();

        // Proves the subject filter is genuinely applied — without this, the test above would
        // also pass on an implementation that ignored the filter completely.
        var unfiltered = await RunAsync(client, seed);
        Assert.Equal(1, unfiltered.Violations.TotalCount);

        var filtered = await RunAsync(client, seed, FilterOn("ACME-CHILD"));
        Assert.Equal("Ok", filtered.Status);
        Assert.Equal(0, filtered.Violations.TotalCount);
        Assert.Empty(filtered.Violations.Items);
    }

    [Fact]
    public async Task NotConfigured_survives_filtering_and_never_becomes_a_clean_result()
    {
        // No issue type classified as Epic, so the check cannot run at all.
        var (client, seed) = await SeedDoneEpicWithOpenChildAsync(classifyEpic: false);

        var filtered = await RunAsync(client, seed, FilterOn("ACME-EPIC"));

        // Both NotConfigured and Ok-with-nothing return zero violations and mean opposite
        // things. A filter cannot make a check configured, so the status must survive intact —
        // otherwise filtering would silently convert "could not run" into "nothing found".
        Assert.Equal("NotConfigured", filtered.Status);
        Assert.Equal(0, filtered.Violations.TotalCount);
    }

    [Fact]
    public async Task Bad_filter_returns_400_and_an_unknown_check_still_returns_404()
    {
        var (client, seed) = await SeedDoneEpicWithOpenChildAsync();

        var bad = await client.PostAsJsonAsync($"/api/projects/{seed.ProjectId}/checks/{Check}",
            new { conditions = new[] { new { field = "secret", op = "Eq", value = "x" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Unknown field 'secret'", await bad.Content.ReadAsStringAsync());

        var unknown = await client.PostAsJsonAsync(
            $"/api/projects/{seed.ProjectId}/checks/no-such-check", new { });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_bad_filter_at_another_tenants_project_returns_404_not_400()
    {
        var (_, seedA) = await SeedDoneEpicWithOpenChildAsync();
        var (clientB, _) = await GapCheckTestData.AuthedTenantAsync(_factory);

        // The project check runs FIRST. Answering 400 here would tell B that A's project id is
        // real — the difference between "no such project" and "your filter is wrong" is itself
        // an existence leak.
        var resp = await clientB.PostAsJsonAsync($"/api/projects/{seedA.ProjectId}/checks/{Check}",
            new { conditions = new[] { new { field = "secret", op = "Eq", value = "x" } } });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
