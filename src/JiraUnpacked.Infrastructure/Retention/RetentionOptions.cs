namespace JiraUnpacked.Infrastructure.Retention;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Age beyond which SyncRun rows are purged. Raw stores and modeled data are NEVER
    /// time-purged: raw snapshots and changelogs are the source for re-derivation.
    /// </summary>
    public int SyncRunDays { get; set; } = 90;
}
