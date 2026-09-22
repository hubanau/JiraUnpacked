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

        // Every project is a candidate. This used to require at least one JiraQuery, which was the
        // scheduled half of the same bug: a project with no query of its own is now synced in full
        // by the orchestrator, so excluding it here would leave it silently never syncing at all.
        var candidates = await db.JiraProjects.IgnoreQueryFilters()
            // A blocked account's scheduled syncs are paused (FR-A5). Users is the tenant root and
            // carries no filter; the reader role may select it.
            .Where(p => db.Users.Any(u => u.Id == p.EndUserId && u.Status == UserStatus.Active))
            .Select(p => new
            {
                p.Id,
                p.EndUserId,
                p.SyncIntervalMinutes,
                HasRunning = db.SyncRuns.IgnoreQueryFilters()
                    .Any(r => r.JiraProjectId == p.Id && r.Status == SyncRunStatus.Running),
                LastSuccess = db.SyncRuns.IgnoreQueryFilters()
                    .Where(r => r.JiraProjectId == p.Id && r.Status == SyncRunStatus.Succeeded && r.Kind == SyncRunKind.Sync)
                    .Max(r => (DateTime?)r.StartedAt),
                Latest = db.SyncRuns.IgnoreQueryFilters()
                    .Where(r => r.JiraProjectId == p.Id && r.Kind == SyncRunKind.Sync && r.Status != SyncRunStatus.Running)
                    .OrderByDescending(r => r.StartedAt)
                    .Select(r => new { r.Status, r.NextRunAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return candidates
            .Where(p => !p.HasRunning && IsDue(p.Latest?.Status, p.Latest?.NextRunAt, p.LastSuccess, p.SyncIntervalMinutes, now))
            .Select(p => new DueProject(p.Id, p.EndUserId, p.SyncIntervalMinutes))
            .ToList();
    }

    /// <summary>
    /// When the latest sync SUCCEEDED, the project is due one interval after it started, as it
    /// always was — so shortening the interval takes effect at once. When it failed or was
    /// stopped, it is due at the NextRunAt that run recorded: a failure's backoff, or a stop's
    /// full interval. This used to look at the last success only, so a failing project was
    /// retried every minute, and a project someone had just stopped was restarted by the next
    /// tick. A failed run with no NextRunAt (reaped, or from before the column) is due now.
    /// </summary>
    internal static bool IsDue(
        SyncRunStatus? latestStatus, DateTime? latestNextRunAt, DateTime? lastSuccess, int intervalMinutes, DateTime now)
    {
        if (latestStatus is null) return true;   // never synced
        if (latestStatus == SyncRunStatus.Succeeded)
            return lastSuccess is null || lastSuccess.Value.AddMinutes(intervalMinutes) <= now;
        return (latestNextRunAt ?? now) <= now;
    }
}
