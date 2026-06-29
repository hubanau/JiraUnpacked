using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

public sealed class JiraApiClientDiscoveryTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private IJiraApiClient Client() => new JiraApiClient(new HttpClient());
    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "t");
    public void Dispose() => _server.Stop();

    [Fact]
    public async Task GetFieldsAsync_maps_id_name_custom()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/field").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new { id = "summary", name = "Summary", custom = false },
                new { id = "customfield_10020", name = "Sprint", custom = true }
            }));

        var fields = await Client().GetFieldsAsync(Creds(), CancellationToken.None);

        Assert.Equal(2, fields.Count);
        Assert.Contains(fields, f => f.Id == "customfield_10020" && f.Name == "Sprint" && f.Custom);
    }

    [Fact]
    public async Task GetProjectsAsync_reads_the_values_array()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/project/search").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                values = new object[] { new { id = "10001", key = "ACME", name = "Acme" } }
            }));

        var projects = await Client().GetProjectsAsync(Creds(), CancellationToken.None);
        var p = Assert.Single(projects);
        Assert.Equal(("10001", "ACME", "Acme"), (p.Id, p.Key, p.Name));
    }

    [Fact]
    public async Task GetProjectAsync_returns_summary_on_200_and_null_on_404()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));
        _server.Given(Request.Create().WithPath("/rest/api/3/project/NOPE").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        Assert.Equal("10001", (await Client().GetProjectAsync(Creds(), "ACME", CancellationToken.None))!.Id);
        Assert.Null(await Client().GetProjectAsync(Creds(), "NOPE", CancellationToken.None));
    }

    [Fact]
    public async Task GetStatusesAsync_flattens_and_dedupes_across_issue_types()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/project/ACME/statuses").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new { id = "10", name = "Story", statuses = new object[] {
                    new { id = "1", name = "To Do" }, new { id = "3", name = "Done" } } },
                new { id = "11", name = "Bug", statuses = new object[] {
                    new { id = "1", name = "To Do" }, new { id = "2", name = "In Progress" } } }
            }));

        var statuses = await Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None);

        Assert.Equal(3, statuses.Count); // 1,3,2 deduped by id
        Assert.Contains(statuses, s => s.Id == "2" && s.Name == "In Progress");
    }

    [Fact]
    public async Task ParseJqlAsync_reports_valid_and_invalid()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                queries = new object[] { new { query = "project = ACME", errors = new string[0] } }
            }));
        var ok = await Client().ParseJqlAsync(Creds(), "project = ACME", CancellationToken.None);
        Assert.True(ok.Valid);

        _server.Reset();
        _server.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                queries = new object[] { new { query = "bad jql", errors = new[] { "Unable to parse" } } }
            }));
        var bad = await Client().ParseJqlAsync(Creds(), "bad jql", CancellationToken.None);
        Assert.False(bad.Valid);
        Assert.Contains("Unable to parse", bad.Errors);
    }

    [Fact]
    public async Task Discovery_throws_JiraApiException_on_401()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/field").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));
        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().GetFieldsAsync(Creds(), CancellationToken.None));
        Assert.Equal(JiraErrorKind.Unauthorized, ex.Kind);
    }
}
