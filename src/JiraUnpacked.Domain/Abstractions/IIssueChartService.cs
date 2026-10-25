namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// The list's filter (the same shapes the query takes) and the field to group its issues by. No
/// measure: every group carries both, so switching between them is the client's business.
/// </summary>
public sealed record IssueChartRequest(List<FilterCondition>? Conditions, FilterGroup? Filter, string? GroupBy);

/// <summary>
/// One value of the grouped field and the filtered issues carrying it. Key is the value's name
/// (a dictionary name, a number for Points, "yyyy-MM" for a month); null is the (Empty) group.
/// StoryPoints counts an issue without points as 0.
/// </summary>
public sealed record ChartGroup(string? Key, int Issues, decimal StoryPoints);

/// <summary>The groups left out of a truncated chart, summed so the totals still add up.</summary>
public sealed record ChartOther(int Groups, int Issues, decimal StoryPoints);

/// <summary>
/// How the filtered issues spread over one field. Totals are over every matched issue, kept
/// groups and Other alike; IssuesWithoutPoints is how many of them a points chart counts as 0.
/// </summary>
public sealed record IssueChart(
    string GroupBy, int TotalIssues, decimal TotalStoryPoints, int IssuesWithoutPoints,
    IReadOnlyList<ChartGroup> Groups, bool Truncated, ChartOther? Other);

/// <summary>Chart + null Error => 200. Error => 400. Both null => unknown or another tenant's project => 404.</summary>
public sealed record IssueChartResult(IssueChart? Chart, string? Error);

/// <summary>The issue list's chart: the filtered issues grouped by one field, with counts and story points.</summary>
public interface IIssueChartService
{
    Task<IssueChartResult> BuildAsync(Guid projectId, IssueChartRequest request, CancellationToken ct = default);
}
