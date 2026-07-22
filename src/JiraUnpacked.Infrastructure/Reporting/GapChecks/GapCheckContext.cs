// src/JiraUnpacked.Infrastructure/Reporting/GapChecks/GapCheckContext.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Infrastructure.Reporting.GapChecks;

/// <summary>One project issue, already classified. Current status only — gap-checks never
/// read change history.</summary>
public sealed record GapCheckIssue(
    string JiraIssueId, string JiraKey, string? Summary,
    string? Status, StatusClass Class, string? IssueType, bool IsEpicType, string? ParentJiraIssueId);

/// <summary>
/// Everything the checks need, loaded once per request by GapCheckRunner. Children are grouped
/// in memory into a single lookup, so parent-to-children resolution costs one pass and never
/// issues a query per parent.
/// </summary>
public sealed class GapCheckContext
{
    public GapCheckContext(IReadOnlyList<GapCheckIssue> issues, bool hasEpicTypeConfigured)
    {
        Issues = issues;
        HasEpicTypeConfigured = hasEpicTypeConfigured;
        ChildrenByParent = issues
            .Where(i => i.ParentJiraIssueId is not null)
            .ToLookup(i => i.ParentJiraIssueId!, StringComparer.Ordinal);
    }

    public IReadOnlyList<GapCheckIssue> Issues { get; }
    public ILookup<string, GapCheckIssue> ChildrenByParent { get; }
    public bool HasEpicTypeConfigured { get; }
}

public sealed record GapCheckOutcome(GapCheckStatus Status, IReadOnlyList<GapCheckViolation> Violations);

/// <summary>A curated correctness check. Pure and synchronous: the runner has already loaded
/// everything, so a check performs no I/O and is unit-testable without a database.</summary>
public interface IGapCheck
{
    GapCheckInfo Info { get; }
    GapCheckOutcome Run(GapCheckContext context);
}

internal static class GapCheckViolations
{
    /// <summary>Builds a violation for an issue, carrying the children that justify the finding.</summary>
    internal static GapCheckViolation From(GapCheckIssue issue, IReadOnlyList<GapCheckIssue> children) =>
        new(issue.JiraIssueId, issue.JiraKey, issue.Summary, issue.Status, issue.Class.ToString(), issue.IssueType,
            children
                .Select(c => new GapCheckChild(c.JiraIssueId, c.JiraKey, c.Summary, c.Status, c.Class.ToString()))
                .ToList());

    internal static GapCheckOutcome NotConfigured() =>
        new(GapCheckStatus.NotConfigured, Array.Empty<GapCheckViolation>());
}
