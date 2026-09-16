namespace JiraUnpacked.Domain.Abstractions;

public enum SyncOutcome { Completed, AlreadyRunning, ProjectNotFound, Started }

public sealed record SyncRunView(
    Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt,
    int IssuesProcessed, string? Error, DateTime? NextRunAt, string Kind,
    int? TotalToSync = null, string? Phase = null,
    int? PhaseProcessed = null, int? PhaseTotal = null);

public sealed record SyncRunResult(SyncOutcome Outcome, SyncRunView? Run);

/// <summary>
/// A run happens in two halves so the slow one can be handed to a background worker.
///
/// START is cheap and synchronous: it validates the project, loses the race for the single
/// Running slot if there is one, and inserts the Running row. A caller can therefore answer
/// 404/409 immediately and hand back a real run id to poll — none of which is knowable if the
/// work is simply thrown onto a queue.
///
/// EXECUTE is the slow half: Jira paging, ingest and derivation, minutes on a large project.
/// It takes a run id rather than a project id because the row already exists, and it runs under
/// the process lifetime rather than a request's — a closed browser must not cancel a sync.
/// </summary>
public interface ISyncOrchestrator
{
    /// <summary>Runs one project's extract, both halves. The caller MUST have established the ambient tenant (project owner).</summary>
    Task<SyncRunResult> RunProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Re-derive modeled tables from existing raw (no Jira), both halves. Caller sets the ambient tenant.</summary>
    Task<SyncRunResult> RunRederiveAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Claims the project's Running slot for a sync. Started carries the run to poll.</summary>
    Task<SyncRunResult> StartProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Claims the project's Running slot for a re-derive. Started carries the run to poll.</summary>
    Task<SyncRunResult> StartRederiveAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Does the extract for an already-started run. A run that is no longer Running is left alone.</summary>
    Task<SyncRunResult> ExecuteProjectAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Does the derivation for an already-started run. A run that is no longer Running is left alone.</summary>
    Task<SyncRunResult> ExecuteRederiveAsync(Guid runId, CancellationToken ct = default);
}
