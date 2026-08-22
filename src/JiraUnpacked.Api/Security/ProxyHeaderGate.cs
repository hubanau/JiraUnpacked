using Microsoft.AspNetCore.HttpOverrides;

namespace JiraUnpacked.Api.Security;

/// <summary>
/// Builds the forwarded-headers configuration, kept as a pure function so the security-relevant
/// decisions are testable without a host.
/// </summary>
public static class ProxyHeaderGate
{
    /// <summary>
    /// Sibling of <see cref="HstsGate"/>: proxy headers are honoured only when the deployment
    /// says every request arrives through a trusted proxy. See SecurityOptions.TrustProxyHeaders
    /// for why the default is off.
    /// </summary>
    public static bool ShouldTrustProxyHeaders(bool trustProxyHeaders) => trustProxyHeaders;

    public static ForwardedHeadersOptions Build(int forwardLimit)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // Only unwind as many hops as the deployment actually has. Left higher, a client's
            // own forged X-Forwarded-For entry survives the unwind and becomes the address the
            // rate limiter partitions on.
            ForwardLimit = forwardLimit < 1 ? 1 : forwardLimit,
        };

        // ASP.NET ignores forwarded headers from an unrecognised peer unless the allow-lists are
        // cleared. In a container the proxy's address is not knowable ahead of time, so clearing
        // is the only workable choice — which is exactly why this whole path is opt-in and
        // documented as "only when the app is not directly reachable".
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        return options;
    }
}
