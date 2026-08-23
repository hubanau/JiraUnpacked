namespace JiraUnpacked.Api.Security;

/// <summary>
/// Startup gate: refuses to boot outside Development with configuration that is safe only for
/// local work. Pure functions so each rule is testable without a host.
///
/// Every rule here exists because the failure it prevents is SILENT. A dev signing key still
/// mints valid tokens; a dev encryption key still decrypts; the logging email sender still
/// returns success. Nothing would look broken until it mattered.
/// </summary>
public static class ProductionConfigGate
{
    /// <summary>
    /// These exact values are committed in appsettings.Development.json — they are published,
    /// so anyone with the repo holds them. Matching on the literal is the point: the failure
    /// being prevented is a copy-paste of the dev file into a real deployment.
    /// </summary>
    public const string DevSigningKey = "dev-only-signing-key-change-me-min-32-chars";

    public const string DevTokenProtectionKey = "JLj4/91ulqjJOo1bQNQnAOugepPpRQ1VNIH5hwt8LHI=";

    /// <summary>
    /// Returns every violation, so an operator fixes them in one pass rather than discovering
    /// them one restart at a time. Empty means the configuration is acceptable.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        bool isDevelopment, string? signingKey, string? tokenProtectionKey, bool emailConfigured,
        string? ownerConnectionString = null, string? readerConnectionString = null)
    {
        if (isDevelopment) return Array.Empty<string>();

        var problems = new List<string>();

        // Pointing the background readers at the owner would put DDL rights back in the running
        // container — the exact arrangement the reader role exists to end. Nothing else would
        // look wrong: the reads succeed either way.
        if (!string.IsNullOrWhiteSpace(ownerConnectionString)
            && string.Equals(ownerConnectionString, readerConnectionString, StringComparison.Ordinal))
        {
            problems.Add(
                "ConnectionStrings:Reader is the same as ConnectionStrings:Default. The "
                + "background readers would run as the owner, which can create tables and drop "
                + "RLS policies. Point Reader at the read-only jiraunpacked_reader role.");
        }

        if (string.Equals(signingKey, DevSigningKey, StringComparison.Ordinal))
        {
            problems.Add(
                "Jwt:SigningKey is the development key committed to this repository. Anyone with "
                + "the source can mint valid access tokens for any user. Set a private key.");
        }

        if (string.Equals(tokenProtectionKey, DevTokenProtectionKey, StringComparison.Ordinal))
        {
            problems.Add(
                "TokenProtection:Key is the development key committed to this repository. Anyone "
                + "with the source can decrypt every stored Jira API token. Set a private key.");
        }

        if (!emailConfigured)
        {
            problems.Add(
                "Email:Host and Email:FromAddress are not configured, so the development email "
                + "sender would be used. It never sends anything and writes verification codes "
                + "to the log, which lets anyone who can read logs verify any account.");
        }

        return problems;
    }

    /// <summary>Throws with every problem listed, rather than failing on the first one.</summary>
    public static void ThrowIfInvalid(
        bool isDevelopment, string? signingKey, string? tokenProtectionKey, bool emailConfigured,
        string? ownerConnectionString = null, string? readerConnectionString = null)
    {
        var problems = Validate(
            isDevelopment, signingKey, tokenProtectionKey, emailConfigured,
            ownerConnectionString, readerConnectionString);
        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            "Refusing to start with development-only configuration:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }
}
