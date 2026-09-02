namespace JiraUnpacked.Infrastructure.Services;

/// <summary>
/// How the application refers to itself in things it sends out — currently the password-reset
/// link. Configuration rather than the request's Host header, on purpose: see
/// PasswordResetService.BuildLink for the host-header injection this avoids.
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// Public origin, e.g. https://jira-unpacked.example.com. Defaults to the address the
    /// local Docker stack serves, so a clone works without configuration.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";
}
