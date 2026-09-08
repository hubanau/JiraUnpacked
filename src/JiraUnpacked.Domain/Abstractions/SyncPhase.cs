namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// What a Running sync is doing, for the progress display. Plain strings rather than an enum:
/// the value is written to a varchar and read by the UI as a label, and a run stored by an older
/// build simply has null here — a phase nobody recognises must not break deserialisation of a
/// run row.
/// </summary>
public static class SyncPhase
{
    public const string Counting = "Counting";
    public const string Fetching = "Fetching";
    public const string Changelog = "Changelog";
    public const string Deriving = "Deriving";
}
