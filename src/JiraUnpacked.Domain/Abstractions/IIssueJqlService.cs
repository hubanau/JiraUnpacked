namespace JiraUnpacked.Domain.Abstractions;

/// <summary>The list's filter (the same tree the query takes) and its sort, to be written as JQL.</summary>
public sealed record IssueJqlRequest(FilterGroup? Filter, string? SortBy = null, bool SortDescending = true);

/// <summary>One way the JQL differs from the filter: a part left out, an approximation, a sort JQL cannot do.</summary>
public sealed record JqlWarning(string Message);

/// <summary>The JQL, a link that opens it in the project's Jira, and every difference from the filter.</summary>
public sealed record IssueJqlView(string Jql, string Url, IReadOnlyList<JqlWarning> Warnings);

/// <summary>View + null Error => 200. Error => 400 (the query's own message). Both null => unknown or another tenant's project => 404.</summary>
public sealed record IssueJqlResult(IssueJqlView? View, string? Error);

public interface IIssueJqlService
{
    Task<IssueJqlResult> BuildAsync(Guid projectId, IssueJqlRequest request, CancellationToken ct = default);
}
