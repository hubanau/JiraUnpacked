namespace JiraUnpacked.Api.Security;

public static class HstsGate
{
    /// <summary>HSTS is never sent in Development; in other environments it follows the config flag.</summary>
    public static bool ShouldEnableHsts(bool isDevelopment, bool hstsEnabled) => !isDevelopment && hstsEnabled;
}
