using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

/// <summary>
/// Set-based removal of projects and everything derived from them, shared by project and
/// connection deletion. Callers own the transaction.
///
/// Explicit deletes rather than leaning on the JiraProject cascades, for two reasons. The Issues
/// self-link (ParentIssueId) is ON DELETE RESTRICT, which is checked row by row and so can refuse
/// a single statement that removes a parent together with its children; it has to be cut first.
/// And the Issues → dictionary links are NO ACTION, so issues must be gone before the
/// connection's statuses, types, priorities and users can be. Doing it in an order we chose is
/// easier to reason about than an order the database chooses.
///
/// Every statement goes through the tenant query filter (and RLS under it), so a project id that
/// is not the caller's deletes nothing.
/// </summary>
internal static class ProjectDataEraser
{
    /// <summary>
    /// Locks the given project rows until the transaction ends. A sync claim inserts a SyncRun
    /// referencing its project, which needs a key-share lock on that row — so while we hold this,
    /// no new run can start for these projects, and one that was mid-claim finishes first and is
    /// then visible to <see cref="AnyRunningAsync"/>.
    /// </summary>
    internal static async Task LockProjectsAsync(AppDbContext db, Guid[] projectIds, CancellationToken ct)
    {
        if (projectIds.Length == 0) return;
        await db.Database
            .SqlQuery<Guid>($@"SELECT ""Id"" AS ""Value"" FROM ""JiraProjects"" WHERE ""Id"" = ANY({projectIds}) FOR UPDATE")
            .ToListAsync(ct);
    }

    internal static Task<bool> AnyRunningAsync(AppDbContext db, Guid[] projectIds, CancellationToken ct) =>
        db.SyncRuns.AnyAsync(r => projectIds.Contains(r.JiraProjectId) && r.Status == SyncRunStatus.Running, ct);

    internal static async Task DeleteProjectsAsync(AppDbContext db, Guid[] projectIds, CancellationToken ct)
    {
        if (projectIds.Length == 0) return;

        // Cut every parent link that points at an issue about to go — including, should one ever
        // exist, a link from another project's issue.
        await db.Issues
            .Where(i => i.ParentIssueId != null
                        && db.Issues.Any(p => p.Id == i.ParentIssueId && projectIds.Contains(p.JiraProjectId)))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ParentIssueId, (Guid?)null), ct);

        await db.IssueChangeHistory.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.RawChangelogs.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.RawIssueSnapshots.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.Issues.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        // Issues referenced these (NO ACTION), so they go after the issues.
        await db.Sprints.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.Teams.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.SyncRuns.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.SavedFilters.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.JiraQueries.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.FieldMappings.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.StatusSemantics.Where(x => projectIds.Contains(x.JiraProjectId)).ExecuteDeleteAsync(ct);
        await db.JiraProjects.Where(x => projectIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }
}
