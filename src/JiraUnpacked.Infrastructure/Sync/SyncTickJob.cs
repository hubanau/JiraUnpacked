using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace JiraUnpacked.Infrastructure.Sync;

/// <summary>Thin tick: find due projects, run each inside its tenant scope + a fresh DI scope.</summary>
[DisallowConcurrentExecution]
public sealed class SyncTickJob : IJob
{
    private readonly IDueProjectFinder _finder;
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SyncTickJob> _logger;

    public SyncTickJob(IDueProjectFinder finder, ITenantScope tenantScope,
        IServiceScopeFactory scopeFactory, ILogger<SyncTickJob> logger)
    {
        _finder = finder; _tenantScope = tenantScope; _scopeFactory = scopeFactory; _logger = logger;
    }

    public Task Execute(IJobExecutionContext context) => RunOnceAsync(context.CancellationToken);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var due = await _finder.GetDueProjectsAsync(ct);
        foreach (var p in due)
        {
            try
            {
                using (_tenantScope.BeginScope(p.EndUserId))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var orchestrator = scope.ServiceProvider.GetRequiredService<ISyncOrchestrator>();
                    await orchestrator.RunProjectAsync(p.ProjectId, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync tick failed for project {ProjectId}", p.ProjectId);
            }
        }
    }
}
