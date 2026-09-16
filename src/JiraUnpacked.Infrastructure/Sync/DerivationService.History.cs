using System.Globalization;
using System.Text.Json;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using JiraUnpacked.Infrastructure.Jira;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed partial class DerivationService
{
    // Append-only, idempotent: skip (issue, historyId, field) already present.
    private async Task<int> DeriveChangeHistoryAsync(
        Guid projectId, int done, int total,
        Func<int, int, CancellationToken, Task>? onProgress, CancellationToken ct)
    {
        var logs = await _db.RawChangelogs.Where(r => r.JiraProjectId == projectId).ToListAsync(ct);
        var existing = (await _db.IssueChangeHistory.Where(h => h.JiraProjectId == projectId)
                .Select(h => new { h.JiraIssueId, h.JiraHistoryId, h.Field }).ToListAsync(ct))
            .Select(x => (x.JiraIssueId, x.JiraHistoryId, x.Field)).ToHashSet();

        foreach (var log in logs)
        {
            if (++done % ProgressEvery == 0 && onProgress is not null) await onProgress(done, total, ct);

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
        await _db.SaveChangesAsync(ct);
        return done;
    }

    // Second pass: resolve ParentIssueId (self-FK) by matching ParentJiraIssueId to a sibling in the project.
    private async Task DeriveHierarchyAsync(Guid projectId, CancellationToken ct)
    {
        var issues = await _db.Issues.Where(i => i.JiraProjectId == projectId).ToListAsync(ct);
        var byJiraId = issues.ToDictionary(i => i.JiraIssueId, i => i.Id);
        var changed = false;
        foreach (var issue in issues)
        {
            var target = issue.ParentJiraIssueId is not null && byJiraId.TryGetValue(issue.ParentJiraIssueId, out var pid)
                ? pid : (Guid?)null;
            if (issue.ParentIssueId != target) { issue.ParentIssueId = target; changed = true; }
        }
        if (changed) await _db.SaveChangesAsync(ct);
    }
}
