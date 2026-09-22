using JiraUnpacked.Domain.Abstractions.Jira;
namespace JiraUnpacked.Domain.Abstractions;
public interface IChangelogIngestService
{
    /// <param name="afterSnapshotId">
    /// Resume point: only snapshots ordered after this id are ingested. Null starts from the
    /// beginning. The order is by snapshot Id, which is what makes the cursor meaningful.
    /// </param>
    /// <param name="alreadyDone">
    /// How many issues a previous attempt finished, so the reported progress continues rather
    /// than restarting at zero.
    /// </param>
    /// <param name="onBatch">
    /// Called after each batch with (done, total, lastSnapshotId). Optional, and awaited: the
    /// caller commits, and it commits the batch's rows and that id together. When it is null the
    /// service saves for itself. This phase re-reads every stored issue and fetches extra
    /// changelog pages for the ones whose history did not fit inline, which on a large project
    /// runs for many minutes with nothing else to show for itself.
    /// </param>
    Task IngestProjectAsync(
        Guid projectId, JiraCredentials creds,
        Guid? afterSnapshotId = null,
        int alreadyDone = 0,
        Func<int, int, Guid, CancellationToken, Task>? onBatch = null,
        CancellationToken ct = default);
}
