using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JiraUnpacked.Infrastructure.Sync;

/// <summary>
/// Removes stored issues that are no longer in the project's scope.
///
/// Snapshots were only ever upserted, so an issue deleted in Jira, moved to another project, or
/// dropped by a narrowed query stayed in every count, metric and gap check indefinitely.
/// </summary>
public sealed partial class SyncOrchestrator
{
    /// <summary>Issues per delete batch — bounds how many rows one save touches.</summary>
    private const int PurgeBatchSize = 500;

    /// <summary>
    /// An incremental reconcile deletes at most this share of the stored issues (and at least
    /// <see cref="PurgeFloor"/>). Beyond it the listing is more likely wrong than the data — a
    /// permissions change on the Jira account, say — and a scope that genuinely shrank that far
    /// is a changed query, which forces a full pull and takes the exact path instead.
    /// </summary>
    private const double PurgeShareCap = 0.10;
    private const int PurgeFloor = 50;

    private async Task ReconcileScopeAsync(
        SyncRun run, JiraProject project, JiraCredentials creds, IReadOnlyList<string> scopeJql,
        bool fullPull, CancellationToken ct)
    {
        var projectId = project.Id;
        List<string> gone;

        if (fullPull)
        {
            // Exact, and free: a full pull that reached this point rewrote every in-scope snapshot
            // at or after the attempt's data instant, so anything older was not returned by Jira.
            var asOf = run.FetchedAsOf ?? run.StartedAt;
            gone = await _db.RawIssueSnapshots
                .Where(r => r.JiraProjectId == projectId && r.FetchedAt < asOf)
                .Select(r => r.JiraIssueId)
                .ToListAsync(ct);
        }
        else
        {
            // An incremental pull sees only what changed, so ask Jira for the scope's ids.
            var inScope = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var jql in scopeJql)
                {
                    string? token = null;
                    do
                    {
                        var page = await _jira.SearchIssueIdsPageAsync(creds, jql, token, ct);
                        inScope.UnionWith(page.JiraIssueIds);
                        token = page.NextPageToken;
                    } while (token is not null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort: an unanswered listing proves nothing is gone, so nothing is removed,
                // and the sync it belongs to carries on.
                _logger.LogWarning(
                    "Sync {ProjectKey}: could not list the scope's issue ids ({ExceptionType}); skipping reconciliation.",
                    project.JiraProjectKey, ex.GetType().Name);
                return;
            }

            var stored = await _db.RawIssueSnapshots
                .Where(r => r.JiraProjectId == projectId)
                .Select(r => r.JiraIssueId)
                .ToListAsync(ct);
            gone = stored.Where(id => !inScope.Contains(id)).ToList();

            var cap = Math.Max(PurgeFloor, (int)(stored.Count * PurgeShareCap));
            if (gone.Count > cap)
            {
                _logger.LogWarning(
                    "Sync {ProjectKey}: Jira no longer lists {Gone} of {Stored} stored issue(s) — more than the "
                    + "{Cap} an incremental reconcile may remove. Keeping them; change the project's queries or "
                    + "re-add the project to force a full pull if the scope really shrank.",
                    project.JiraProjectKey, gone.Count, stored.Count, cap);
                return;
            }
        }

        if (gone.Count == 0) return;

        await PurgeIssuesAsync(projectId, gone, ct);
        _logger.LogInformation(
            "Sync {ProjectKey}: removed {Count} issue(s) no longer in scope (deleted or moved in Jira, or outside the queries).",
            project.JiraProjectKey, gone.Count);
    }

    /// <summary>
    /// Deletes every stored trace of the given issues — raw rows, the modelled issue, its history —
    /// a batch at a time. Children pointing at a removed parent are detached first: the self-FK is
    /// Restrict, and the hierarchy pass recomputes parents anyway.
    /// </summary>
    private async Task PurgeIssuesAsync(Guid projectId, IReadOnlyList<string> jiraIssueIds, CancellationToken ct)
    {
        foreach (var chunk in jiraIssueIds.Chunk(PurgeBatchSize))
        {
            var ids = chunk.ToList();

            var issues = await _db.Issues
                .Where(i => i.JiraProjectId == projectId && ids.Contains(i.JiraIssueId))
                .ToListAsync(ct);
            var issueIds = issues.Select(i => (Guid?)i.Id).ToList();

            var children = await _db.Issues
                .Where(i => i.JiraProjectId == projectId && issueIds.Contains(i.ParentIssueId))
                .ToListAsync(ct);
            foreach (var child in children) child.ParentIssueId = null;

            _db.IssueChangeHistory.RemoveRange(await _db.IssueChangeHistory
                .Where(h => h.JiraProjectId == projectId && ids.Contains(h.JiraIssueId)).ToListAsync(ct));
            _db.RawChangelogs.RemoveRange(await _db.RawChangelogs
                .Where(r => r.JiraProjectId == projectId && ids.Contains(r.JiraIssueId)).ToListAsync(ct));
            _db.RawIssueSnapshots.RemoveRange(await _db.RawIssueSnapshots
                .Where(r => r.JiraProjectId == projectId && ids.Contains(r.JiraIssueId)).ToListAsync(ct));
            // A child that is itself being removed is the same tracked instance, so removing it
            // here overrides the modification above.
            _db.Issues.RemoveRange(issues);

            await _db.SaveChangesAsync(ct);

            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                if (entry.Entity is Issue or IssueChangeHistory or RawChangelog or RawIssueSnapshot)
                    entry.State = EntityState.Detached;
        }
    }
}
