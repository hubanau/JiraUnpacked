// tests/JiraUnpacked.Tests/Jira/JiraApiErrorDetailTests.cs
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

/// <summary>
/// What a failed Jira call is allowed to say. The client used to discard the response body, so
/// a sync failure reached the user as "Jira returned 400." — true, and useless: a rejected JQL
/// and a malformed request body are indistinguishable without Jira's own explanation, and both
/// arrive as 400. These messages are persisted to SyncRun.Error and read by a tenant, so what
/// they may and may not contain is worth pinning down.
/// </summary>
public sealed class JiraApiErrorDetailTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private JiraApiClient Client() => new(new HttpClient());
    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "secret-token");
    public void Dispose() => _server.Stop();

    private const string StatusesPath = "/rest/api/3/project/ACME/statuses";

    private void Respond(int status, object body) =>
        _server.Given(Request.Create().WithPath(StatusesPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBodyAsJson(body));

    [Fact]
    public async Task A_400_carries_Jiras_own_errorMessages()
    {
        Respond(400, new { errorMessages = new[] { "Can not deserialize value of type java.lang.String" }, errors = new { } });

        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None));

        Assert.Equal(JiraErrorKind.Upstream, ex.Kind);
        Assert.Contains("400", ex.Message);
        Assert.Contains("Can not deserialize value of type java.lang.String", ex.Message);
    }

    [Fact]
    public async Task Field_level_errors_are_named_with_their_field()
    {
        // Jira reports request-body problems either way round; a message that dropped the field
        // name would leave the reader guessing which part of the request was wrong.
        Respond(400, new { errorMessages = new string[0], errors = new { expand = "Expected a string" } });

        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None));

        Assert.Contains("expand: Expected a string", ex.Message);
    }

    [Fact]
    public async Task A_non_JSON_body_is_still_reported()
    {
        // A proxy or gateway in front of Jira answers in HTML. Dropping it would put us back at
        // "Jira returned 502." — the exact hole this closes.
        _server.Given(Request.Create().WithPath(StatusesPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(502).WithBody("<html>Bad gateway from proxy</html>"));

        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None));

        Assert.Contains("Bad gateway from proxy", ex.Message);
    }

    [Fact]
    public async Task A_credential_failure_stays_terse()
    {
        // Deliberate exception to the rule. Nothing Jira says about an auth failure helps the
        // reader, and echoing an upstream body on the credential path is how tokens and account
        // detail end up in a database column.
        Respond(401, new { errorMessages = new[] { "Client must be authenticated: secret-token" } });

        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None));

        Assert.Equal(JiraErrorKind.Unauthorized, ex.Kind);
        Assert.Equal("Jira rejected the credentials.", ex.Message);
        Assert.DoesNotContain("secret-token", ex.Message);
    }

    [Fact]
    public async Task A_long_body_is_capped()
    {
        // The message lands in a database column and on a screen; an unbounded upstream body
        // should not decide how big either gets.
        Respond(400, new { errorMessages = new[] { new string('x', 5000) } });

        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None));

        Assert.True(ex.Message.Length < 500, $"message was {ex.Message.Length} chars");
        Assert.EndsWith("…", ex.Message);
    }
}
