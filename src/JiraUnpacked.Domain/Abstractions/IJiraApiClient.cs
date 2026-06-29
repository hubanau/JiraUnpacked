using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Domain.Abstractions;

public interface IJiraApiClient
{
    Task<JiraValidationResult> ValidateAsync(JiraCredentials creds, CancellationToken ct = default);
    // discovery methods added in CSL3
}
