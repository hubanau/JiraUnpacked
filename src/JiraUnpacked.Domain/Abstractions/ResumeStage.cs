namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Where a run got to, at a finer grain than <see cref="SyncPhase"/>.
///
/// Plain strings for the same reason SyncPhase uses them: the value goes to a varchar, and a
/// stage written by a newer build must not break an older one reading the row.
/// </summary>
public static class ResumeStage
{
    public const string Fetch = "fetch";
    public const string Changelog = "changelog";
    public const string DeriveIssues = "derive:issues";
    public const string DeriveHistory = "derive:history";
    public const string DeriveHierarchy = "derive:hierarchy";

    /// <summary>Execution order. Resuming at a stage skips every stage before it.</summary>
    public static readonly string[] Order =
        { Fetch, Changelog, DeriveIssues, DeriveHistory, DeriveHierarchy };

    /// <summary>
    /// Position of a stage in <see cref="Order"/>. An unknown or null stage answers 0 — start
    /// from the beginning.
    ///
    /// That is the safe direction, and the choice is deliberate: redoing work costs time, whereas
    /// treating an unrecognised stage as "late" would skip work that was never done, and nothing
    /// downstream would ever notice the gap.
    /// </summary>
    public static int IndexOf(string? stage)
    {
        if (stage is null) return 0;
        var i = Array.IndexOf(Order, stage);
        return i < 0 ? 0 : i;
    }
}
