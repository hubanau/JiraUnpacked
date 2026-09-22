using System.Globalization;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using JiraUnpacked.Infrastructure.Jira;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed partial class DerivationService : IDerivationService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    public DerivationService(AppDbContext db, IClock clock) { _db = db; _clock = clock; }

    /// <summary>How often progress is published. Often enough to move, rarely enough to be free.</summary>
    private const int ProgressEvery = 250;

    public async Task DeriveProjectAsync(
        Guid projectId,
        string? resumeStage = null,
        string? resumeCursor = null,
        int alreadyDone = 0,
        int batchSize = 10_000,
        Func<string, string, int, int, CancellationToken, Task>? onBatch = null,
        CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return;
        var connectionId = project.JiraConnectionId;

        var mappings = await _db.FieldMappings.Where(m => m.JiraProjectId == projectId).ToListAsync(ct);
        string FieldFor(FieldRole role, string standard) => mappings.FirstOrDefault(m => m.Role == role)?.JiraFieldId ?? standard;
        var fields = new DerivationFields(
            FieldFor(FieldRole.Status, "status"),
            FieldFor(FieldRole.Assignee, "assignee"),
            mappings.FirstOrDefault(m => m.Role == FieldRole.Sprint)?.JiraFieldId,
            mappings.FirstOrDefault(m => m.Role == FieldRole.StoryPoints)?.JiraFieldId,
            mappings.FirstOrDefault(m => m.Role == FieldRole.Team)?.JiraFieldId);

        // Counted up front so the bar has its denominator before the first row. These are COUNTS,
        // not payloads — the rows themselves are read a batch at a time below.
        var total = await _db.RawIssueSnapshots.CountAsync(r => r.JiraProjectId == projectId, ct)
                  + await _db.RawChangelogs.CountAsync(r => r.JiraProjectId == projectId, ct);
        var done = alreadyDone;

        var start = ResumeStage.IndexOf(resumeStage);

        if (start <= ResumeStage.IndexOf(ResumeStage.DeriveIssues))
        {
            var after = resumeStage == ResumeStage.DeriveIssues && Guid.TryParse(resumeCursor, out var c)
                ? c : (Guid?)null;
            done = await DeriveIssuesAsync(projectId, connectionId, fields, after, done, total, batchSize, onBatch, ct);
        }

        if (start <= ResumeStage.IndexOf(ResumeStage.DeriveHistory))
        {
            var after = resumeStage == ResumeStage.DeriveHistory && Guid.TryParse(resumeCursor, out var c)
                ? c : (Guid?)null;
            done = await DeriveChangeHistoryAsync(projectId, after, done, total, batchSize, onBatch, ct);
        }

        if (start <= ResumeStage.IndexOf(ResumeStage.DeriveHierarchy))
        {
            var after = resumeStage == ResumeStage.DeriveHierarchy && Guid.TryParse(resumeCursor, out var c)
                ? c : (Guid?)null;
            await DeriveHierarchyAsync(projectId, after, total, done, batchSize, onBatch, ct);
        }
    }

    /// <summary>The mapped field ids this project models by, resolved once per run.</summary>
    private sealed record DerivationFields(
        string Status, string Assignee, string? Sprint, string? Points, string? Team);

    /// <summary>
    /// Models issues from their raw snapshots, one batch at a time.
    ///
    /// The version this replaces loaded EVERY snapshot — payloads and all — plus every existing
    /// Issue, before touching the first row. On a 92,000-issue project that is gigabytes resident
    /// for the length of the stage, and it is where every long sync died. ChangelogIngestService
    /// has always done it the bounded way and says why; this now matches it.
    /// </summary>
    private async Task<int> DeriveIssuesAsync(
        Guid projectId, Guid connectionId, DerivationFields fields, Guid? after,
        int done, int total, int batchSize,
        Func<string, string, int, int, CancellationToken, Task>? onBatch, CancellationToken ct)
    {
        // Caches live across batches: resolving the same status twice is wasted work, and they
        // hold ids rather than entities, so they stay small however many batches there are.
        var statusCache = new Dictionary<string, Guid>();
        var typeCache = new Dictionary<string, Guid>();
        var prioCache = new Dictionary<string, Guid>();
        var userCache = new Dictionary<string, Guid>();
        var sprintCache = new Dictionary<string, Guid>();
        var teamCache = new Dictionary<string, Guid>();

        while (true)
        {
            var batch = await _db.RawIssueSnapshots
                .Where(r => r.JiraProjectId == projectId && (after == null || r.Id > after))
                .OrderBy(r => r.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            // Scoped to the batch, not the project: the dictionary of every Issue in the project
            // was the second unbounded load in this method.
            var batchJiraIds = batch.Select(b => b.JiraIssueId).ToList();
            var existingIssues = await _db.Issues
                .Where(i => i.JiraProjectId == projectId && batchJiraIds.Contains(i.JiraIssueId))
                .ToDictionaryAsync(i => i.JiraIssueId, ct);

            foreach (var snap in batch)
            {
                done++;
                using var doc = JsonDocument.Parse(snap.Payload, JiraJson.Document);
                var root = doc.RootElement;
                if (!root.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object) continue;

                existingIssues.TryGetValue(snap.JiraIssueId, out var found);
                var issue = found ?? new Issue { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = snap.JiraIssueId };

                issue.JiraKey = GetString(root, "key") ?? issue.JiraKey ?? snap.JiraIssueId;
                issue.Summary = GetString(f, "summary");
                issue.JiraCreatedAt = GetDate(f, "created") ?? issue.JiraCreatedAt;
                issue.JiraUpdatedAt = GetDate(f, "updated") ?? issue.JiraUpdatedAt;
                issue.ParentJiraIssueId = f.TryGetProperty("parent", out var par) && par.ValueKind == JsonValueKind.Object
                    ? GetString(par, "id") : null;
                issue.LastSyncedAt = _clock.UtcNow;

                issue.IssueTypeId = await ResolveTypeAsync(f, "issuetype", connectionId, typeCache, ct);
                issue.PriorityId = await ResolvePriorityAsync(f, "priority", connectionId, prioCache, ct);
                issue.StatusId = await ResolveStatusAsync(f, fields.Status, connectionId, statusCache, ct);
                issue.AssigneeId = await ResolveUserAsync(f, fields.Assignee, connectionId, userCache, ct);
                issue.SprintId = fields.Sprint is null ? null : await ResolveSprintAsync(f, fields.Sprint, projectId, sprintCache, ct);
                issue.TeamId = fields.Team is null ? null : await ResolveTeamAsync(f, fields.Team, projectId, teamCache, ct);
                issue.StoryPoints = fields.Points is null ? null : GetDecimal(f, fields.Points);

                if (_db.Entry(issue).State == EntityState.Detached) _db.Issues.Add(issue);
            }

            after = batch[^1].Id;
            if (onBatch is not null) await onBatch(ResumeStage.DeriveIssues, after.Value.ToString(), done, total, ct);
            else await _db.SaveChangesAsync(ct);

            // After the save, never before — detaching first discards the rows it must write.
            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                if (entry.Entity is RawIssueSnapshot or Issue) entry.State = EntityState.Detached;
        }

        return done;
    }

    // ---- JSON helpers ----
    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? GetDecimal(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
    private static DateTime? GetDate(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
           ? dt : null;

    // ---- dictionary resolvers (upsert by grain unique key, cache by jiraId) ----
    private async Task<Guid?> ResolveStatusAsync(JsonElement fields, string field, Guid connId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var jid = GetString(s, "id"); if (jid is null) return null;
        if (cache.TryGetValue(jid, out var id)) return id;
        var name = GetString(s, "name") ?? jid;
        var category = s.TryGetProperty("statusCategory", out var sc) && sc.ValueKind == JsonValueKind.Object ? GetString(sc, "name") : null;
        var row = await _db.Statuses.FirstOrDefaultAsync(x => x.JiraConnectionId == connId && x.JiraStatusId == jid, ct);
        if (row is null) { row = new Status { Id = Guid.NewGuid(), JiraConnectionId = connId, JiraStatusId = jid, Name = name, StatusCategory = category }; _db.Statuses.Add(row); }
        else { row.Name = name; row.StatusCategory = category; }
        cache[jid] = row.Id; return row.Id;
    }
    private async Task<Guid?> ResolveTypeAsync(JsonElement fields, string field, Guid connId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var jid = GetString(s, "id"); if (jid is null) return null;
        if (cache.TryGetValue(jid, out var id)) return id;
        var name = GetString(s, "name") ?? jid;
        var row = await _db.IssueTypes.FirstOrDefaultAsync(x => x.JiraConnectionId == connId && x.JiraIssueTypeId == jid, ct);
        if (row is null) { row = new IssueType { Id = Guid.NewGuid(), JiraConnectionId = connId, JiraIssueTypeId = jid, Name = name, Class = IssueTypeClass.Other }; _db.IssueTypes.Add(row); }
        else row.Name = name;
        cache[jid] = row.Id; return row.Id;
    }
    private async Task<Guid?> ResolvePriorityAsync(JsonElement fields, string field, Guid connId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var jid = GetString(s, "id"); if (jid is null) return null;
        if (cache.TryGetValue(jid, out var id)) return id;
        var name = GetString(s, "name") ?? jid;
        var row = await _db.Priorities.FirstOrDefaultAsync(x => x.JiraConnectionId == connId && x.JiraPriorityId == jid, ct);
        if (row is null) { row = new Priority { Id = Guid.NewGuid(), JiraConnectionId = connId, JiraPriorityId = jid, Name = name }; _db.Priorities.Add(row); }
        else row.Name = name;
        cache[jid] = row.Id; return row.Id;
    }
    private async Task<Guid?> ResolveUserAsync(JsonElement fields, string field, Guid connId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var jid = GetString(s, "accountId"); if (jid is null) return null;
        if (cache.TryGetValue(jid, out var id)) return id;
        var name = GetString(s, "displayName") ?? jid;
        var row = await _db.JiraUsers.FirstOrDefaultAsync(x => x.JiraConnectionId == connId && x.JiraAccountId == jid, ct);
        if (row is null) { row = new JiraUser { Id = Guid.NewGuid(), JiraConnectionId = connId, JiraAccountId = jid, DisplayName = name }; _db.JiraUsers.Add(row); }
        else row.DisplayName = name;
        cache[jid] = row.Id; return row.Id;
    }
    // Sprint field is typically an array; take the last (most recent) sprint object.
    private async Task<Guid?> ResolveSprintAsync(JsonElement fields, string field, Guid projectId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s)) return null;
        JsonElement sprint;
        if (s.ValueKind == JsonValueKind.Array) { if (s.GetArrayLength() == 0) return null; sprint = s[s.GetArrayLength() - 1]; }
        else if (s.ValueKind == JsonValueKind.Object) sprint = s;
        else return null;
        var jid = sprint.TryGetProperty("id", out var idv)
            ? (idv.ValueKind == JsonValueKind.Number ? idv.GetInt64().ToString() : GetString(sprint, "id")) : null;
        if (jid is null) return null;
        if (cache.TryGetValue(jid, out var cid)) return cid;
        var name = GetString(sprint, "name") ?? jid;
        var state = GetString(sprint, "state");
        var row = await _db.Sprints.FirstOrDefaultAsync(x => x.JiraProjectId == projectId && x.JiraSprintId == jid, ct);
        if (row is null) { row = new Sprint { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraSprintId = jid, Name = name, State = state }; _db.Sprints.Add(row); }
        else { row.Name = name; row.State = state; }
        cache[jid] = row.Id; return row.Id;
    }
    // Team field: object {id, value|name} or a scalar string.
    private async Task<Guid?> ResolveTeamAsync(JsonElement fields, string field, Guid projectId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s)) return null;
        string? jid, name;
        if (s.ValueKind == JsonValueKind.Object) { jid = GetString(s, "id") ?? GetString(s, "value"); name = GetString(s, "value") ?? GetString(s, "name") ?? jid; }
        else if (s.ValueKind == JsonValueKind.String) { jid = s.GetString(); name = jid; }
        else return null;
        if (jid is null) return null;
        if (cache.TryGetValue(jid, out var cid)) return cid;
        var row = await _db.Teams.FirstOrDefaultAsync(x => x.JiraProjectId == projectId && x.JiraTeamId == jid, ct);
        if (row is null) { row = new Team { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraTeamId = jid, Name = name! }; _db.Teams.Add(row); }
        else row.Name = name!;
        cache[jid] = row.Id; return row.Id;
    }
}
