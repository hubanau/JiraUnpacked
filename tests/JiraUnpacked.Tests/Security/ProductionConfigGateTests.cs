using JiraUnpacked.Api.Security;

namespace JiraUnpacked.Tests.Security;

/// <summary>
/// The startup gate. Every rule it enforces guards a SILENT failure — a dev signing key still
/// mints valid tokens, a dev encryption key still decrypts, the log-only email sender still
/// reports success — so nothing would look broken until it mattered.
/// </summary>
public sealed class ProductionConfigGateTests
{
    private const string SafeKey = "a-real-private-signing-key-at-least-32-chars";
    private const string SafeProtectionKey = "sJ8tS0Q0m4a6r5b1z2x3c4v5b6n7m8a9s0d1f2g3h4=";

    [Fact]
    public void Development_is_exempt_from_every_rule()
    {
        // Local work uses exactly these values on purpose; the gate must not block it.
        var problems = ProductionConfigGate.Validate(
            isDevelopment: true,
            ProductionConfigGate.DevSigningKey,
            ProductionConfigGate.DevTokenProtectionKey,
            emailConfigured: false);

        Assert.Empty(problems);
    }

    [Fact]
    public void A_fully_configured_deployment_passes()
    {
        var problems = ProductionConfigGate.Validate(
            isDevelopment: false, SafeKey, SafeProtectionKey, emailConfigured: true);

        Assert.Empty(problems);
    }

    [Fact]
    public void The_committed_dev_signing_key_is_refused_outside_development()
    {
        var problems = ProductionConfigGate.Validate(
            isDevelopment: false, ProductionConfigGate.DevSigningKey, SafeProtectionKey, true);

        var problem = Assert.Single(problems);
        Assert.Contains("Jwt:SigningKey", problem);
    }

    [Fact]
    public void The_committed_dev_token_protection_key_is_refused_outside_development()
    {
        // This one decrypts every stored Jira API token, and it is published in this repo.
        var problems = ProductionConfigGate.Validate(
            isDevelopment: false, SafeKey, ProductionConfigGate.DevTokenProtectionKey, true);

        var problem = Assert.Single(problems);
        Assert.Contains("TokenProtection:Key", problem);
    }

    [Fact]
    public void Unconfigured_email_is_refused_outside_development()
    {
        var problems = ProductionConfigGate.Validate(
            isDevelopment: false, SafeKey, SafeProtectionKey, emailConfigured: false);

        var problem = Assert.Single(problems);
        Assert.Contains("Email:Host", problem);
    }

    [Fact]
    public void Every_problem_is_reported_at_once_not_one_restart_at_a_time()
    {
        var problems = ProductionConfigGate.Validate(
            isDevelopment: false,
            ProductionConfigGate.DevSigningKey,
            ProductionConfigGate.DevTokenProtectionKey,
            emailConfigured: false);

        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void ThrowIfInvalid_names_every_problem_in_the_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ProductionConfigGate.ThrowIfInvalid(
            isDevelopment: false,
            ProductionConfigGate.DevSigningKey,
            ProductionConfigGate.DevTokenProtectionKey,
            emailConfigured: false));

        Assert.Contains("Jwt:SigningKey", ex.Message);
        Assert.Contains("TokenProtection:Key", ex.Message);
        Assert.Contains("Email:Host", ex.Message);
    }

    [Fact]
    public void ThrowIfInvalid_is_silent_when_the_configuration_is_acceptable()
    {
        ProductionConfigGate.ThrowIfInvalid(false, SafeKey, SafeProtectionKey, true);
    }
}
