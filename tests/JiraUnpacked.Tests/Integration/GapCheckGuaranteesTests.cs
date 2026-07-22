// tests/JiraUnpacked.Tests/Integration/GapCheckGuaranteesTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The guarantees FR-E3 rests on: tenant isolation on the checks routes, epic classifications
/// surviving a re-derive, and a status re-classification changing results with zero Jira calls.
/// NOTE: this file starts no WireMock server — that absence IS the zero-Jira proof.
/// </summary>
public sealed class GapCheckGuaranteesTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public GapCheckGuaranteesTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ItemDto(string JiraIssueTypeId, string Name, string Class);
    private sealed record ChildDto(string JiraIssueId, string JiraKey, string? Summary, string? Status, string Class);
    private sealed record ViolationDto(string JiraIssueId, string JiraKey, string? Summary,
        string? Status, string Class, string? IssueType, List<ChildDto> Children);
    private sealed record PageDto(List<ViolationDto> Items, int Page, int PageSize, int TotalCount);
    private sealed record ResultDto(string Status, PageDto Violations);
    private sealed record IssueListItemDto(string JiraIssueId, string JiraKey);
    private sealed record IssuesPageDto(List<IssueListItemDto> Items, int Page, int PageSize, int TotalCount);

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_checks()
    {
        var (_, tenantA) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenantA);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");
        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);

        var (clientB, _) = await GapCheckTestData.AuthedTenantAsync(_factory);

        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/projects/{seed.ProjectId}/checks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done")).StatusCode);
    }

    [Fact]
    public async Task Unknown_project_returns_404_on_both_routes()
    {
        var (client, _) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var missing = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{missing}/checks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/projects/{missing}/checks/open-epic-all-children-done")).StatusCode);
    }

    [Fact]
    public async Task Epic_classification_survives_a_rederive()
    {
        // The derivation upserts IssueTypes by (JiraConnectionId, JiraIssueTypeId) and assigns only
        // Name on an existing row. If that ever changes, a sync would silently wipe the user's
        // epic classification and every parent-based check would go quiet.
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        await client.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
            new { issueTypes = new[] { new { jiraIssueTypeId = "1", @class = "Epic" } } });

        // Seed a raw snapshot for issue type "1" (Epic) so the re-derive actually walks the
        // dictionary-upsert path in ResolveTypeAsync — without a snapshot, /rederive is a no-op
        // and this test would pass even if the upsert started wiping Class on existing rows.
        await GapCheckTestData.SeedRawSnapshotAsync(seed, "10001", "ACME-1", "1", "Epic");

        await client.PostAsJsonAsync($"/api/projects/{seed.ProjectId}/rederive", new { });

        var items = await (await client.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types"))
            .Content.ReadFromJsonAsync<List<ItemDto>>();
        Assert.Equal("Epic", Assert.Single(items!, i => i.JiraIssueTypeId == "1").Class);

        // Prove the re-derive actually ran (and isn't itself vacuous): the seeded snapshot must
        // have materialized into a modeled issue.
        var issues = await (await client.GetAsync($"/api/projects/{seed.ProjectId}/issues"))
            .Content.ReadFromJsonAsync<IssuesPageDto>();
        Assert.Contains(issues!.Items, i => i.JiraKey == "ACME-1");
    }

    [Fact]
    public async Task Reclassifying_a_status_changes_the_result_without_touching_jira()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        // Epic ACME-1 sits in "To Do" (Other); its only child sits in "Done" (Done).
        // => epic not Done + at least one child + every child Done => the flagship fires.
        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);
        await GapCheckTestData.SeedIssueAsync(seed, "10002", "ACME-2", "child", seed.StoryTypeId, seed.DoneStatusId, "10001");

        Assert.Equal(1, (await RunFlagshipAsync(client, seed.ProjectId)).Violations.TotalCount);

        // Re-map the "Done" status to Other: the child stops counting as done, so the
        // violation disappears. No sync, no re-derive, no Jira call.
        await GapCheckTestData.SetStatusClassAsync(seed, "2", "Other");
        Assert.Equal(0, (await RunFlagshipAsync(client, seed.ProjectId)).Violations.TotalCount);

        // Map it back and the violation returns — proving the result tracks the mapping on
        // every request rather than being cached or baked in at derive time.
        await GapCheckTestData.SetStatusClassAsync(seed, "2", "Done");
        var restored = await RunFlagshipAsync(client, seed.ProjectId);
        Assert.Equal(1, restored.Violations.TotalCount);
        Assert.Equal("ACME-1", Assert.Single(restored.Violations.Items).JiraKey);
    }

    [Fact]
    public async Task Newly_discovered_issue_types_default_to_other()
    {
        // The EF creation path in DerivationService.ResolveTypeAsync must not silently default
        // a brand-new issue type to Epic (the CLR default of the old enum ordering). Seed a raw
        // snapshot referencing an issue type id that does NOT already exist in the fixture
        // ("1"/"2" are seeded by SeedAsync) so /rederive actually walks the INSERT branch.
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        await GapCheckTestData.SeedRawSnapshotAsync(seed, "90001", "BUG-1", "99", "Bug");

        await client.PostAsJsonAsync($"/api/projects/{seed.ProjectId}/rederive", new { });

        var items = await (await client.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types"))
            .Content.ReadFromJsonAsync<List<ItemDto>>();
        Assert.Equal("Other", Assert.Single(items!, i => i.JiraIssueTypeId == "99").Class);
    }

    [Fact]
    public async Task Partially_invalid_batch_persists_nothing()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        // One VALID change bundled with one unknown id: the batch must be rejected wholesale
        // and the valid change must NOT be applied.
        var resp = await client.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types", new
        {
            issueTypes = new[]
            {
                new { jiraIssueTypeId = "1", @class = "Epic" },
                new { jiraIssueTypeId = "does-not-exist", @class = "Epic" },
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // Type "1" must still be Other — nothing from the rejected batch was persisted.
        var items = await (await client.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types"))
            .Content.ReadFromJsonAsync<List<ItemDto>>();
        Assert.Equal("Other", Assert.Single(items!, i => i.JiraIssueTypeId == "1").Class);
    }

    private static async Task<ResultDto> RunFlagshipAsync(HttpClient client, Guid projectId) =>
        (await (await client.GetAsync($"/api/projects/{projectId}/checks/open-epic-all-children-done"))
            .Content.ReadFromJsonAsync<ResultDto>())!;
}
