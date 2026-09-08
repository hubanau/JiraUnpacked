// tests/JiraUnpacked.Tests/Jira/JiraSearchRequestShapeTests.cs
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

/// <summary>
/// The shape of the body sent to /rest/api/3/search/jql, checked against Atlassian's published
/// contract for that endpoint.
///
/// This is the gap that let a 400 reach production: every other test in the suite stubs Jira with
/// WireMock, and a stub accepts whatever it is sent. The whole pipeline could pass end-to-end
/// while sending a body real Jira rejects outright. These tests assert what LEAVES the client,
/// which is the only part a stub cannot vouch for.
/// </summary>
public sealed class JiraSearchRequestShapeTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private JiraApiClient Client() => new(new HttpClient());
    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "t");
    public void Dispose() => _server.Stop();

    private const string SearchPath = "/rest/api/3/search/jql";

    private void StubEmptyPage() =>
        _server.Given(Request.Create().WithPath(SearchPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { issues = new object[0] }));

    private JsonElement SentBody()
    {
        var entry = _server.LogEntries.Single(e => e.RequestMessage.Path == SearchPath);
        return JsonDocument.Parse(entry.RequestMessage.Body!).RootElement.Clone();
    }

    [Fact]
    public async Task Expand_is_a_comma_delimited_string_not_an_array()
    {
        // Atlassian's spec for THIS endpoint: "unlike the majority of instances where expand is
        // specified, expand is defined as a comma-delimited string of values." Sending the array
        // that every other endpoint accepts is a 400 before the JQL is even parsed — which is
        // exactly how a sync failed with "Jira returned 400."
        StubEmptyPage();
        await Client().SearchIssuesPageAsync(Creds(), "project = \"ACME\"", null, CancellationToken.None);

        var expand = SentBody().GetProperty("expand");
        Assert.Equal(JsonValueKind.String, expand.ValueKind);
        Assert.Equal("changelog", expand.GetString());
    }

    [Fact]
    public async Task Fields_asks_for_all_of_them()
    {
        // This endpoint returns ONLY ids when fields is omitted, and DerivationService skips any
        // issue whose payload has no `fields` object — so omitting it stores raw rows that model
        // into nothing. `*all` because field mappings point at arbitrary custom field ids.
        StubEmptyPage();
        await Client().SearchIssuesPageAsync(Creds(), "project = \"ACME\"", null, CancellationToken.None);

        var fields = SentBody().GetProperty("fields");
        Assert.Equal(JsonValueKind.Array, fields.ValueKind);
        Assert.Equal(new[] { "*all" }, fields.EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task The_jql_is_sent_verbatim_and_paging_is_absent_on_the_first_page()
    {
        StubEmptyPage();
        await Client().SearchIssuesPageAsync(Creds(), "project = \"ACME\" AND status = Done", null, CancellationToken.None);

        var body = SentBody();
        Assert.Equal("project = \"ACME\" AND status = Done", body.GetProperty("jql").GetString());
        Assert.Equal(100, body.GetProperty("maxResults").GetInt32());
        // Null is omitted rather than sent as null — a literal null token is a type error here too.
        Assert.False(body.TryGetProperty("nextPageToken", out _));
    }

    [Fact]
    public async Task A_continuation_sends_the_page_token()
    {
        StubEmptyPage();
        await Client().SearchIssuesPageAsync(Creds(), "project = \"ACME\"", "tok-abc", CancellationToken.None);

        Assert.Equal("tok-abc", SentBody().GetProperty("nextPageToken").GetString());
    }
}
