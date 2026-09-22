using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using JiraUnpacked.Infrastructure.Jira;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed partial class DerivationService
{
    /// <summary>
    /// Append-only and idempotent: an (issue, historyId, field) already stored is skipped.
    ///
    /// The dedup set is scoped to the batch. It used to hold every such key in the project —
    /// 1,477,699 rows on the real one — purely to answer "have I seen this". Per batch it answers
    /// the same question at a bounded cost, and the append-only result is identical because the
    /// keys it needs are exactly the ones belonging to the issues in hand.
    /// </summary>
    private async Task<int> DeriveChangeHistoryAsync(
        Guid projectId, Guid? after, int done, int total, int batchSize,
        Func<string, string, int, int, CancellationToken, Task>? onBatch, CancellationToken ct)
    {
        while (true)
        {
            var batch = await _db.RawChangelogs
                .Where(r => r.JiraProjectId == projectId && (after == null || r.Id > after))
                .OrderBy(r => r.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            var batchIssueIds = batch.Select(l => l.JiraIssueId).ToList();
            var existing = (await _db.IssueChangeHistory
                    .Where(h => h.JiraProjectId == projectId && batchIssueIds.Contains(h.JiraIssueId))
                    .Select(h => new { h.JiraIssueId, h.JiraHistoryId, h.Field })
                    .ToListAsync(ct))
                .Select(x => (x.JiraIssueId, x.JiraHistoryId, x.Field))
                .ToHashSet();

            foreach (var log in batch)
            {
                done++;

                using var doc = JsonDocument.Parse(log.Payload, JiraJson.Document);
                if (!doc.RootElement.TryGetProperty("histories", out var hs) || hs.ValueKind != JsonValueKind.Array) continue;
                foreach (var h in hs.EnumerateArray())
                {
                    var historyId = GetString(h, "id"); if (historyId is null) continue;
                    var author = h.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? GetString(a, "displayName") : null;
                    var changedAt = GetDate(h, "created") ?? _clock.UtcNow;
                    if (!h.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                    foreach (var it in items.EnumerateArray())
                    {
                        var fieldName = GetString(it, "field"); if (fieldName is null) continue;
                        if (!existing.Add((log.JiraIssueId, historyId, fieldName))) continue; // dedup
                        _db.IssueChangeHistory.Add(new IssueChangeHistory
                        {
                            Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = log.JiraIssueId,
                            JiraHistoryId = historyId, Field = fieldName,
                            FromValue = GetString(it, "fromString"), ToValue = GetString(it, "toString"),
                            Author = author, ChangedAt = changedAt
                        });
                    }
                }
            }

            after = batch[^1].Id;
            if (onBatch is not null) await onBatch(ResumeStage.DeriveHistory, after.Value.ToString(), done, total, ct);
            else await _db.SaveChangesAsync(ct);

            // After the save, never before — detaching first discards the rows it must write.
            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                if (entry.Entity is RawChangelog or IssueChangeHistory) entry.State = EntityState.Detached;
        }

        return done;
    }

    /// <summary>
    /// Second pass: resolve ParentIssueId (self-FK) by matching ParentJiraIssueId to a sibling.
    ///
    /// The lookup covers the whole project because a parent can live in any batch, but it holds
    /// two values per issue rather than whole entities — the issues themselves are still walked a
    /// batch at a time.
    /// </summary>
    private async Task DeriveHierarchyAsync(
        Guid projectId, Guid? after, int total, int done, int batchSize,
        Func<string, string, int, int, CancellationToken, Task>? onBatch, CancellationToken ct)
    {
        var byJiraId = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Select(i => new { i.JiraIssueId, i.Id })
            .ToDictionaryAsync(x => x.JiraIssueId, x => x.Id, ct);

        while (true)
        {
            var batch = await _db.Issues
                .Where(i => i.JiraProjectId == projectId && (after == null || i.Id > after))
                .OrderBy(i => i.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var issue in batch)
            {
                var target = issue.ParentJiraIssueId is not null
                             && byJiraId.TryGetValue(issue.ParentJiraIssueId, out var pid) ? pid : (Guid?)null;
                if (issue.ParentIssueId != target) issue.ParentIssueId = target;
            }

            after = batch[^1].Id;
            if (onBatch is not null) await onBatch(ResumeStage.DeriveHierarchy, after.Value.ToString(), done, total, ct);
            else await _db.SaveChangesAsync(ct);

            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                if (entry.Entity is Issue) entry.State = EntityState.Detached;
        }
    }
}
