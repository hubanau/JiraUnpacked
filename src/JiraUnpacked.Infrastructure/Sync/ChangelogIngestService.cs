using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed class ChangelogIngestService : IChangelogIngestService
{
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly IClock _clock;

    public ChangelogIngestService(AppDbContext db, IJiraApiClient jira, IClock clock)
    { _db = db; _jira = jira; _clock = clock; }

    public async Task IngestProjectAsync(Guid projectId, JiraCredentials creds, CancellationToken ct = default)
    {
        var snapshots = await _db.RawIssueSnapshots.Where(r => r.JiraProjectId == projectId).ToListAsync(ct);
        var existingByJiraId = await _db.RawChangelogs
            .Where(x => x.JiraProjectId == projectId)
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
        await _db.SaveChangesAsync(ct);   // hoisted out of the loop — one save per project
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
