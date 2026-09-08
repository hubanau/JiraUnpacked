using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed class ChangelogIngestService : IChangelogIngestService
{
    /// <summary>
    /// Issues per batch. Small enough that a batch's payloads are a bounded cost, large enough
    /// that the per-batch round trips stay negligible against the work they do.
    /// </summary>
    private const int BatchSize = 250;

    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly IClock _clock;

    public ChangelogIngestService(AppDbContext db, IJiraApiClient jira, IClock clock)
    { _db = db; _jira = jira; _clock = clock; }

    public async Task IngestProjectAsync(Guid projectId, JiraCredentials creds, CancellationToken ct = default)
    {
        // Ids only. Loading the snapshot ENTITIES for a whole project pulls every issue payload
        // into memory at once — hundreds of megabytes on a large project, on top of whatever the
        // extract phase just did — so the payloads are fetched a batch at a time below.
        var snapshotIds = await _db.RawIssueSnapshots
            .Where(r => r.JiraProjectId == projectId)
            .Select(r => r.Id)
            .ToListAsync(ct);

        foreach (var batch in snapshotIds.Chunk(BatchSize))
        {
            await IngestBatchAsync(projectId, batch, creds, ct);
        }
    }

    /// <summary>
    /// One batch, end to end: load the payloads, write their changelogs, save, then let the whole
    /// batch go. Keeping the context clean between batches is what bounds the memory this phase
    /// uses, and what stops change detection re-scanning every issue already processed.
    /// </summary>
    private async Task IngestBatchAsync(
        Guid projectId, IReadOnlyList<Guid> ids, JiraCredentials creds, CancellationToken ct)
    {
        var snapshots = await _db.RawIssueSnapshots.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        var jiraIds = snapshots.Select(s => s.JiraIssueId).ToList();

        // Scoped to this batch rather than the whole project, for the same reason.
        var existingByJiraId = await _db.RawChangelogs
            .Where(x => x.JiraProjectId == projectId && jiraIds.Contains(x.JiraIssueId))
            .ToDictionaryAsync(x => x.JiraIssueId, ct);

        foreach (var snap in snapshots)
        {
            var histories = await AssembleHistoriesJsonAsync(snap, creds, ct);
            var payload = "{\"histories\":[" + string.Join(",", histories) + "]}";

            if (existingByJiraId.TryGetValue(snap.JiraIssueId, out var existing))
            {
                existing.Payload = payload; existing.FetchedAt = _clock.UtcNow;
            }
            else
            {
                _db.RawChangelogs.Add(new RawChangelog
                {
                    Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = snap.JiraIssueId,
                    Payload = payload, FetchedAt = _clock.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync(ct);   // one save per batch

        foreach (var entry in _db.ChangeTracker.Entries().ToList())
            if (entry.Entity is RawIssueSnapshot or RawChangelog)
                entry.State = EntityState.Detached;
    }

    // Returns each history entry as a raw JSON string, from the embedded changelog if complete, else by paging.
    private async Task<List<string>> AssembleHistoriesJsonAsync(RawIssueSnapshot snap, JiraCredentials creds, CancellationToken ct)
    {
        var result = new List<string>();
        using var doc = JsonDocument.Parse(snap.Payload);
        var root = doc.RootElement;

        int total = 0;
        if (root.TryGetProperty("changelog", out var cl) && cl.ValueKind == JsonValueKind.Object)
        {
            total = cl.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : 0;
            if (cl.TryGetProperty("histories", out var hs) && hs.ValueKind == JsonValueKind.Array)
                foreach (var h in hs.EnumerateArray()) result.Add(h.GetRawText());
        }

        if (result.Count >= total) return result; // embedded is complete

        // Truncated: page the dedicated endpoint from the start (authoritative full list).
        result.Clear();
        var startAt = 0;
        while (true)
        {
            var page = await _jira.GetIssueChangelogPageAsync(creds, snap.JiraIssueId, startAt, ct);
            result.AddRange(page.ValuesJson);
            if (page.IsLast || page.ValuesJson.Count == 0) break;
            startAt += page.ValuesJson.Count;
        }
        return result;
    }
}
