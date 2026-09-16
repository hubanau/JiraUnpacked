using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;
public sealed class SyncRun : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public SyncRunStatus Status { get; set; }
    public SyncRunKind Kind { get; set; }
    public int IssuesProcessed { get; set; }

    /// <summary>
    /// How many issues the run expects to fetch, from Jira's approximate count. Null means
    /// unknown — the count call failed, or this is a Rederive, which fetches nothing. Null and 0
    /// are different answers and a progress bar must not conflate them.
    /// </summary>
    public int? TotalToSync { get; set; }

    /// <summary>
    /// What the run is doing right now, for the progress display. Null once the run is over.
    /// Present because extract is only the first of three phases: without it a bar reaches 100%
    /// and then appears to hang through changelog ingest and derivation.
    /// </summary>
    public string? Phase { get; set; }

    /// <summary>
    /// How far through the CURRENT phase the run is, and out of how much. Null when the phase
    /// cannot say — counting, deriving, or a run that is over.
    ///
    /// Deliberately separate from IssuesProcessed/TotalToSync, which mean one thing and keep
    /// meaning it: how many issues this run fetched from Jira. Reusing those for the progress bar
    /// would leave a finished run reporting whatever the last phase happened to be counting.
    /// </summary>
    public int? PhaseProcessed { get; set; }

    public int? PhaseTotal { get; set; }

    /// <summary>
    /// When this run last showed a sign of life — set every time it publishes progress or enters
    /// a phase. Null on runs that predate the column.
    ///
    /// This is what separates a STUCK run from a slow one. The reaper used to compare StartedAt
    /// against a fixed timeout and so killed a perfectly healthy 92,673-issue sync at the
    /// two-hour mark, three hours into work it would have finished. A run that is still reporting
    /// is alive whatever its age; a run whose process died stops reporting and is reaped.
    /// </summary>
    public DateTime? LastProgressAt { get; set; }
    public string? Error { get; set; }
    public DateTime? NextRunAt { get; set; }

    /// <summary>
    /// Which SCOPE this run extracted — a hash of the JQL it actually ran (or of the
    /// whole-project default when the project has no query of its own).
    ///
    /// It exists so an incremental watermark cannot be inherited across a change of scope. A run
    /// only tells you "everything in THIS scope is current as of then"; after the queries change,
    /// the new scope has never been fetched, and reusing the old timestamp asks Jira only for what
    /// changed recently — silently skipping every older issue the new query covers.
    ///
    /// Null on runs made before this column existed, which is deliberate: those include the
    /// silent no-ops that fetched nothing at all, and none of them may seed a watermark.
    /// </summary>
    public string? ScopeFingerprint { get; set; }
}
