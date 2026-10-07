using System.Globalization;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// The columns an export can contain, by the SAME ids the SPA's column chooser, the filter
/// catalog and the sort catalog use. One vocabulary: the ids travel in the URL next to `sortBy`
/// and `filter`, and a second naming scheme would be a bug waiting to happen.
///
/// This mirrors ISSUE_COLUMNS in the SPA. The two must stay in step — a divergence shows up as a
/// column missing from the file rather than as a crash, so it is worth checking both when either
/// changes.
/// </summary>
public static class ExportColumns
{
    public sealed record Column(string Id, string Header, Func<IssueListItem, string?> Value);

    public static readonly IReadOnlyList<Column> All = new[]
    {
        new Column("jiraKey", "Key", i => i.JiraKey),
        new Column("summary", "Summary", i => i.Summary),
        new Column("status", "Status", i => i.Status),
        new Column("statusCategory", "Category", i => i.StatusCategory),
        new Column("issueType", "Type", i => i.IssueType),
        new Column("priority", "Priority", i => i.Priority),
        new Column("assignee", "Assignee", i => i.Assignee),
        new Column("sprint", "Sprint", i => i.Sprint),
        new Column("team", "Team", i => i.Team),
        new Column("storyPoints", "Points", i => i.StoryPoints?.ToString(CultureInfo.InvariantCulture)),
        new Column("parent", "Parent", i => i.ParentJiraIssueId),
        // Round-trippable rather than locale-formatted: a CSV is read by machines as often as by
        // people, and "01/02/2026" means two different days on two sides of an ocean.
        new Column("createdAt", "Created", i => i.JiraCreatedAt.ToString("O", CultureInfo.InvariantCulture)),
        new Column("updatedAt", "Updated", i => i.JiraUpdatedAt.ToString("O", CultureInfo.InvariantCulture)),
    };

    /// <summary>
    /// The requested columns in catalog order, or the default set when none were asked for.
    /// Unknown ids are ignored rather than refused: a stale link with a since-renamed column
    /// should still produce a file.
    /// </summary>
    public static IReadOnlyList<Column> Resolve(string? cols)
    {
        if (string.IsNullOrWhiteSpace(cols)) return All;
        var wanted = cols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chosen = All.Where(c => wanted.Contains(c.Id, StringComparer.Ordinal)).ToList();
        return chosen.Count == 0 ? All : chosen;
    }
}
