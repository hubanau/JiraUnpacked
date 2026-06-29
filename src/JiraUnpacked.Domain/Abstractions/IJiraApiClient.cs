using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Domain.Abstractions;

public interface IJiraApiClient
{
    Task<JiraValidationResult> ValidateAsync(JiraCredentials creds, CancellationToken ct = default);
    Task<IReadOnlyList<JiraField>> GetFieldsAsync(JiraCredentials creds, CancellationToken ct = default);
    Task<IReadOnlyList<JiraProjectSummary>> GetProjectsAsync(JiraCredentials creds, CancellationToken ct = default);
    Task<JiraProjectSummary?> GetProjectAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default);
    Task<IReadOnlyList<JiraStatus>> GetStatusesAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default);
    Task<JqlValidationResult> ParseJqlAsync(JiraCredentials creds, string jql, CancellationToken ct = default);
}
