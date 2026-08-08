namespace JiraUnpacked.Infrastructure.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Emit HSTS in production. Default on; set false for deployments that terminate TLS at a
    /// reverse proxy and forward plain HTTP (the app then never sees HTTPS).
    /// </summary>
    public bool HstsEnabled { get; set; } = true;
}
