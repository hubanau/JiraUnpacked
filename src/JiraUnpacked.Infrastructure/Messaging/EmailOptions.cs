namespace JiraUnpacked.Infrastructure.Messaging;

/// <summary>
/// SMTP delivery. When <see cref="Host"/> is empty the app falls back to the development
/// sender that only logs — which <see cref="JiraUnpacked.Api.Security.ProductionConfigGate"/>
/// refuses to start with outside Development.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;

    /// <summary>STARTTLS. Leave on unless the host genuinely does not support it.</summary>
    public bool UseStartTls { get; set; } = true;

    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Jira Unpacked";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
