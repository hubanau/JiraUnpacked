namespace JiraUnpacked.Infrastructure.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Emit HSTS in production. Default on; set false for deployments that terminate TLS at a
    /// reverse proxy and forward plain HTTP (the app then never sees HTTPS).
    /// </summary>
    public bool HstsEnabled { get; set; } = true;

    /// <summary>
    /// Honour X-Forwarded-For / X-Forwarded-Proto. Default OFF, deliberately.
    ///
    /// Without it, an app behind a TLS-terminating reverse proxy sees the PROXY's address on
    /// every request, so the per-IP auth rate limiter degrades into one shared bucket for all
    /// users — brute-force protection stops being per-attacker and legitimate users lock each
    /// other out. UseHttpsRedirection also sees a plain-HTTP request and redirects forever.
    ///
    /// But turning it on when the app IS directly reachable is worse: any client can then send
    /// its own X-Forwarded-For and choose which rate-limit bucket to spend. So this must be a
    /// deployment decision, and the safe default is the one that cannot be abused from outside.
    /// Enable it only when every request necessarily arrives through a trusted proxy.
    /// </summary>
    public bool TrustProxyHeaders { get; set; }

    /// <summary>
    /// How many proxy hops to unwind from X-Forwarded-For. Only the rightmost
    /// <c>ForwardLimit</c> entries are trusted; anything a client prepended is discarded.
    /// Set this to the actual number of proxies in front of the app — a larger number lets a
    /// client's forged entry survive the unwind and become the "client" address.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;
}
