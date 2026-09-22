// tests/JiraUnpacked.Tests/Jira/JiraBaseUrlValidatorTests.cs
using JiraUnpacked.Infrastructure.Jira;
using Microsoft.Extensions.Configuration;

namespace JiraUnpacked.Tests.Jira;

/// <summary>
/// Pins the DEFAULT base-URL policy — the one production runs with. The integration factory
/// relaxes it to reach WireMock on http://localhost, so these are the tests that prove the
/// relaxation is not the product's behaviour.
/// </summary>
public sealed class JiraBaseUrlValidatorTests
{
    private static readonly JiraBaseUrlValidator Default = new(new JiraOptions());

    [Theory]
    [InlineData("http://acme.atlassian.net")]              // plain http
    [InlineData("https://169.254.169.254")]                // cloud metadata endpoint
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1:8080")]
    [InlineData("https://intranet.example.com")]           // non-Atlassian host
    [InlineData("https://acme.atlassian.net.evil.com")]    // suffix must be the END of the host
    [InlineData("https://evilatlassian.net")]              // ...on a label boundary
    [InlineData("https://atlassian.net")]                  // a site is a subdomain
    [InlineData("https://user:pw@acme.atlassian.net")]     // userinfo
    [InlineData("https://acme.atlassian.net@evil.com")]    // userinfo disguising the real host
    [InlineData("https://acme.atlassian.net/?x=1")]        // query
    [InlineData("https://acme.atlassian.net/#frag")]       // fragment
    [InlineData("ftp://acme.atlassian.net")]
    [InlineData("file:///etc/passwd")]
    [InlineData("acme.atlassian.net")]                     // not absolute
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Default_policy_rejects(string? baseUrl)
    {
        var result = Default.Check(baseUrl);

        Assert.False(result.IsValid);
        Assert.Null(result.NormalizedUrl);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData("https://acme.atlassian.net", "https://acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net/", "https://acme.atlassian.net")]
    [InlineData("  https://ACME.Atlassian.net/  ", "https://acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net:8443", "https://acme.atlassian.net:8443")] // non-default port is fine
    [InlineData("https://acme.atlassian.net:443/", "https://acme.atlassian.net")]
    [InlineData("https://eu.acme.atlassian.net", "https://eu.acme.atlassian.net")]
    public void Default_policy_accepts_jira_cloud_and_normalises(string baseUrl, string expected)
    {
        var result = Default.Check(baseUrl);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(expected, result.NormalizedUrl);
    }

    [Fact]
    public void Http_is_refused_by_default_and_allowed_only_when_configured()
    {
        Assert.Contains("https", Default.Check("http://acme.atlassian.net").Error);

        var insecure = new JiraBaseUrlValidator(new JiraOptions { AllowInsecureHttp = true });
        Assert.True(insecure.Check("http://acme.atlassian.net").IsValid);
        // Allowing http widens the scheme, never the hosts.
        Assert.False(insecure.Check("http://169.254.169.254").IsValid);
    }

    [Fact]
    public void Configured_suffixes_replace_the_default()
    {
        var custom = new JiraBaseUrlValidator(new JiraOptions { AllowedHostSuffixes = [".jira.example.com", "localhost"] });

        Assert.True(custom.Check("https://team.jira.example.com").IsValid);
        Assert.True(custom.Check("https://localhost:9000").IsValid);   // no leading dot: exact host too
        Assert.False(custom.Check("https://jira.example.com").IsValid); // leading dot: subdomain only
        Assert.False(custom.Check("https://acme.atlassian.net").IsValid);
    }

    [Fact]
    public void Binding_from_configuration_replaces_rather_than_extends_the_default()
    {
        // The reason AllowedHostSuffixes defaults to null: binding appends to a pre-filled list.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jira:AllowedHostSuffixes:0"] = ".jira.example.com",
                ["Jira:AllowInsecureHttp"] = "false",
            })
            .Build();
        var options = config.GetSection(JiraOptions.SectionName).Get<JiraOptions>()!;

        Assert.Equal([".jira.example.com"], options.EffectiveAllowedHostSuffixes);
        Assert.False(new JiraBaseUrlValidator(options).Check("https://acme.atlassian.net").IsValid);
    }

    [Fact]
    public void Unconfigured_options_fall_back_to_atlassian_net()
    {
        var options = new ConfigurationBuilder().Build().GetSection(JiraOptions.SectionName).Get<JiraOptions>()
                      ?? new JiraOptions();

        Assert.Equal([".atlassian.net"], options.EffectiveAllowedHostSuffixes);
        Assert.False(options.AllowInsecureHttp);
    }
}
