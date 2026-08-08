using System.Net;
using Xunit;

namespace JiraUnpacked.Tests.Integration;

public sealed class SpaHostingTests : IClassFixture<JiraUnpackedApiFactory>
{
    private readonly JiraUnpackedApiFactory _factory;

    public SpaHostingTests(JiraUnpackedApiFactory factory) => _factory = factory;

    // An unknown /api path must stay a JSON-shaped 404. If the SPA fallback
    // swallowed it, this would be 200 with an HTML body and every client would
    // try to parse HTML as JSON.
    [Fact]
    public async Task Unknown_api_route_returns_404_not_the_spa_shell()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/definitely-not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!doctype html", body, StringComparison.OrdinalIgnoreCase);
    }

    // A real endpoint must still win over the catch-all. Unauthenticated =>
    // 401, which proves routing reached the controller rather than the fallback.
    [Fact]
    public async Task Real_api_route_still_reaches_its_controller()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
