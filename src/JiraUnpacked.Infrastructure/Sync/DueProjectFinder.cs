using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

/// <summary>
/// The ONE deliberate cross-tenant read: finds due projects across all tenants using the
/// privileged connection + IgnoreQueryFilters, selecting scheduling metadata only.
/// </summary>
public sealed class DueProjectFinder : IDueProjectFinder
{
    private readonly IPrivilegedDbContextFactory _factory;
    private readonly IClock _clock;

    public DueProjectFinder(IPrivilegedDbContextFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DueProject>> GetDueProjectsAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        await using var db = _factory.Create();

        var candidates = await db.JiraProjects.IgnoreQueryFilters()
            .Where(p => db.JiraQueries.IgnoreQueryFilters().Any(q => q.JiraProjectId == p.Id))
            .Select(p => new
            {
                p.Id,
                p.EndUserId,
                p.SyncIntervalMinutes,
                HasRunning = db.SyncRuns.IgnoreQueryFilters()
                    .Any(r => r.JiraProjectId == p.Id && r.Status == SyncRunStatus.Running),
                LastSuccess = db.SyncRuns.IgnoreQueryFilters()
                    .Where(r => r.JiraProjectId == p.Id && r.Status == SyncRunStatus.Succeeded && r.Kind == SyncRunKind.Sync)
                    .Max(r => (DateTime?)r.StartedAt)
            })
            .ToListAsync(ct);

        return candidates
            .Where(p => !p.HasRunning &&
                        (p.LastSuccess == null || p.LastSuccess.Value.AddMinutes(p.SyncIntervalMinutes) <= now))
            .Select(p => new DueProject(p.Id, p.EndUserId, p.SyncIntervalMinutes))
            .ToList();
    }
}
