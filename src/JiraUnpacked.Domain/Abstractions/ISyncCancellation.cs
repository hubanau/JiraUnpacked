namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Lets a request stop a run that a background worker is in the middle of.
///
/// The two live in different scopes and different call stacks — the request that started the run
/// returned its 202 long ago — so the only thing they can share is this registry, which is a
/// singleton for exactly that reason.
///
/// Cancellation is remembered as well as signalled. A token that has been cancelled cannot say
/// WHY it was cancelled, and the orchestrator has to tell a deliberate stop from the process
/// shutting down: one is a Cancelled run, the other leaves the run alone to be picked up again.
/// </summary>
public interface ISyncCancellation
{
    /// <summary>
    /// Registers a run as cancellable and returns its token. The caller disposes the returned
    /// registration when the run ends, which is what stops the registry growing forever.
    /// </summary>
    IDisposable Register(Guid runId, CancellationToken linkedTo, out CancellationToken token);

    /// <summary>
    /// As <see cref="Register(Guid, CancellationToken, out CancellationToken)"/>, remembering which
    /// tenant owns the run so <see cref="CancelAllFor"/> can reach it.
    /// </summary>
    IDisposable Register(Guid runId, Guid ownerId, CancellationToken linkedTo, out CancellationToken token);

    /// <summary>
    /// Stops every run this process is executing for one tenant — used when an account is blocked
    /// or deleted, which must not leave a sync writing on its behalf. Returns how many it signalled.
    /// </summary>
    int CancelAllFor(Guid ownerId);

    /// <summary>
    /// Signals the run to stop. False when this process is not the one running it — after a
    /// restart, say — in which case there is nothing here to cancel and the run is already
    /// orphaned for the reaper.
    /// </summary>
    bool Cancel(Guid runId);

    /// <summary>Whether this run was deliberately cancelled, as opposed to shut down or failed.</summary>
    bool WasCancelled(Guid runId);
}
