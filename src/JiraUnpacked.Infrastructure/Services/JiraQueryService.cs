using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class JiraQueryService : IJiraQueryService
{
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;

    public JiraQueryService(AppDbContext db, IJiraApiClient jira, ITokenProtector protector, IClock clock)
    { _db = db; _jira = jira; _protector = protector; _clock = clock; }

    public async Task<AddQueryResult> AddAsync(Guid projectId, string jql, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return new AddQueryResult(AddQueryStatus.ProjectNotFound, null, Array.Empty<string>());

        var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == project.JiraConnectionId, ct);
        if (conn is null) return new AddQueryResult(AddQueryStatus.ProjectNotFound, null, Array.Empty<string>());

        var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));
        var parse = await _jira.ParseJqlAsync(creds, jql, ct);
        if (!parse.Valid) return new AddQueryResult(AddQueryStatus.InvalidJql, null, parse.Errors);

        var entity = new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = jql, CreatedAt = _clock.UtcNow };
        _db.JiraQueries.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new AddQueryResult(AddQueryStatus.Created,
            new QueryView(entity.Id, entity.JiraProjectId, entity.Jql, entity.CreatedAt), Array.Empty<string>());
    }

    public async Task<IReadOnlyList<QueryView>> ListAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.JiraQueries.Where(q => q.JiraProjectId == projectId).OrderBy(q => q.CreatedAt)
            .Select(q => new QueryView(q.Id, q.JiraProjectId, q.Jql, q.CreatedAt)).ToListAsync(ct);

    public async Task<bool> DeleteAsync(Guid queryId, CancellationToken ct = default)
    {
        var q = await _db.JiraQueries.FirstOrDefaultAsync(x => x.Id == queryId, ct);
        if (q is null) return false;
        _db.JiraQueries.Remove(q);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
