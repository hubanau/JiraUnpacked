namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// One handed-off run. Carries the tenant because the worker runs outside any request and must
/// re-establish the ambient tenant itself — every read in the pipeline is filtered by it, and a
/// worker that forgot would quietly do the work against nobody's data.
/// </summary>
public readonly record struct SyncWorkItem(Guid EndUserId, Guid RunId, bool IsRederive);

/// <summary>
/// Hand-off between the request that starts a run and the worker that performs it.
///
/// Deliberately in-memory: a queued item is only ever a pointer to a Running row that already
/// exists in the database, so a process restart loses the pointer, not the record — the run is
/// left Running and StuckRunReaperJob reaps it, which is the same path an interrupted sync
/// already took before any of this existed.
/// </summary>
public interface ISyncQueue
{
    void Enqueue(SyncWorkItem item);

    IAsyncEnumerable<SyncWorkItem> ReadAllAsync(CancellationToken ct);
}
