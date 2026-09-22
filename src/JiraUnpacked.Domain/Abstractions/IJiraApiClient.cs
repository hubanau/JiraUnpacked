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
    Task<SearchPage> SearchIssuesPageAsync(JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default);

    /// <summary>
    /// Roughly how many issues a JQL matches, for progress reporting only.
    /// <c>/search/jql</c> is token-paginated and reports no total, so this is the only way to
    /// know the size of a sync before doing it.
    ///
    /// Returns null when Jira will not say. Null and 0 are deliberately distinct: 0 means "no
    /// issues match", null means "unknown", and a progress bar must not render the second as
    /// the first. Never throws — a failed count degrades the bar, it does not fail the sync.
    /// </summary>
    Task<int?> ApproximateCountAsync(JiraCredentials creds, string jql, CancellationToken ct = default);
    Task<ChangelogPage> GetIssueChangelogPageAsync(JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct = default);

    /// <summary>
    /// The IANA time zone of the account the credentials belong to, or null when Jira will not
    /// say. JQL date literals are read in THAT zone, not UTC, so an incremental clause written in
    /// UTC is hours late for any account west of Greenwich. Never throws except on cancellation.
    /// </summary>
    Task<string?> GetUserTimeZoneAsync(JiraCredentials creds, CancellationToken ct = default);

    /// <summary>
    /// One page of the ids a JQL matches — no fields, so pages are large and cheap. Used to find
    /// stored issues that have left the scope (deleted or moved in Jira).
    /// </summary>
    Task<IssueIdPage> SearchIssueIdsPageAsync(JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default);
}
