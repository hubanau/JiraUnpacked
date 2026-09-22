// tests/JiraUnpacked.Tests/Integration/ConnectionProjectLifecycleTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Removing connections and projects, rotating a connection's API token, and pausing a
/// project's scheduled sync.
/// </summary>
public sealed class ConnectionProjectLifecycleTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public ConnectionProjectLifecycleTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId,
        int SyncIntervalMinutes, bool SyncPaused, DateTime CreatedAtUtc);

    private static void AssertAllZero(Dictionary<string, long> counts) =>
        Assert.All(counts, kv => Assert.True(kv.Value == 0, $"{kv.Key} still has {kv.Value} row(s)"));

    private static void AssertAllPresent(Dictionary<string, long> counts) =>
        Assert.All(counts, kv => Assert.True(kv.Value > 0, $"{kv.Key} lost its rows"));

    // ---------------------------------------------------------------- DELETE /api/projects/{id}

    [Fact]
    public async Task Delete_project_removes_every_derived_row_and_keeps_the_connection_and_its_siblings()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var seed = await LifecycleTestData.SeedConnectionAsync(tenant, projects: 2);
        var (doomed, sibling) = (seed.ProjectIds[0], seed.ProjectIds[1]);
        AssertAllPresent(await LifecycleTestData.ProjectCountsAsync(doomed));

        var resp = await client.DeleteAsync($"/api/projects/{doomed}");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        AssertAllZero(await LifecycleTestData.ProjectCountsAsync(doomed));
        AssertAllPresent(await LifecycleTestData.ProjectCountsAsync(sibling));
        AssertAllPresent(await LifecycleTestData.ConnectionCountsAsync(seed.ConnectionId));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{doomed}")).StatusCode);
    }

    [Fact]
    public async Task Delete_project_of_another_tenant_answers_404_and_deletes_nothing()
    {
        var (_, owner) = await MetricsTestData.AuthedTenantAsync(_factory);
        var seed = await LifecycleTestData.SeedConnectionAsync(owner, projects: 1);
        var (intruder, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        var resp = await intruder.DeleteAsync($"/api/projects/{seed.ProjectIds[0]}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        AssertAllPresent(await LifecycleTestData.ProjectCountsAsync(seed.ProjectIds[0]));
    }

    [Fact]
    public async Task Delete_unknown_project_answers_404()
    {
        var (client, _) = await MetricsTestData.AuthedTenantAsync(_factory);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/projects/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Delete_project_while_a_sync_is_running_answers_409_and_deletes_nothing()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var seed = await LifecycleTestData.SeedConnectionAsync(tenant, projects: 1);
        await LifecycleTestData.SeedRunningRunAsync(tenant, seed.ProjectIds[0]);

        var resp = await client.DeleteAsync($"/api/projects/{seed.ProjectIds[0]}");

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        AssertAllPresent(await LifecycleTestData.ProjectCountsAsync(seed.ProjectIds[0]));
    }

    // ------------------------------------------------------------- DELETE /api/connections/{id}

    [Fact]
    public async Task Delete_connection_removes_its_projects_their_data_and_its_dictionaries()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var seed = await LifecycleTestData.SeedConnectionAsync(tenant, projects: 2);
        var other = await LifecycleTestData.SeedConnectionAsync(tenant, projects: 1);

        var resp = await client.DeleteAsync($"/api/connections/{seed.ConnectionId}");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        foreach (var p in seed.ProjectIds) AssertAllZero(await LifecycleTestData.ProjectCountsAsync(p));
        AssertAllZero(await LifecycleTestData.ConnectionCountsAsync(seed.ConnectionId));

        // The same tenant's other connection is untouched.
        AssertAllPresent(await LifecycleTestData.ConnectionCountsAsync(other.ConnectionId));
        AssertAllPresent(await LifecycleTestData.ProjectCountsAsync(other.ProjectIds[0]));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/connections/{seed.ConnectionId}")).StatusCode);
    }

    [Fact]
    public async Task Delete_connection_without_projects_succeeds()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var seed = await LifecycleTestData.SeedConnectionAsync(tenant, projects: 0);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/connections/{seed.ConnectionId}")).StatusCode);
        AssertAllZero(await LifecycleTestData.ConnectionCountsAsync(seed.ConnectionId));
    }

    [Fact]
    public async Task Delete_connection_of_another_tenant_answers_404_and_deletes_nothing()
    {
        var (_, owner) = await MetricsTestData.AuthedTenantAsync(_factory);
        var seed = await LifecycleTestData.SeedConnectionAsync(owner, projects: 1);
        var (intruder, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        var resp = await intruder.DeleteAsync($"/api/connections/{seed.ConnectionId}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        AssertAllPresent(await LifecycleTestData.ConnectionCountsAsync(seed.ConnectionId));
        AssertAllPresent(await LifecycleTestData.ProjectCountsAsync(seed.ProjectIds[0]));
    }

    [Fact]
    public async Task Delete_connection_while_any_project_is_syncing_answers_409_and_deletes_nothing()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var seed = await LifecycleTestData.SeedConnectionAsync(tenant, projects: 2);
        await LifecycleTestData.SeedRunningRunAsync(tenant, seed.ProjectIds[1]);

        var resp = await client.DeleteAsync($"/api/connections/{seed.ConnectionId}");

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        AssertAllPresent(await LifecycleTestData.ConnectionCountsAsync(seed.ConnectionId));
        foreach (var p in seed.ProjectIds) AssertAllPresent(await LifecycleTestData.ProjectCountsAsync(p));
    }

    // -------------------------------------------------------------- PATCH /api/connections/{id}

    private void StubMyself(int status)
    {
        _jira.Reset();
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(status).WithBodyAsJson(new { accountId = "a" }));
    }

    private async Task<(HttpClient client, ConnectionDto conn)> CreatedConnectionAsync()
    {
        StubMyself(200);
        var client = await TestData.AuthedClientAsync(_factory);
        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "original-token" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (client, (await resp.Content.ReadFromJsonAsync<ConnectionDto>())!);
    }

    private string Unprotect(string ciphertext)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITokenProtector>().Unprotect(ciphertext);
    }

    [Fact]
    public async Task Token_rotation_validates_then_stores_the_new_token_encrypted_and_never_returns_it()
    {
        var (client, conn) = await CreatedConnectionAsync();
        StubMyself(200);

        var resp = await client.PatchAsync($"/api/connections/{conn.Id}",
            JsonContent.Create(new { email = "new@acme.com", apiToken = "rotated-token" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("rotated-token", body);
        var view = (await resp.Content.ReadFromJsonAsync<ConnectionDto>())!;
        Assert.Equal("new@acme.com", view.Email);

        // Jira was asked about the NEW pair before anything was stored.
        var sent = Assert.Single(_jira.LogEntries, e => e.RequestMessage.Path == "/rest/api/3/myself");
        var expectedAuth = "Basic " + Convert.ToBase64String("new@acme.com:rotated-token"u8.ToArray());
        var authHeader = sent.RequestMessage.Headers!
            .First(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Value.Single();
        Assert.Equal(expectedAuth, authHeader);

        var stored = await LifecycleTestData.StoredTokenCiphertextAsync(conn.Id);
        Assert.NotEqual("rotated-token", stored);
        Assert.Equal("rotated-token", Unprotect(stored));
    }

    [Fact]
    public async Task Token_rotation_without_email_keeps_the_stored_email()
    {
        var (client, conn) = await CreatedConnectionAsync();
        StubMyself(200);

        var resp = await client.PatchAsync($"/api/connections/{conn.Id}",
            JsonContent.Create(new { apiToken = "rotated-token" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ops@acme.com", (await resp.Content.ReadFromJsonAsync<ConnectionDto>())!.Email);
        Assert.Equal("rotated-token", Unprotect(await LifecycleTestData.StoredTokenCiphertextAsync(conn.Id)));
    }

    [Fact]
    public async Task Token_rotation_with_credentials_jira_rejects_answers_400_and_changes_nothing()
    {
        var (client, conn) = await CreatedConnectionAsync();
        var before = await LifecycleTestData.StoredTokenCiphertextAsync(conn.Id);
        StubMyself(401);

        var resp = await client.PatchAsync($"/api/connections/{conn.Id}",
            JsonContent.Create(new { email = "new@acme.com", apiToken = "wrong-token" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("credentials could not be validated", await resp.Content.ReadAsStringAsync());
        Assert.Equal(before, await LifecycleTestData.StoredTokenCiphertextAsync(conn.Id));
        Assert.Equal("ops@acme.com", (await (await client.GetAsync($"/api/connections/{conn.Id}"))
            .Content.ReadFromJsonAsync<ConnectionDto>())!.Email);
    }

    [Fact]
    public async Task Token_rotation_while_jira_is_unreachable_answers_502_and_changes_nothing()
    {
        var (client, conn) = await CreatedConnectionAsync();
        var before = await LifecycleTestData.StoredTokenCiphertextAsync(conn.Id);
        StubMyself(500);

        var resp = await client.PatchAsync($"/api/connections/{conn.Id}",
            JsonContent.Create(new { apiToken = "rotated-token" }));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal(before, await LifecycleTestData.StoredTokenCiphertextAsync(conn.Id));
    }

    [Fact]
    public async Task Token_rotation_requires_a_token()
    {
        var (client, conn) = await CreatedConnectionAsync();

        var resp = await client.PatchAsync($"/api/connections/{conn.Id}",
            JsonContent.Create(new { email = "new@acme.com" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Token_rotation_on_another_tenants_connection_answers_404_without_contacting_jira()
    {
        var (_, conn) = await CreatedConnectionAsync();
        var before = await LifecycleTestData.StoredTokenCiphertextAsync(conn.Id);
        StubMyself(200);
        var intruder = await TestData.AuthedClientAsync(_factory);

        var resp = await intruder.PatchAsync($"/api/connections/{conn.Id}",
            JsonContent.Create(new { email = "evil@x.com", apiToken = "stolen" }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(_jira.LogEntries);
        Assert.Equal(before, await LifecycleTestData.StoredTokenCiphertextAsync(conn.Id));
    }

    // ------------------------------------------------- PATCH /api/projects/{id} â€” pause/resume

    [Fact]
    public async Task Pause_and_resume_round_trip_and_leave_the_interval_alone()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var seed = await LifecycleTestData.SeedConnectionAsync(tenant, projects: 1);
        var id = seed.ProjectIds[0];

        var initial = (await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{id}"))!;
        Assert.False(initial.SyncPaused);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PatchAsync($"/api/projects/{id}", JsonContent.Create(new { syncPaused = true }))).StatusCode);
        var paused = (await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{id}"))!;
        Assert.True(paused.SyncPaused);
        Assert.Equal(60, paused.SyncIntervalMinutes);

        var listed = (await client.GetFromJsonAsync<List<ProjectDto>>($"/api/connections/{seed.ConnectionId}/projects"))!;
        Assert.True(Assert.Single(listed).SyncPaused);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PatchAsync($"/api/projects/{id}", JsonContent.Create(new { syncPaused = false }))).StatusCode);
        Assert.False((await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{id}"))!.SyncPaused);
    }

    [Fact]
    public async Task Patch_can_set_interval_and_pause_together()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var id = (await LifecycleTestData.SeedConnectionAsync(tenant, projects: 1)).ProjectIds[0];

        var resp = await client.PatchAsync($"/api/projects/{id}",
            JsonContent.Create(new { syncIntervalMinutes = 180, syncPaused = true }));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var p = (await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{id}"))!;
        Assert.Equal(180, p.SyncIntervalMinutes);
        Assert.True(p.SyncPaused);
    }

    [Fact]
    public async Task Patch_with_nothing_to_change_answers_400()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var id = (await LifecycleTestData.SeedConnectionAsync(tenant, projects: 1)).ProjectIds[0];

        var resp = await client.PatchAsync($"/api/projects/{id}", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_pause_with_an_interval_below_60_is_rejected_and_changes_nothing()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var id = (await LifecycleTestData.SeedConnectionAsync(tenant, projects: 1)).ProjectIds[0];

        var resp = await client.PatchAsync($"/api/projects/{id}",
            JsonContent.Create(new { syncIntervalMinutes = 30, syncPaused = true }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False((await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{id}"))!.SyncPaused);
    }

    [Fact]
    public async Task Pausing_another_tenants_project_answers_404_and_changes_nothing()
    {
        var (owner, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var id = (await LifecycleTestData.SeedConnectionAsync(tenant, projects: 1)).ProjectIds[0];
        var (intruder, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        var resp = await intruder.PatchAsync($"/api/projects/{id}", JsonContent.Create(new { syncPaused = true }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.False((await owner.GetFromJsonAsync<ProjectDto>($"/api/projects/{id}"))!.SyncPaused);
    }

    [Fact]
    public async Task A_paused_project_can_still_be_synced_manually()
    {
        // Created through the API against WireMock, so the background run that follows talks to
        // the stand-in and never to a real host.
        var (client, conn) = await CreatedConnectionAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PatchAsync($"/api/projects/{project.Id}", JsonContent.Create(new { syncPaused = true }));

        var resp = await client.PostAsync($"/api/projects/{project.Id}/sync", null);

        // Accepted means the run was claimed; how it then fares against the stand-in is irrelevant
        // here — pausing stops the scheduler, not the user.
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    // ------------------------------------------------------- base-URL policy, through the API

    [Theory]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://intranet.example.com")]
    [InlineData("https://user:pw@acme.atlassian.net")]
    public async Task Create_refuses_a_base_url_outside_the_policy_without_contacting_it(string baseUrl)
    {
        StubMyself(200);
        var client = await TestData.AuthedClientAsync(_factory);

        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl, email = "ops@acme.com", apiToken = "t" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<ConnectionDto>>("/api/connections"))!);
    }

    [Fact]
    public async Task Create_stores_the_base_url_without_a_trailing_slash()
    {
        StubMyself(200);
        var client = await TestData.AuthedClientAsync(_factory);

        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url + "/", email = "ops@acme.com", apiToken = "t" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(_jira.Url!.TrimEnd('/'), (await resp.Content.ReadFromJsonAsync<ConnectionDto>())!.BaseUrl);
    }
}
