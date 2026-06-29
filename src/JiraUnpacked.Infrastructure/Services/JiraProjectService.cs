using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class JiraProjectService : IJiraProjectService
{
    private const int MinIntervalMinutes = 60;
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;

    public JiraProjectService(AppDbContext db, IJiraApiClient jira, ITokenProtector protector, IClock clock)
    { _db = db; _jira = jira; _protector = protector; _clock = clock; }

    public async Task<AddProjectResult> AddAsync(Guid connectionId, string projectKey, int syncIntervalMinutes, CancellationToken ct = default)
    {
        if (syncIntervalMinutes < MinIntervalMinutes)
            return new AddProjectResult(AddProjectStatus.InvalidInterval, null);

        var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (conn is null) return new AddProjectResult(AddProjectStatus.ConnectionNotFound, null);

        var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));
        var jiraProject = await _jira.GetProjectAsync(creds, projectKey, ct);
        if (jiraProject is null) return new AddProjectResult(AddProjectStatus.UnknownProjectKey, null);

        var entity = new JiraProject
        {
            Id = Guid.NewGuid(),
            JiraConnectionId = connectionId,
            JiraProjectKey = jiraProject.Key,
            JiraProjectId = jiraProject.Id,
            SyncIntervalMinutes = syncIntervalMinutes,
            CreatedAt = _clock.UtcNow
        };
        _db.JiraProjects.Add(entity);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        { return new AddProjectResult(AddProjectStatus.Duplicate, null); }

        return new AddProjectResult(AddProjectStatus.Created, ToView(entity));
    }

    public async Task<IReadOnlyList<ProjectView>> ListAsync(Guid connectionId, CancellationToken ct = default) =>
        await _db.JiraProjects.Where(p => p.JiraConnectionId == connectionId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new ProjectView(p.Id, p.JiraConnectionId, p.JiraProjectKey, p.JiraProjectId, p.SyncIntervalMinutes, p.CreatedAt))
            .ToListAsync(ct);

    public async Task<ProjectView?> GetAsync(Guid projectId, CancellationToken ct = default)
    {
        var p = await _db.JiraProjects.FirstOrDefaultAsync(x => x.Id == projectId, ct);
        return p is null ? null : ToView(p);
    }

    public async Task<bool?> UpdateIntervalAsync(Guid projectId, int syncIntervalMinutes, CancellationToken ct = default)
    {
        if (syncIntervalMinutes < MinIntervalMinutes) return false;
        var p = await _db.JiraProjects.FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return null;
        p.SyncIntervalMinutes = syncIntervalMinutes;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static ProjectView ToView(JiraProject p) =>
        new(p.Id, p.JiraConnectionId, p.JiraProjectKey, p.JiraProjectId, p.SyncIntervalMinutes, p.CreatedAt);
}
