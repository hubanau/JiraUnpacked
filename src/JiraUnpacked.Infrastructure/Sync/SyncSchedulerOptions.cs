using System.ComponentModel.DataAnnotations;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed class SyncSchedulerOptions
{
    public const string SectionName = "Sync";
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>A Running run older than this is a crashed/stuck sync — the reaper marks it Failed,
    /// unblocking the project (the partial unique index permits one Running run per project).</summary>
    public int StuckRunTimeoutMinutes { get; set; } = 120;

    /// <summary>
    /// Items per checkpoint for the fetch and derivation stages. After each batch the run commits
    /// its data and its position together, so an interruption costs at most one batch rather than
    /// the whole run.
    ///
    /// Changelog ingest keeps its own smaller batch of 250 — see ChangelogIngestService. Every
    /// item there costs a Jira round trip, so 250 is already seconds of work, and checkpointing at
    /// the commit point it already had loses less on interruption rather than more.
    /// </summary>
    [Range(1, 1_000_000)]
    public int CheckpointBatchSize { get; set; } = 10_000;
}
