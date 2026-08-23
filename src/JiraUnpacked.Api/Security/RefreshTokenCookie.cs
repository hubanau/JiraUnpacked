namespace JiraUnpacked.Api.Security;

/// <summary>
/// The refresh token lives in an httpOnly cookie and nowhere else — not in a response body,
/// not in a request body, not in localStorage. Script therefore cannot read it, which is the
/// entire point: an XSS can act as the user while the page is open, but it cannot exfiltrate
/// a credential to use later, elsewhere, after the tab is closed.
/// </summary>
public static class RefreshTokenCookie
{
    public const string Name = "ju_rt";

    /// <summary>
    /// Scoped to the only two endpoints that consume it, so the token is never attached to
    /// any other request and cannot surface in a proxy trace or an access log for them.
    /// </summary>
    public const string Path = "/api/auth";

    /// <summary>
    /// The header a cross-site request cannot supply: a form post cannot set headers at all,
    /// and a cross-site fetch that sets one triggers a CORS preflight, which fails because the
    /// app publishes no CORS policy. Second layer behind SameSite=Strict, which is the primary
    /// CSRF control.
    /// </summary>
    public const string RequiredHeader = "X-Requested-With";

    public const string RequiredHeaderValue = "XMLHttpRequest";

    /// <summary>
    /// Secure is required everywhere except Development, and except where a deployment
    /// explicitly opts out. Gated like HSTS (<see cref="HstsGate"/>): local development and the
    /// integration suite both run over plain HTTP, and a hard-coded Secure would silently stop
    /// the cookie being sent there — a failure that looks like broken auth, not a setting.
    ///
    /// <paramref name="secureCookiesEnabled"/> exists for a local Docker stack served over
    /// http://localhost. Browsers disagree about Secure cookies there, so leaving it on makes
    /// sign-in work in Chrome and fail in Safari. Over a real network it must stay on.
    /// </summary>
    public static bool ShouldRequireSecure(bool isDevelopment, bool secureCookiesEnabled = true) =>
        !isDevelopment && secureCookiesEnabled;

    public static CookieOptions Options(
        bool isDevelopment, DateTimeOffset expiresAt, bool secureCookiesEnabled = true) => new()
    {
        HttpOnly = true,
        Secure = ShouldRequireSecure(isDevelopment, secureCookiesEnabled),
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expiresAt,
    };

    /// <summary>
    /// Deleting must repeat every attribute the cookie was set with: a browser matches a
    /// deletion by name AND path, so an expiry written at the default path leaves the real
    /// cookie in place and the user silently still holds a valid token.
    /// </summary>
    public static CookieOptions DeleteOptions(
        bool isDevelopment, bool secureCookiesEnabled = true) => new()
    {
        HttpOnly = true,
        Secure = ShouldRequireSecure(isDevelopment, secureCookiesEnabled),
        SameSite = SameSiteMode.Strict,
        Path = Path,
    };
}
