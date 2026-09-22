using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using JiraUnpacked.Infrastructure.Jira;

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

    public async Task IngestProjectAsync(
        Guid projectId, JiraCredentials creds,
        Guid? afterSnapshotId = null,
        int alreadyDone = 0,
        Func<int, int, Guid, CancellationToken, Task>? onBatch = null,
        CancellationToken ct = default)
    {
        // Ids only. Loading the snapshot ENTITIES for a whole project pulls every issue payload
        // into memory at once — hundreds of megabytes on a large project, on top of whatever the
        // extract phase just did — so the payloads are fetched a batch at a time below.
        //
        // Ordered, and filtered past the resume point: the cursor is a snapshot Id, so it only
        // means anything if this walk is in that same order every time.
        var snapshotIds = await Pending(_db, projectId, afterSnapshotId)
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var total = alreadyDone + snapshotIds.Count;
        var done = alreadyDone;

        foreach (var batch in snapshotIds.Chunk(BatchSize))
        {
            await IngestBatchAsync(projectId, batch, creds, ct);
            done += batch.Length;

            // The cursor advances at the batch's own commit point, which already existed. This
            // batch is deliberately smaller than the 10,000 used elsewhere: every item here costs
            // a Jira round trip, so 250 is already seconds of work, and a smaller batch loses less
            // when a run dies — which is the whole point.
            if (onBatch is not null) await onBatch(done, total, batch[^1], ct);
            else await _db.SaveChangesAsync(ct);

            // After the save, never before: detaching first would discard the very rows the save
            // is meant to write.
            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                if (entry.Entity is RawIssueSnapshot or RawChangelog)
                    entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// The snapshots whose changelog is missing or older than the snapshot itself — the only ones
    /// with anything new to ingest.
    ///
    /// This walked EVERY snapshot in the project on every sync, calling Jira again for each issue
    /// with a long history: a 691-issue incremental sync of a 92,000-issue project took seventy
    /// minutes, most of it re-reading changelogs nothing had touched. A snapshot is rewritten only
    /// when it is fetched, so "changelog older than snapshot" is exactly "fetched since last
    /// ingested" — and it heals by itself: a snapshot a failed run fetched but never ingested
    /// still qualifies next time, whatever the checkpoints say.
    ///
    /// Public so the orchestrator can size its progress bar with the same predicate it will walk.
    /// </summary>
    public static IQueryable<RawIssueSnapshot> Pending(AppDbContext db, Guid projectId, Guid? afterSnapshotId = null) =>
        db.RawIssueSnapshots.Where(r => r.JiraProjectId == projectId
            && (afterSnapshotId == null || r.Id > afterSnapshotId)
            && !db.RawChangelogs.Any(c => c.JiraProjectId == projectId
                                         && c.JiraIssueId == r.JiraIssueId
                                         && c.FetchedAt > r.FetchedAt));

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
            payload = JiraJson.SanitiseForStorage(payload);   // jsonb cannot hold NUL — see JiraJson

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

        // No save here: the caller commits this batch and its cursor in one go, so that a cursor
        // can never outlive the rows it points past.
    }

    // Returns each history entry as a raw JSON string, from the embedded changelog if complete, else by paging.
    private async Task<List<string>> AssembleHistoriesJsonAsync(RawIssueSnapshot snap, JiraCredentials creds, CancellationToken ct)
    {
        var result = new List<string>();
        using var doc = JsonDocument.Parse(snap.Payload, JiraJson.Document);
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
