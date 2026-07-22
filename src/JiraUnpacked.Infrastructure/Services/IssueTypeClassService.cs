// src/JiraUnpacked.Infrastructure/Services/IssueTypeClassService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class IssueTypeClassService : IIssueTypeClassService
{
    private readonly AppDbContext _db;
    public IssueTypeClassService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<IssueTypeClassInfo>?> GetAsync(Guid connectionId, CancellationToken ct = default)
    {
        // Tenant-scoped existence check: another tenant's connection is filtered out => null => 404.
        if (!await _db.JiraConnections.AnyAsync(c => c.Id == connectionId, ct)) return null;

        // Materialise then map: the enum is stored via HasConversion<string>, so ToString()
        // is done in memory rather than pushed into SQL.
        var rows = await _db.IssueTypes
            .Where(t => t.JiraConnectionId == connectionId)
            .OrderBy(t => t.Name)
            .Select(t => new { t.JiraIssueTypeId, t.Name, t.Class })
            .ToListAsync(ct);

        return rows.Select(r => new IssueTypeClassInfo(r.JiraIssueTypeId, r.Name, r.Class.ToString())).ToList();
    }

    public async Task<SetIssueTypeClassesResult> SetAsync(
        Guid connectionId, IReadOnlyList<IssueTypeClassInput> items, CancellationToken ct = default)
    {
        if (!await _db.JiraConnections.AnyAsync(c => c.Id == connectionId, ct))
            return new SetIssueTypeClassesResult(SetIssueTypeClassesOutcome.ConnectionNotFound, null);

        var rows = await _db.IssueTypes.Where(t => t.JiraConnectionId == connectionId).ToListAsync(ct);
        var byJiraId = rows.ToDictionary(r => r.JiraIssueTypeId, StringComparer.Ordinal);

        // Validate the whole request before mutating anything: one unknown id rejects the batch.
        foreach (var item in items)
            if (!byJiraId.ContainsKey(item.JiraIssueTypeId))
                return new SetIssueTypeClassesResult(
                    SetIssueTypeClassesOutcome.UnknownIssueTypeId, item.JiraIssueTypeId);

        // Additive: types absent from the request keep whatever class they already have.
        foreach (var item in items) byJiraId[item.JiraIssueTypeId].Class = item.Class;

        await _db.SaveChangesAsync(ct);
        return new SetIssueTypeClassesResult(SetIssueTypeClassesOutcome.Ok, null);
    }
}
