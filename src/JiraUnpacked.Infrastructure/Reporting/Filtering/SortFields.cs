namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// The columns a caller may sort by.
///
/// A closed list for the same reason FilterFields is one: the name arrives from a URL, and a name
/// that reached SQL as a string would be an injection point. Nothing is ever built from caller
/// input — the name only ever selects a pre-written ordering (see IssueQueryService.ApplySort).
///
/// The names are the SAME ones FilterFields uses, so one vocabulary covers filtering, sorting and
/// the column chooser. All three appear together in the URL, and two names for one field would be
/// a bug waiting to happen.
///
/// `parent` is deliberately absent: it holds an opaque Jira id, so ordering by it orders by a
/// string nobody reads.
/// </summary>
internal static class SortFields
{
    internal const string Default = "updatedAt";

    internal static readonly IReadOnlyList<string> Names = new[]
    {
        "jiraKey", "summary", "status", "issueType", "priority", "assignee",
        "sprint", "team", "storyPoints", "createdAt", "updatedAt",
    };

    internal static bool IsKnown(string? name) => Names.Contains(name ?? Default);

    internal static string Describe(string? name) =>
        $"'{name}' is not a sortable column. Sortable: {string.Join(", ", Names)}.";
}
