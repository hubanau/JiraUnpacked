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
            .Select(p => new ProjectView(p.Id, p.JiraConnectionId, p.JiraProjectKey, p.JiraProjectId, p.SyncIntervalMinutes, p.SyncPaused, p.CreatedAt))
            .ToListAsync(ct);

    public async Task<ProjectView?> GetAsync(Guid projectId, CancellationToken ct = default)
    {
        var p = await _db.JiraProjects.FirstOrDefaultAsync(x => x.Id == projectId, ct);
        return p is null ? null : ToView(p);
    }

    public async Task<UpdateProjectStatus> UpdateAsync(
        Guid projectId, int? syncIntervalMinutes, bool? syncPaused, CancellationToken ct = default)
    {
        if (syncIntervalMinutes is null && syncPaused is null) return UpdateProjectStatus.NothingToUpdate;
        if (syncIntervalMinutes < MinIntervalMinutes) return UpdateProjectStatus.InvalidInterval;

        var p = await _db.JiraProjects.FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return UpdateProjectStatus.NotFound;

        if (syncIntervalMinutes is { } interval) p.SyncIntervalMinutes = interval;
        if (syncPaused is { } paused) p.SyncPaused = paused;
        await _db.SaveChangesAsync(ct);
        return UpdateProjectStatus.Updated;
    }

    public async Task<DeleteOutcome> DeleteAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Tenant-filtered: another tenant's project id is simply not found.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return DeleteOutcome.NotFound;

        Guid[] ids = [projectId];
        await ProjectDataEraser.LockProjectsAsync(_db, ids, ct);
        if (await ProjectDataEraser.AnyRunningAsync(_db, ids, ct)) return DeleteOutcome.SyncRunning;

        await ProjectDataEraser.DeleteProjectsAsync(_db, ids, ct);
        await tx.CommitAsync(ct);
        return DeleteOutcome.Deleted;
    }

    private static ProjectView ToView(JiraProject p) =>
        new(p.Id, p.JiraConnectionId, p.JiraProjectKey, p.JiraProjectId, p.SyncIntervalMinutes, p.SyncPaused, p.CreatedAt);
}
