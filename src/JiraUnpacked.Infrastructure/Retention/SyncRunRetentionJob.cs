using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace JiraUnpacked.Infrastructure.Retention;

/// <summary>
/// Trims SyncRun history. Enumerates (project, tenant) pairs with the privileged context —
/// scheduling metadata only — then deletes inside each tenant's scope, so this job adds a
/// privileged READER and no privileged writer: every delete still runs under RLS.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SyncRunRetentionJob : IJob
{
    private readonly IPrivilegedDbContextFactory _privileged;
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly RetentionOptions _options;
    private readonly ILogger<SyncRunRetentionJob> _logger;

    public SyncRunRetentionJob(
        IPrivilegedDbContextFactory privileged, ITenantScope tenantScope,
        IServiceScopeFactory scopeFactory, IClock clock,
        IOptions<RetentionOptions> options, ILogger<SyncRunRetentionJob> logger)
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
        if (!_options.Enabled) return;

        var cutoff = _clock.UtcNow.AddDays(-_options.SyncRunDays);

        // Cross-tenant metadata read only: project id + owning tenant, no issue content.
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
                // Scope BEFORE the DI scope so the connection opens with the right tenant GUC.
                using (_tenantScope.BeginScope(endUserId))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // The watermark anchor: the newest Succeeded + Sync run must survive at any age,
                    // or the next incremental sync loses its "updated >=" floor and re-pulls everything.
                    var anchorId = await db.SyncRuns
                        .Where(r => r.JiraProjectId == projectId
                                    && r.Status == SyncRunStatus.Succeeded
                                    && r.Kind == SyncRunKind.Sync)
                        .OrderByDescending(r => r.StartedAt)
                        .Select(r => (Guid?)r.Id)
                        .FirstOrDefaultAsync(ct);

                    var doomed = await db.SyncRuns
                        .Where(r => r.JiraProjectId == projectId
                                    && r.StartedAt < cutoff
                                    && r.Status != SyncRunStatus.Running   // never orphan an in-flight sync
                                    && (anchorId == null || r.Id != anchorId))
                        .ToListAsync(ct);

                    if (doomed.Count == 0) continue;

                    db.SyncRuns.RemoveRange(doomed);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "Retention purged {Count} sync runs for project {ProjectId}.", doomed.Count, projectId);
                }
            }
            catch (Exception ex)
            {
                // One tenant's failure must not stop the sweep.
                _logger.LogError(ex, "Retention purge failed for project {ProjectId}.", projectId);
            }
        }
    }
}
