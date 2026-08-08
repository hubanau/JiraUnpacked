using JiraUnpacked.Api.Security;

namespace JiraUnpacked.Tests.Security;

public sealed class HstsGateTests
{
    [Theory]
    [InlineData(false, true, true)]   // production + enabled  -> HSTS on
    [InlineData(false, false, false)] // production + disabled -> off (reverse-proxy escape hatch)
    [InlineData(true, true, false)]   // development           -> never
    [InlineData(true, false, false)]  // development           -> never
    public void ShouldEnableHsts_covers_all_four_cases(bool isDevelopment, bool hstsEnabled, bool expected)
        => Assert.Equal(expected, HstsGate.ShouldEnableHsts(isDevelopment, hstsEnabled));
}
