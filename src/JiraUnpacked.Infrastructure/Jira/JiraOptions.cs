namespace JiraUnpacked.Infrastructure.Jira;

/// <summary>
/// Which Jira sites a connection may point at. The product is Jira Cloud only (FR-C1), and the
/// base URL is where the server sends the user's email and API token — so it is an outbound
/// request target chosen by a user, and must not be allowed to reach internal hosts.
/// </summary>
public sealed class JiraOptions
{
    public const string SectionName = "Jira";

    /// <summary>Used when <see cref="AllowedHostSuffixes"/> is not configured.</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedHostSuffixes = [".atlassian.net"];

    /// <summary>
    /// Host suffixes a base URL must end in. A suffix starting with a dot (".atlassian.net")
    /// requires a subdomain; one without ("localhost") also matches that exact host.
    ///
    /// Null rather than pre-populated on purpose: configuration binding APPENDS to a collection it
    /// finds already filled, so a default list here could never be replaced, only extended.
    /// </summary>
    public string[]? AllowedHostSuffixes { get; set; }

    /// <summary>Permit http:// base URLs. Off by default — the credentials travel in the request.</summary>
    public bool AllowInsecureHttp { get; set; }

    public IReadOnlyList<string> EffectiveAllowedHostSuffixes =>
        AllowedHostSuffixes is { Length: > 0 } configured
            ? configured.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
            : DefaultAllowedHostSuffixes;
}
