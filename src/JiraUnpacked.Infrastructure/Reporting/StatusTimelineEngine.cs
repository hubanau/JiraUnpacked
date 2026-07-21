// src/JiraUnpacked.Infrastructure/Reporting/StatusTimelineEngine.cs
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>One status change: Jira's changelog carries display NAMES, not status ids.</summary>
public sealed record StatusTransition(DateTime ChangedAt, string? FromValue, string? ToValue);

/// <summary>The three derived facts every FR-E2 metric is computed from.</summary>
public sealed record IssueTimeline(
    DateTime? FirstActiveAt,
    DateTime? FirstDoneAt,
    string? CurrentStatus,
    StatusClass CurrentClass,
    DateTime CurrentEnteredAt);

/// <summary>
/// Pure fold from an issue's status history to <see cref="IssueTimeline"/>. No EF, no clock,
/// no I/O — so it is unit-testable without a database. Classification happens HERE, at query
/// time, against the caller's semantics map; that is what makes a StatusSemantics edit change
/// every metric on the next request with no Jira re-pull and no re-derive.
/// </summary>
public static class StatusTimelineEngine
{
    /// <param name="jiraCreatedAt">When the issue was created — the start of the first segment.</param>
    /// <param name="currentStatusName">Current status name (Issue.StatusId -> Status.Name); may be null.</param>
    /// <param name="transitions">Status transitions ordered by ChangedAt ascending.</param>
    /// <param name="semantics">Status name -> class. MUST use a case-insensitive comparer.</param>
    public static IssueTimeline Build(
        DateTime jiraCreatedAt,
        string? currentStatusName,
        IReadOnlyList<StatusTransition> transitions,
        IReadOnlyDictionary<string, StatusClass> semantics)
    {
        // Segment starts, chronological. A segment is (status, when it was entered).
        var starts = new List<(string? Status, DateTime Start)>();

        if (transitions.Count == 0)
        {
            // No status ever changed: one open segment holding the current status since creation.
            starts.Add((currentStatusName, jiraCreatedAt));
        }
        else
        {
            // The status before the first transition is that transition's FromValue, held since creation.
            // Defensive: a null FromValue means the prior status is unknown, so we skip that segment
            // rather than invent one.
            if (transitions[0].FromValue is not null)
                starts.Add((transitions[0].FromValue, jiraCreatedAt));

            foreach (var t in transitions)
                starts.Add((t.ToValue, t.ChangedAt));
        }

        DateTime? firstActive = null;
        DateTime? firstDone = null;
        foreach (var (status, start) in starts)
        {
            var cls = Classify(status, semantics);
            if (cls == StatusClass.Active && firstActive is null) firstActive = start;
            // FirstDoneAt is deliberately independent of FirstActiveAt: an issue that reached Done
            // without ever being Active must still count toward throughput.
            if (cls == StatusClass.Done && firstDone is null) firstDone = start;
        }

        var last = starts[^1];
        return new IssueTimeline(
            firstActive, firstDone, last.Status, Classify(last.Status, semantics), last.Start);
    }

    /// <summary>A name absent from the project's mapping classifies as Other.</summary>
    public static StatusClass Classify(string? status, IReadOnlyDictionary<string, StatusClass> semantics) =>
        status is not null && semantics.TryGetValue(status, out var cls) ? cls : StatusClass.Other;
}
