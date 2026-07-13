using System.Net;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

public sealed class JiraApiClientChangelogTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private IJiraApiClient Client() => new JiraApiClient(new HttpClient());
    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "t");
    public void Dispose() => _server.Stop();

    [Fact]
    public async Task Reads_values_total_and_islast()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                startAt = 0, maxResults = 100, total = 1, isLast = true,
                values = new object[] { new { id = "h1", created = "2024-01-01T00:00:00.000+0000",
                    items = new object[] { new { field = "status", fromString = "To Do", toString = "Done" } } } }
            }));

        var page = await Client().GetIssueChangelogPageAsync(Creds(), "10001", 0, CancellationToken.None);

        Assert.Single(page.ValuesJson);
        Assert.Contains("\"id\":\"h1\"", page.ValuesJson[0].Replace(" ", ""));
        Assert.Equal(1, page.Total);
        Assert.True(page.IsLast);
    }

    [Fact]
    public async Task Passes_startAt_and_returns_not_last()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet().WithParam("startAt", "100"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            { startAt = 100, maxResults = 100, total = 250, isLast = false, values = new object[] { new { id = "h2" } } }));

        var page = await Client().GetIssueChangelogPageAsync(Creds(), "10001", 100, CancellationToken.None);
        Assert.False(page.IsLast);
        Assert.Equal(250, page.Total);
    }

    [Fact]
    public async Task Retries_after_429()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet())
            .InScenario("rl").WillSetStateTo("ok")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));
        _server.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet())
            .InScenario("rl").WhenStateIs("ok")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            { startAt = 0, maxResults = 100, total = 0, isLast = true, values = new object[0] }));

        var page = await Client().GetIssueChangelogPageAsync(Creds(), "10001", 0, CancellationToken.None);
        Assert.True(page.IsLast);
    }
}
