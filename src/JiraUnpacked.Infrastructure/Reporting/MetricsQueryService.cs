// src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// FR-E2 curated metrics, computed on the fly from stored data — no Jira calls. Every read goes
/// through the injected tenant-scoped AppDbContext (EF query filter + Postgres RLS).
/// </summary>
public sealed class MetricsQueryService : IMetricsQueryService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public MetricsQueryService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Tenant-scoped existence check: a cross-tenant/unknown project is filtered out => 404.</summary>
    private Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct) =>
        _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct);

    /// <summary>The project's status-name -> class map. Case-insensitive, per the spec.</summary>
    private async Task<Dictionary<string, StatusClass>> LoadSemanticsAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await _db.StatusSemantics
            .Where(s => s.JiraProjectId == projectId)
            .Select(s => new { s.StatusName, s.Class })
            .ToListAsync(ct);

        var map = new Dictionary<string, StatusClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) map[r.StatusName] = r.Class;
        return map;
    }

    public async Task<StoryCountsReport?> GetStoryCountsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        var semantics = await LoadSemanticsAsync(projectId, ct);

        // Current status comes straight off Issue.StatusId — this metric never loads change history.
        var rows = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Select(i => new
            {
                Status = _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                i.StoryPoints
            })
            .ToListAsync(ct);

        var byStatus = rows
            .GroupBy(r => r.Status)
            .Select(g => new StatusCount(
                g.Key,
                StatusTimelineEngine.Classify(g.Key, semantics).ToString(),
                g.Count(),
                g.Sum(x => x.StoryPoints ?? 0m)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Status)
            .ToList();

        return new StoryCountsReport(byStatus, rows.Count, rows.Sum(r => r.StoryPoints ?? 0m));
    }

    public Task<TimeInProgressReport?> GetTimeInProgressAsync(Guid projectId, CancellationToken ct = default) =>
        throw new NotImplementedException("MT3");

    public Task<ThroughputReport?> GetThroughputAsync(
        Guid projectId, ThroughputBucketSize bucket, DateTime? from, DateTime? to, CancellationToken ct = default) =>
        throw new NotImplementedException("MT3");

    public Task<StatusAgingReport?> GetStatusAgingAsync(
        Guid projectId, int page, int pageSize, CancellationToken ct = default) =>
        throw new NotImplementedException("MT4");
}
