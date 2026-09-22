using System.Collections.Concurrent;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Sync;

/// <summary>
/// In-memory registry of the runs this process is currently executing.
///
/// In-memory is the honest scope: a CancellationTokenSource cannot outlive the process holding
/// it, and a run whose process is gone is not cancellable — it is orphaned, which is the
/// reaper's job rather than this one's.
/// </summary>
public sealed class SyncCancellation : ISyncCancellation
{
    private readonly ConcurrentDictionary<Guid, (Guid Owner, CancellationTokenSource Cts)> _running = new();

    /// <summary>
    /// Remembered separately from the token source, and NOT removed when the run ends: the
    /// orchestrator asks this question while unwinding, after the registration is disposed, and
    /// an answer of "no" there would record a deliberate stop as an unexpected failure.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _cancelled = new();

    public IDisposable Register(Guid runId, CancellationToken linkedTo, out CancellationToken token) =>
        Register(runId, Guid.Empty, linkedTo, out token);

    public IDisposable Register(Guid runId, Guid ownerId, CancellationToken linkedTo, out CancellationToken token)
    {
        // Linked, so shutting the process down still stops the work — the run then ends as
        // interrupted rather than cancelled, which is what the orchestrator distinguishes.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        _running[runId] = (ownerId, cts);
        token = cts.Token;
        return new Registration(this, runId, cts);
    }

    public bool Cancel(Guid runId)
    {
        if (!_running.TryGetValue(runId, out var entry)) return false;
        var cts = entry.Cts;

        // Recorded BEFORE signalling: the worker may react on another thread the instant the
        // token trips, and it must not find this flag missing.
        _cancelled[runId] = 1;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished between the lookup and here: there is nothing left to stop.
            return false;
        }
        return true;
    }

    public bool WasCancelled(Guid runId) => _cancelled.ContainsKey(runId);

    public int CancelAllFor(Guid ownerId)
    {
        if (ownerId == Guid.Empty) return 0;   // unowned registrations are never swept up
        var count = 0;
        foreach (var (runId, entry) in _running.ToArray())
            if (entry.Owner == ownerId && Cancel(runId)) count++;
        return count;
    }

    private sealed class Registration : IDisposable
    {
        private readonly SyncCancellation _owner;
        private readonly Guid _runId;
        private readonly CancellationTokenSource _cts;
        public Registration(SyncCancellation owner, Guid runId, CancellationTokenSource cts)
        { _owner = owner; _runId = runId; _cts = cts; }

        public void Dispose()
        {
            _owner._running.TryRemove(_runId, out _);
            _cts.Dispose();

            // The cancelled-run memory is bounded by how often anyone presses the button, and a
            // run id is 16 bytes; trimming it would risk forgetting a cancellation still being
            // unwound, which is the one thing this must not do.
        }
    }
}
