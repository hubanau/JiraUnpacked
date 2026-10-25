namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// What JQL needs that a filter does not carry: ids where the filter has display names, and the
/// project's custom fields. Loaded tenant-scoped by IssueJqlService; the translator only reads it,
/// so it is testable without a database. Names match exactly (ordinal), as the issue filter's do.
/// </summary>
/// <param name="AccountIds">Jira user display name → account ids (a name two accounts share maps to both).</param>
/// <param name="SprintIds">Sprint name → Jira sprint ids, this project's sprints only.</param>
/// <param name="TeamIds">Team name → Jira team ids, this project's teams only.</param>
/// <param name="TeamField">The Team mapping's Jira field id ("customfield_10001"), or null.</param>
/// <param name="StoryPointsField">The Story Points mapping's Jira field id, or null.</param>
public sealed record JqlLookup(
    IReadOnlyDictionary<string, IReadOnlyList<string>> AccountIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SprintIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TeamIds,
    string? TeamField,
    string? StoryPointsField)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> None =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public static JqlLookup Empty { get; } = new(None, None, None, null, null);
}

/// <summary>The JQL, and every way it differs from the filter it came from (each once, in the order they arose).</summary>
public sealed record JqlTranslation(string Jql, IReadOnlyList<string> Warnings);
