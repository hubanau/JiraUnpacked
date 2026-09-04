using System.Threading.Channels;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Sync;

/// <summary>
/// The hand-off channel, unbounded — which is safe here for a specific reason rather than by
/// optimism.
///
/// Every queued item corresponds to a SyncRun row already inserted with status Running, and the
/// partial unique index IX_SyncRuns_OneRunningPerProject permits exactly one of those per
/// project. Queue depth is therefore bounded by the number of projects, not by how often anyone
/// presses the button: a second press while one is queued loses the race in START and never
/// reaches here. A bounded channel would only add a rejection path that cannot be exercised.
/// </summary>
public sealed class SyncQueue : ISyncQueue
{
    private readonly Channel<SyncWorkItem> _channel =
        Channel.CreateUnbounded<SyncWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,   // one worker loop
            SingleWriter = false,  // any request thread
        });

    public void Enqueue(SyncWorkItem item)
    {
        // Cannot fail on an unbounded channel that is never completed; the writer only refuses
        // after Complete(), which nothing calls.
        _channel.Writer.TryWrite(item);
    }

    public IAsyncEnumerable<SyncWorkItem> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
