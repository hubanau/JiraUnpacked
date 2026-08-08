using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace JiraUnpacked.Infrastructure.Sync;

/// <summary>
/// Marks stuck Running runs (crashed syncs) as Failed so they stop blocking the project — the
/// partial unique index permits one Running run per project, and retention never purges Running.
/// Enumerates (project, tenant) with the privileged context (metadata only), then updates inside
/// each tenant's scope: a privileged READER, no privileged writer.
/// </summary>
[DisallowConcurrentExecution]
public sealed class StuckRunReaperJob : IJob
{
    private readonly IPrivilegedDbContextFactory _privileged;
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly SyncSchedulerOptions _options;
    private readonly ILogger<StuckRunReaperJob> _logger;

    public StuckRunReaperJob(
        IPrivilegedDbContextFactory privileged, ITenantScope tenantScope,
        IServiceScopeFactory scopeFactory, IClock clock,
        IOptions<SyncSchedulerOptions> options, ILogger<StuckRunReaperJob> logger)
    {
        _privileged = privileged;
        _tenantScope = tenantScope;
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context) => RunOnceAsync(context.CancellationToken);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var cutoff = _clock.UtcNow.AddMinutes(-_options.StuckRunTimeoutMinutes);

        List<(Guid ProjectId, Guid EndUserId)> projects;
        await using (var db = _privileged.Create())
        {
            var rows = await db.JiraProjects.IgnoreQueryFilters()
                .Select(p => new { p.Id, p.EndUserId })
                .ToListAsync(ct);
            projects = rows.Select(x => (x.Id, x.EndUserId)).ToList();
        }

        foreach (var (projectId, endUserId) in projects)
        {
            try
            {
                using (_tenantScope.BeginScope(endUserId))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var stuck = await db.SyncRuns
                        .Where(r => r.JiraProjectId == projectId
                                    && r.Status == SyncRunStatus.Running
                                    && r.StartedAt < cutoff)
                        .ToListAsync(ct);

                    if (stuck.Count == 0) continue;

                    foreach (var run in stuck)
                    {
                        run.Status = SyncRunStatus.Failed;
                        run.Error = "Marked failed: exceeded maximum runtime (stuck-run reaper).";
                        run.FinishedAt = _clock.UtcNow;
                    }
                    await db.SaveChangesAsync(ct);
                    _logger.LogWarning("Reaped {Count} stuck sync run(s) for project {ProjectId}.", stuck.Count, projectId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stuck-run reaper failed for project {ProjectId}.", projectId);
            }
        }
    }
}
