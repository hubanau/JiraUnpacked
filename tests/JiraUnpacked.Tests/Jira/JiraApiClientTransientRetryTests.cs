// tests/JiraUnpacked.Tests/Jira/JiraApiClientTransientRetryTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

/// <summary>
/// Transient-failure retry, now shared by every Jira call rather than only the two paged ones.
/// A single 5xx or dropped connection used to fail a whole sync run, and the discovery calls
/// had no retry at all.
///
/// Every failing response here carries <c>Retry-After: 0</c> so the tests exercise the retry
/// decision rather than the backoff schedule.
/// </summary>
public sealed class JiraApiClientTransientRetryTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private IJiraApiClient Client() => new JiraApiClient(new HttpClient());
    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "t");
    public void Dispose() => _server.Stop();

    private const string StatusesPath = "/rest/api/3/project/ACME/statuses";

    [Fact]
    public async Task A_discovery_call_survives_a_transient_5xx()
    {
        // The gap this closes: discovery had no retry, so one blip while reading a project's
        // statuses failed the request outright.
        _server.Given(Request.Create().WithPath(StatusesPath).UsingGet())
            .InScenario("blip").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503).WithHeader("Retry-After", "0"));

        _server.Given(Request.Create().WithPath(StatusesPath).UsingGet())
            .InScenario("blip").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new { statuses = new[] { new { id = "1", name = "To Do" } } },
            }));

        var statuses = await Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None);

        Assert.Single(statuses);
        Assert.Equal("To Do", statuses[0].Name);
    }

    [Fact]
    public async Task A_persistent_5xx_still_fails_as_upstream()
    {
        // Retry must not turn a genuinely broken Jira into a hang or a wrong answer.
        _server.Given(Request.Create().WithPath(StatusesPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithHeader("Retry-After", "0"));

        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None));

        Assert.Equal(JiraErrorKind.Upstream, ex.Kind);
    }

    [Fact]
    public async Task A_rejected_credential_is_not_retried()
    {
        // 401 is an answer, not a blip. Retrying it would burn attempts against a lockout
        // policy and delay a user's error message for no reason.
        _server.Given(Request.Create().WithPath(StatusesPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));

        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None));

        Assert.Equal(JiraErrorKind.Unauthorized, ex.Kind);
        Assert.Single(_server.LogEntries);
    }

    [Fact]
    public async Task A_missing_project_is_not_retried()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/project/NOPE").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var project = await Client().GetProjectAsync(Creds(), "NOPE", CancellationToken.None);

        Assert.Null(project);
        Assert.Single(_server.LogEntries);
    }

    [Fact]
    public async Task A_changelog_page_survives_a_transient_5xx()
    {
        const string path = "/rest/api/3/issue/10001/changelog";

        _server.Given(Request.Create().WithPath(path).UsingGet())
            .InScenario("blip").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(502).WithHeader("Retry-After", "0"));

        _server.Given(Request.Create().WithPath(path).UsingGet())
            .InScenario("blip").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                values = new object[] { new { id = "1" } },
                total = 1,
                isLast = true,
            }));

        var page = await Client().GetIssueChangelogPageAsync(Creds(), "10001", 0, CancellationToken.None);

        Assert.Single(page.ValuesJson);
        Assert.True(page.IsLast);
    }

    [Fact]
    public async Task A_search_page_survives_a_transient_5xx_even_though_it_posts_a_body()
    {
        // The reason the helper takes a factory rather than a request: an HttpRequestMessage
        // cannot be sent twice, so a retry that reused one would throw on the second attempt
        // instead of retrying. A POST with content is where that breaks first.
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .InScenario("blip").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503).WithHeader("Retry-After", "0"));

        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .InScenario("blip").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new object[] { new { id = "10001", key = "ACME-1" } },
            }));

        var page = await Client().SearchIssuesPageAsync(
            Creds(), "project = ACME", null, CancellationToken.None);

        Assert.Single(page.Issues);
        Assert.Equal("10001", page.Issues[0].JiraIssueId);
    }
}
