namespace JiraUnpacked.Infrastructure.Jira;

/// <summary>Outcome of checking a Jira base URL. <see cref="NormalizedUrl"/> is set only when valid.</summary>
public sealed record JiraBaseUrlCheck(bool IsValid, string? NormalizedUrl, string? Error)
{
    public static JiraBaseUrlCheck Valid(string normalized) => new(true, normalized, null);
    public static JiraBaseUrlCheck Invalid(string error) => new(false, null, error);
}

/// <summary>
/// The one place that decides whether a Jira base URL is acceptable.
///
/// A connection's base URL is where the server sends the user's email and API token, and every
/// later sync follows it. Accepting any URL made the server an open request proxy: an http://
/// URL puts the token on the wire in clear text, and a URL naming an internal host
/// (169.254.169.254, localhost, a service name on the Docker network) lets a user point the
/// server at something only the server can reach. The product is Jira Cloud only (FR-C1), so the
/// rule is simple: https, no userinfo, and a host under one of the configured suffixes.
/// </summary>
public sealed class JiraBaseUrlValidator
{
    private readonly JiraOptions _options;

    public JiraBaseUrlValidator(JiraOptions options) => _options = options;

    public JiraBaseUrlCheck Check(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return JiraBaseUrlCheck.Invalid("A Jira site URL is required.");

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            return JiraBaseUrlCheck.Invalid("The Jira site URL is not a valid absolute URL.");

        var httpsOnly = !_options.AllowInsecureHttp;
        if (uri.Scheme != Uri.UriSchemeHttps && (httpsOnly || uri.Scheme != Uri.UriSchemeHttp))
            return JiraBaseUrlCheck.Invalid(httpsOnly
                ? "The Jira site URL must use https."
                : "The Jira site URL must use https or http.");

        // https://user:pass@host is a credential in the URL, and "trusted.example@evil" is the
        // classic way to make a URL read as one host while naming another.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return JiraBaseUrlCheck.Invalid("The Jira site URL must not contain a user name or password.");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return JiraBaseUrlCheck.Invalid("The Jira site URL must not contain a query string or fragment.");

        var host = uri.IdnHost.ToLowerInvariant();
        if (!_options.EffectiveAllowedHostSuffixes.Any(suffix => HostMatches(host, suffix)))
            return JiraBaseUrlCheck.Invalid(
                $"The Jira site must be a Jira Cloud site: its host must end in {string.Join(" or ", _options.EffectiveAllowedHostSuffixes)}.");

        // Scheme and host come back lower-cased and a default port is dropped; the only other
        // normalisation is the trailing slash, so "https://acme.atlassian.net/" and
        // "https://acme.atlassian.net" are stored the same way.
        return JiraBaseUrlCheck.Valid(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }

    private static bool HostMatches(string host, string suffix)
    {
        var s = suffix.Trim().ToLowerInvariant();
        if (s.Length == 0) return false;
        if (s.StartsWith('.'))
            return host.Length > s.Length && host.EndsWith(s, StringComparison.Ordinal);
        return host == s || host.EndsWith("." + s, StringComparison.Ordinal);
    }
}
