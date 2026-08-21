using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

// A rejected Jira credential must not end the caller's Jira Unpacked
// session. JiraApiExceptionFilter used to map JiraErrorKind.Unauthorized to
// 401, which the SPA could not distinguish from its own JWT expiring — so a
// revoked *Jira* token bounced the user to /login. Every JiraApiException is
// now 502 (see JiraApiExceptionFilter.cs), with the title carrying which
// upstream failure occurred. These tests drive that through a real
// live-Jira read (GET /connections/{id}/fields) against a WireMock stand-in
// for Jira, the same style ConnectionDiscoveryTests uses, rather than
// stubbing IJiraApiClient directly through the factory: nothing in this
// codebase replaces IJiraApiClient via DI in tests, and WireMock gives a
// truer end-to-end proof that EnsureOk's status mapping and the filter
// agree with each other.
public sealed class JiraCredentialFailureTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public JiraCredentialFailureTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);

    private async Task<Guid> CreateConnectionAsync(HttpClient client)
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));

        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" }))
            .Content.ReadFromJsonAsync<ConnectionDto>())!;
        return conn.Id;
    }

    [Fact]
    public async Task Rejected_jira_credential_returns_502_not_401()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var connectionId = await CreateConnectionAsync(client);

        // The field listing itself is what now fails: Jira rejects the
        // stored token, which JiraApiClient.EnsureOk maps to
        // JiraErrorKind.Unauthorized.
        _jira.Given(Request.Create().WithPath("/rest/api/3/field").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(401).WithBodyAsJson(new { message = "unauthorized" }));

        var response = await client.GetAsync($"/api/connections/{connectionId}/fields");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("rejected the stored credentials", body);
        // Must not be readable as a session failure — 401 now means exactly
        // one thing (our own session), so a rejected Jira credential must
        // never carry that status.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unreachable_jira_returns_502_with_the_distinct_unreachable_title()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var connectionId = await CreateConnectionAsync(client);

        // Any non-auth, non-2xx upstream response falls into
        // JiraErrorKind.Upstream (EnsureOk's default arm) — a 500 here
        // stands in for "Jira is broken/unreachable" without having to stop
        // the WireMock server mid-test.
        _jira.Given(Request.Create().WithPath("/rest/api/3/field").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(500));

        var response = await client.GetAsync($"/api/connections/{connectionId}/fields");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Jira could not be reached", body);
        // The two JiraApiException kinds must stay distinguishable by title
        // even though they now share a status code.
        Assert.DoesNotContain("rejected the stored credentials", body);
    }
}
