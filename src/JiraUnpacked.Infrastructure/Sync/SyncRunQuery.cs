using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed class SyncRunQuery : ISyncRunQuery
{
    private readonly AppDbContext _db;
    public SyncRunQuery(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<SyncRunView>> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        // Materialize the tenant-scoped rows first, then map (enum.ToString() runs in memory,
        // not in the SQL projection, so there is no LINQ-translation risk).
        var runs = await _db.SyncRuns
            .Where(r => r.JiraProjectId == projectId)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .ToListAsync(ct);

        return runs
            .Select(r => new SyncRunView(r.Id, r.JiraProjectId, r.Status.ToString(),
                r.StartedAt, r.FinishedAt, r.IssuesProcessed, r.Error, r.NextRunAt))
            .ToList();
    }
}
