// tests/JiraUnpacked.Tests/Integration/FilterValuesTests.cs
using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The filter's value picker. The interesting property is not "returns values" but "returns the
/// values you can usefully pick" — which is narrower than the dictionary, and never another
/// tenant's.
/// </summary>
public sealed class FilterValuesTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public FilterValuesTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ValueDto(string Name, int Count);
    private sealed record ValuesResponse(string Field, List<ValueDto> Values, int EmptyCount);

    [Fact]
    public async Task Lists_only_the_statuses_some_issue_in_this_project_actually_has()
    {
        // SeedProjectAsync creates three statuses in the dictionary. Only one is used by an
        // issue. Offering the other two would produce a filter that matches nothing for a reason
        // invisible on screen — indistinguishable from having mistyped the value.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await MetricsTestData.SeedIssueAsync(
            ctx, "10001", "ACME-1", "only one status is in use",
            ctx.ToDoStatusId, null, DateTime.UtcNow);

        var body = await GetValuesAsync(client, ctx.ProjectId, "status");

        Assert.Equal(new[] { "To Do" }, body.Values.Select(v => v.Name));
    }

    [Fact]
    public async Task Never_lists_another_projects_values()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var mine = await MetricsTestData.SeedProjectAsync(tenant);
        var other = await MetricsTestData.SeedProjectAsync(tenant);

        await FilterValuesTestData.SeedStatusWithIssueAsync(mine, "Mine Only");
        await FilterValuesTestData.SeedStatusWithIssueAsync(other, "Other Project Only");

        var body = await GetValuesAsync(client, mine.ProjectId, "status");

        Assert.Contains("Mine Only", body.Values.Select(v => v.Name));
        Assert.DoesNotContain("Other Project Only", body.Values.Select(v => v.Name));
    }

    [Fact]
    public async Task Never_lists_another_tenants_values()
    {
        var (clientA, tenantA) = await MetricsTestData.AuthedTenantAsync(_factory);
        var (_, tenantB) = await MetricsTestData.AuthedTenantAsync(_factory);
        var a = await MetricsTestData.SeedProjectAsync(tenantA);
        var b = await MetricsTestData.SeedProjectAsync(tenantB);

        await FilterValuesTestData.SeedStatusWithIssueAsync(a, "Belongs To A");
        await FilterValuesTestData.SeedStatusWithIssueAsync(b, "Belongs To B");

        var body = await GetValuesAsync(clientA, a.ProjectId, "status");

        Assert.Contains("Belongs To A", body.Values.Select(v => v.Name));
        Assert.DoesNotContain("Belongs To B", body.Values.Select(v => v.Name));
    }

    [Theory]
    [InlineData("summary")]      // Text
    [InlineData("storyPoints")]  // Decimal
    [InlineData("createdAt")]    // DateTime
    [InlineData("parent")]       // ParentRef
    [InlineData("nonsense")]     // not a field at all
    [InlineData("")]             // omitted entirely
    public async Task A_field_with_no_value_list_is_a_400_naming_it(string field)
    {
        // Not an empty array: "this field has no picker" and "this project has no values" are
        // different answers, and a client that cannot tell them apart shows the wrong thing.
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/filter-values?field={field}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains($"'{field}'", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_project_is_404_before_the_field_is_even_considered()
    {
        // Checked first so a caller cannot probe field names against a project that is not theirs
        // and learn anything from the difference between 400 and 404.
        var (client, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        var resp = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/filter-values?field=nonsense");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Counts_each_value_and_the_issues_that_have_none()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var at = DateTime.UtcNow;
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.ToDoStatusId, null, at);
        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "b", ctx.ToDoStatusId, null, at);
        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "c", ctx.DoneStatusId, null, at);
        await MetricsTestData.SeedIssueAsync(ctx, "10004", "ACME-4", "d", null, null, at);
        await MetricsTestData.SeedIssueAsync(ctx, "10005", "ACME-5", "e", null, null, at);

        var status = await GetValuesAsync(client, ctx.ProjectId, "status");

        Assert.Equal(new[] { new ValueDto("Done", 1), new ValueDto("To Do", 2) }, status.Values);
        Assert.Equal(2, status.EmptyCount);

        // No issue here has an assignee: nothing to pick, and every issue is behind (Empty).
        var assignee = await GetValuesAsync(client, ctx.ProjectId, "assignee");

        Assert.Empty(assignee.Values);
        Assert.Equal(5, assignee.EmptyCount);
    }

    private static async Task<ValuesResponse> GetValuesAsync(HttpClient client, Guid projectId, string field)
    {
        var resp = await client.GetAsync($"/api/projects/{projectId}/filter-values?field={field}");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ValuesResponse>())!;
    }
}

internal static class FilterValuesTestData
{
    /// <summary>
    /// A status with a distinctive name, plus one issue using it — both are needed, because the
    /// endpoint answers from the ISSUES, so a dictionary row nothing points at must not appear.
    /// </summary>
    internal static async Task SeedStatusWithIssueAsync(SeedContext ctx, string statusName)
    {
        var statusId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();

        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"")
              VALUES (@i,@t,@c,@sid,@n,'new')",
            ("i", statusId), ("t", ctx.Tenant), ("c", ctx.ConnectionId),
            ("sid", statusId.ToString("N")[..8]), ("n", statusName));

        await MetricsTestData.SeedIssueAsync(
            ctx, Guid.NewGuid().ToString("N")[..10], $"K-{statusId.ToString("N")[..6]}",
            statusName, statusId, null, DateTime.UtcNow);
    }
}
