using JiraUnpacked.Api.Security;
using Microsoft.AspNetCore.HttpOverrides;

namespace JiraUnpacked.Tests.Security;

/// <summary>
/// Forwarded-header handling is a two-sided risk, so both sides are pinned here.
///
/// OFF behind a proxy: every request carries the proxy's address, the per-IP auth rate limiter
/// becomes one bucket shared by all users, and UseHttpsRedirection sees plain HTTP and
/// redirects forever.
///
/// ON when the app is directly reachable: any client can send its own X-Forwarded-For and pick
/// which rate-limit bucket to spend.
/// </summary>
public sealed class ProxyHeaderGateTests
{
    [Fact]
    public void Proxy_headers_are_not_trusted_by_default()
    {
        // The safe default is the one that cannot be abused from outside: an app that is
        // directly reachable must ignore a client's forwarded headers.
        Assert.False(ProxyHeaderGate.ShouldTrustProxyHeaders(trustProxyHeaders: false));
        Assert.True(ProxyHeaderGate.ShouldTrustProxyHeaders(trustProxyHeaders: true));
    }

    [Fact]
    public void Both_the_caller_address_and_the_scheme_are_forwarded()
    {
        var options = ProxyHeaderGate.Build(forwardLimit: 1);

        // XForwardedFor fixes the rate limiter's partition key; XForwardedProto fixes the
        // redirect loop. Either one alone leaves a real deployment broken.
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void Forward_limit_is_honoured_and_never_drops_below_one(int configured, int expected)
    {
        // The limit is what stops a client's own prepended entry from surviving the unwind and
        // becoming the "client" address. Zero or negative would mean unlimited unwinding, so it
        // is clamped rather than passed through.
        Assert.Equal(expected, ProxyHeaderGate.Build(configured).ForwardLimit);
    }

    [Fact]
    public void The_peer_allow_lists_are_cleared_so_a_containerised_proxy_is_accepted()
    {
        var options = ProxyHeaderGate.Build(forwardLimit: 1);

        // ASP.NET drops forwarded headers from an unrecognised peer, and a proxy's address is
        // not knowable ahead of time in a container. Clearing is what makes the feature work at
        // all — and precisely why the whole path is opt-in.
        Assert.Empty(options.KnownNetworks);
        Assert.Empty(options.KnownProxies);
    }
}
