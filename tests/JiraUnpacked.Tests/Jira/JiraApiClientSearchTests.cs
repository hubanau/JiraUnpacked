// tests/JiraUnpacked.Tests/Jira/JiraApiClientSearchTests.cs
using System.Net;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

public sealed class JiraApiClientSearchTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private IJiraApiClient Client() => new JiraApiClient(new HttpClient());
    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "t");
    public void Dispose() => _server.Stop();

    [Fact]
    public async Task Returns_issues_and_next_page_token()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new object[] { new { id = "10001", key = "ACME-1" }, new { id = "10002", key = "ACME-2" } },
                nextPageToken = "TOKEN2"
            }));

        var page = await Client().SearchIssuesPageAsync(Creds(), "project = ACME", null, CancellationToken.None);

        Assert.Equal(2, page.Issues.Count);
        Assert.Equal("10001", page.Issues[0].JiraIssueId);
        Assert.Contains("\"key\":\"ACME-1\"", page.Issues[0].RawJson.Replace(" ", ""));
        Assert.Equal("TOKEN2", page.NextPageToken);
    }

    [Fact]
    public async Task Null_next_page_token_on_last_page()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new object[] { new { id = "10003", key = "ACME-3" } }
                // no nextPageToken
            }));

        var page = await Client().SearchIssuesPageAsync(Creds(), "project = ACME", "TOKEN2", CancellationToken.None);
        Assert.Single(page.Issues);
        Assert.Null(page.NextPageToken);
    }

    [Fact]
    public async Task Posts_the_jql_and_changelog_expand()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { issues = new object[0] }));

        await Client().SearchIssuesPageAsync(Creds(), "project = ACME AND updated >= \"2024-01-01 10:00\"", null, CancellationToken.None);

        var body = Assert.Single(_server.LogEntries).RequestMessage.Body!;
        Assert.Contains("updated >=", body);
        Assert.Contains("changelog", body);
    }

    [Fact]
    public async Task Retries_after_429_then_succeeds()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .InScenario("rl").WillSetStateTo("done")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .InScenario("rl").WhenStateIs("done")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new object[] { new { id = "10004", key = "ACME-4" } }
            }));

        var page = await Client().SearchIssuesPageAsync(Creds(), "project = ACME", null, CancellationToken.None);
        Assert.Single(page.Issues);
    }

    [Fact]
    public async Task Throws_RateLimited_after_exhausting_retries()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));

        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().SearchIssuesPageAsync(Creds(), "project = ACME", null, CancellationToken.None));
        Assert.Equal(JiraErrorKind.RateLimited, ex.Kind);
    }
}
