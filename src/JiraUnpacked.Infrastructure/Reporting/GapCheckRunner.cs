// src/JiraUnpacked.Infrastructure/Reporting/GapCheckRunner.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.GapChecks;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// Runs the curated FR-E3 checks. Performs ONE tenant-scoped load per request and hands the
/// resulting context to a pure check, then owns ordering and pagination so a new check never
/// has to reimplement them. Reads current status only — no change history.
/// </summary>
public sealed class GapCheckRunner : IGapCheckService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    private readonly AppDbContext _db;
    private readonly IReadOnlyList<IGapCheck> _checks;

    public GapCheckRunner(AppDbContext db, IEnumerable<IGapCheck> checks)
    {
        _db = db;
        _checks = checks.ToList();
    }

    public async Task<IReadOnlyList<GapCheckInfo>?> GetCatalogAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        return _checks.Select(c => c.Info).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<GapCheckResult?> RunAsync(
        Guid projectId, string checkId, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;

        var check = _checks.FirstOrDefault(
            c => string.Equals(c.Info.Id, checkId, StringComparison.OrdinalIgnoreCase));
        if (check is null) return null;   // unknown checkId => 404

        var context = await LoadContextAsync(projectId, ct);
        var outcome = check.Run(context);

        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var ordered = outcome.Violations.OrderBy(v => v.JiraKey, StringComparer.Ordinal).ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new GapCheckResult(
            check.Info,
            outcome.Status.ToString(),
            new PagedResult<GapCheckViolation>(items, page, pageSize, ordered.Count));
    }

    /// <summary>Tenant-scoped existence check: a cross-tenant/unknown project is filtered out => 404.</summary>
    private Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct) =>
        _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct);

    private async Task<GapCheckContext> LoadContextAsync(Guid projectId, CancellationToken ct)
    {
        // Case-insensitive by contract: StatusTimelineEngine.Classify relies on the caller's comparer.
        var semanticRows = await _db.StatusSemantics
            .Where(s => s.JiraProjectId == projectId)
            .Select(s => new { s.StatusName, s.Class })
            .ToListAsync(ct);

        var semantics = new Dictionary<string, StatusClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in semanticRows) semantics[r.StatusName] = r.Class;

        var rows = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Select(i => new
            {
                i.JiraIssueId,
                i.JiraKey,
                i.Summary,
                i.ParentJiraIssueId,
                Status = _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                IssueType = _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault(),
                IsEpicType = _db.IssueTypes.Any(t => t.Id == i.IssueTypeId && t.Class == IssueTypeClass.Epic),
            })
            .ToListAsync(ct);

        var issues = rows
            .Select(r => new GapCheckIssue(
                r.JiraIssueId, r.JiraKey, r.Summary, r.Status,
                StatusTimelineEngine.Classify(r.Status, semantics),
                r.IssueType, r.IsEpicType, r.ParentJiraIssueId))
            .ToList();

        // Epic classification lives on the connection that owns this project.
        var hasEpicType = await _db.IssueTypes.AnyAsync(
            t => t.Class == IssueTypeClass.Epic
                 && _db.JiraProjects.Any(p => p.Id == projectId && p.JiraConnectionId == t.JiraConnectionId),
            ct);

        return new GapCheckContext(issues, hasEpicType);
    }
}
