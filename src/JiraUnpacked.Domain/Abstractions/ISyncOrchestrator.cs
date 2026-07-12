namespace JiraUnpacked.Domain.Abstractions;

public enum SyncOutcome { Completed, AlreadyRunning, ProjectNotFound }

public sealed record SyncRunView(
    Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt,
    int IssuesProcessed, string? Error, DateTime? NextRunAt, string Kind);

public sealed record SyncRunResult(SyncOutcome Outcome, SyncRunView? Run);

public interface ISyncOrchestrator
{
    /// <summary>Runs one project's extract. The caller MUST have established the ambient tenant (project owner).</summary>
    Task<SyncRunResult> RunProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Re-derive modeled tables from existing raw (no Jira). Caller sets the ambient tenant.</summary>
    Task<SyncRunResult> RunRederiveAsync(Guid projectId, CancellationToken ct = default);
}
