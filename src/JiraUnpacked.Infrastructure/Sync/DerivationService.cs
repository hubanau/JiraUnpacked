using System.Globalization;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed partial class DerivationService : IDerivationService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    public DerivationService(AppDbContext db, IClock clock) { _db = db; _clock = clock; }

    public async Task DeriveProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return;
        var connectionId = project.JiraConnectionId;

        var mappings = await _db.FieldMappings.Where(m => m.JiraProjectId == projectId).ToListAsync(ct);
        string FieldFor(FieldRole role, string standard) => mappings.FirstOrDefault(m => m.Role == role)?.JiraFieldId ?? standard;
        var statusField = FieldFor(FieldRole.Status, "status");
        var assigneeField = FieldFor(FieldRole.Assignee, "assignee");
        var sprintField = mappings.FirstOrDefault(m => m.Role == FieldRole.Sprint)?.JiraFieldId;
        var pointsField = mappings.FirstOrDefault(m => m.Role == FieldRole.StoryPoints)?.JiraFieldId;
        var teamField = mappings.FirstOrDefault(m => m.Role == FieldRole.Team)?.JiraFieldId;

        var snapshots = await _db.RawIssueSnapshots.Where(r => r.JiraProjectId == projectId).ToListAsync(ct);

        // Dictionary caches (jiraId -> surrogate Id) so we resolve each once.
        var statusCache = new Dictionary<string, Guid>();
        var typeCache = new Dictionary<string, Guid>();
        var prioCache = new Dictionary<string, Guid>();
        var userCache = new Dictionary<string, Guid>();
        var sprintCache = new Dictionary<string, Guid>();
        var teamCache = new Dictionary<string, Guid>();

        foreach (var snap in snapshots)
        {
            using var doc = JsonDocument.Parse(snap.Payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object) continue;

            var issue = await _db.Issues
                            .FirstOrDefaultAsync(i => i.JiraProjectId == projectId && i.JiraIssueId == snap.JiraIssueId, ct)
                        ?? new Issue { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = snap.JiraIssueId };

            issue.JiraKey = GetString(root, "key") ?? issue.JiraKey ?? snap.JiraIssueId;
            issue.Summary = GetString(f, "summary");
            issue.JiraCreatedAt = GetDate(f, "created") ?? issue.JiraCreatedAt;
            issue.JiraUpdatedAt = GetDate(f, "updated") ?? issue.JiraUpdatedAt;
            issue.ParentJiraIssueId = f.TryGetProperty("parent", out var par) && par.ValueKind == JsonValueKind.Object
                ? GetString(par, "id") : null;
            issue.LastSyncedAt = _clock.UtcNow;

            issue.IssueTypeId = await ResolveTypeAsync(f, "issuetype", connectionId, typeCache, ct);
            issue.PriorityId = await ResolvePriorityAsync(f, "priority", connectionId, prioCache, ct);
            issue.StatusId = await ResolveStatusAsync(f, statusField, connectionId, statusCache, ct);
            issue.AssigneeId = await ResolveUserAsync(f, assigneeField, connectionId, userCache, ct);
            issue.SprintId = sprintField is null ? null : await ResolveSprintAsync(f, sprintField, projectId, sprintCache, ct);
            issue.TeamId = teamField is null ? null : await ResolveTeamAsync(f, teamField, projectId, teamCache, ct);
            issue.StoryPoints = pointsField is null ? null : GetDecimal(f, pointsField);

            if (_db.Entry(issue).State == EntityState.Detached) _db.Issues.Add(issue);
        }
        await _db.SaveChangesAsync(ct);

        await DeriveChangeHistoryAsync(projectId, ct);
        await DeriveHierarchyAsync(projectId, ct);
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
