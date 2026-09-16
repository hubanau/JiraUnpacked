using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JiraUnpacked.Api.Sync;

/// <summary>
/// Performs runs handed over by a request. One loop, one run at a time.
///
/// The point of it is the cancellation token: work started here follows the PROCESS lifetime,
/// not the request that asked for it. A sync of a large project outlives the HTTP call by
/// minutes, and running it inline meant a closed tab or a proxy timeout cancelled it mid-pull.
///
/// Serial rather than parallel on purpose. A sync is almost entirely waiting on Jira, and Jira
/// rate-limits per account; running several at once buys little and makes 429s likelier for
/// everyone. If that ever needs revisiting, it is a change to this loop alone.
/// </summary>
public sealed class SyncQueueWorker : BackgroundService
{
    private readonly ISyncQueue _queue;
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISyncCancellation _cancellation;
    private readonly ILogger<SyncQueueWorker> _logger;

    public SyncQueueWorker(ISyncQueue queue, ITenantScope tenantScope,
        IServiceScopeFactory scopeFactory, ISyncCancellation cancellation,
        ILogger<SyncQueueWorker> logger)
    {
        _queue = queue;
        _tenantScope = tenantScope;
        _scopeFactory = scopeFactory;
        _cancellation = cancellation;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Both are required and neither is optional: the tenant scope makes the query
                // filters resolve to this run's owner, and a FRESH DI scope gives it its own
                // DbContext — the request's was disposed the moment the 202 was written.
                using (_tenantScope.BeginScope(item.EndUserId))
                using (var scope = _scopeFactory.CreateScope())
                // Registered here rather than deeper down because this is the only place that
                // spans the whole run: the token has to exist before the work starts and be gone
                // once it ends, or the registry grows a leak per sync.
                using (_cancellation.Register(item.RunId, stoppingToken, out var runToken))
                {
                    var orchestrator = scope.ServiceProvider.GetRequiredService<ISyncOrchestrator>();
                    var result = item.IsRederive
                        ? await orchestrator.ExecuteRederiveAsync(item.RunId, runToken)
                        : await orchestrator.ExecuteProjectAsync(item.RunId, runToken);

                    if (result.Run is not null)
                        _logger.LogInformation("Background {Kind} run {RunId} finished as {Status}.",
                            result.Run.Kind, item.RunId, result.Run.Status);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown. The run stays Running and StuckRunReaperJob reaps it — the same
                // outcome an interrupted sync has always had.
                _logger.LogInformation("Shutting down with run {RunId} still in flight.", item.RunId);
                throw;
            }
            catch (Exception ex)
            {
                // The orchestrator records its own failures on the run; reaching here means
                // something outside that, and the loop must survive it or one bad run stops
                // every later one.
                _logger.LogError(ex, "Background run {RunId} threw outside the orchestrator.", item.RunId);
            }
        }
    }
}
