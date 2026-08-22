// tests/JiraUnpacked.Tests/Integration/HealthEndpointTests.cs
using System.Net;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Liveness and readiness. An orchestrator needs these anonymous, and an unauthenticated caller
/// must learn nothing about the deployment from them.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public HealthEndpointTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_answer_without_a_token(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_reports_the_database_is_reachable()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("ready", body);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_are_not_swallowed_by_the_spa_fallback(string path)
    {
        // MapFallbackToFile answers any unmatched path with index.html and a 200, so a route
        // registered after it would look healthy while actually serving the SPA shell. The
        // content type is what separates "the endpoint ran" from "the fallback ran".
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_reveal_nothing_about_the_deployment(string path)
    {
        var body = await (await _factory.CreateClient().GetAsync(path)).Content.ReadAsStringAsync();

        // Anonymous and unauthenticated: no host, no connection string, no version, no stack.
        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localhost", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(body.Length < 64, $"health body should stay terse, got: {body}");
    }
}
