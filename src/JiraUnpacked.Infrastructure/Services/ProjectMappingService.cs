using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class ProjectMappingService : IProjectMappingService
{
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;

    public ProjectMappingService(AppDbContext db, IJiraApiClient jira, ITokenProtector protector)
    { _db = db; _jira = jira; _protector = protector; }

    public async Task<IReadOnlyList<JiraStatus>?> GetStatusesAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return null;
        var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == project.JiraConnectionId, ct);
        if (conn is null) return null;
        var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));
        return await _jira.GetStatusesAsync(creds, project.JiraProjectKey, ct);
    }

    public async Task<bool?> ReplaceFieldMappingsAsync(Guid projectId, IReadOnlyList<FieldMappingInput> items, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return null;

        var existing = await _db.FieldMappings.Where(m => m.JiraProjectId == projectId).ToListAsync(ct);
        _db.FieldMappings.RemoveRange(existing);
        foreach (var i in items)
            _db.FieldMappings.Add(new FieldMapping { Id = Guid.NewGuid(), JiraProjectId = projectId, Role = i.Role, JiraFieldId = i.JiraFieldId });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<FieldMappingInput>?> GetFieldMappingsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return null;
        return await _db.FieldMappings.Where(m => m.JiraProjectId == projectId)
            .Select(m => new FieldMappingInput(m.Role, m.JiraFieldId)).ToListAsync(ct);
    }

    public async Task<bool?> ReplaceStatusSemanticsAsync(Guid projectId, IReadOnlyList<StatusSemanticInput> items, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return null;

        var existing = await _db.StatusSemantics.Where(s => s.JiraProjectId == projectId).ToListAsync(ct);
        _db.StatusSemantics.RemoveRange(existing);
        foreach (var i in items)
            _db.StatusSemantics.Add(new StatusSemantic { Id = Guid.NewGuid(), JiraProjectId = projectId,
                JiraStatusId = i.JiraStatusId, StatusName = i.StatusName, Class = i.Class });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<StatusSemanticInput>?> GetStatusSemanticsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return null;
        return await _db.StatusSemantics.Where(s => s.JiraProjectId == projectId)
            .Select(s => new StatusSemanticInput(s.JiraStatusId, s.StatusName, s.Class)).ToListAsync(ct);
    }
}
