using System.Net;
using System.Text;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

public sealed class JiraApiClientValidateTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private static IJiraApiClient Client() => new JiraApiClient(new HttpClient());

    public void Dispose() => _server.Stop();

    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "secret-token");

    [Fact]
    public async Task ValidateAsync_returns_Valid_and_sends_basic_auth_on_200()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new { accountId = "abc", displayName = "Ops" }));

        var result = await Client().ValidateAsync(Creds(), CancellationToken.None);

        Assert.Equal(JiraValidationResult.Valid, result);
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ops@acme.com:secret-token"));
        var log = Assert.Single(_server.LogEntries);
        Assert.Equal(expected, log.RequestMessage.Headers!["Authorization"][0]);
    }

    [Fact]
    public async Task ValidateAsync_returns_InvalidCredentials_on_401()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
               .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Unauthorized));
        Assert.Equal(JiraValidationResult.InvalidCredentials,
            await Client().ValidateAsync(Creds(), CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_returns_Unreachable_on_500()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(500));
        Assert.Equal(JiraValidationResult.Unreachable,
            await Client().ValidateAsync(Creds(), CancellationToken.None));
    }
}
