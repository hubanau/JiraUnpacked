// src/JiraUnpacked.Infrastructure/Reporting/GapCheckRunner.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
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
    private readonly IIssueFilterCompiler _filter;

    public GapCheckRunner(AppDbContext db, IEnumerable<IGapCheck> checks, IIssueFilterCompiler filter)
    {
        _db = db;
        _checks = checks.ToList();
        _filter = filter;
    }

    public async Task<IReadOnlyList<GapCheckInfo>?> GetCatalogAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        return _checks.Select(c => c.Info).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<ReportResult<GapCheckResult>> RunAsync(
        Guid projectId, string checkId, int page, int pageSize,
        ReportFilter? filter = null, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return ReportResult<GapCheckResult>.NotFound();

        var check = _checks.FirstOrDefault(
            c => string.Equals(c.Info.Id, checkId, StringComparison.OrdinalIgnoreCase));
        if (check is null) return ReportResult<GapCheckResult>.NotFound();   // unknown checkId => 404

        var compiled = await _filter.CompileAsync(filter, ct);
        if (compiled.Error is not null) return ReportResult<GapCheckResult>.Invalid(compiled.Error);

        // The context is ALWAYS built from the whole project. The filter selects which issues
        // are examined; it never changes the evidence used to judge them. Filtering the context
        // instead would let "assignee = Alice" hide Bob's open children and report a broken
        // epic as clean — a green result to a question nobody asked.
        var context = await LoadContextAsync(projectId, ct);
        var outcome = check.Run(context);

        // Applied AFTER the check has run, and only to the violation SUBJECTS. Null = unfiltered.
        var subjects = await LoadSubjectIdsAsync(projectId, filter, compiled.Predicate!, ct);
        var violations = subjects is null
            ? outcome.Violations
            : outcome.Violations.Where(v => subjects.Contains(v.JiraIssueId)).ToList();

        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var ordered = violations.OrderBy(v => v.JiraKey, StringComparer.Ordinal).ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // Status is untouched by filtering: a check that could not run still could not run, and
        // NotConfigured must never be softened into "nothing found here".
        return ReportResult<GapCheckResult>.Ok(new GapCheckResult(
            check.Info,
            outcome.Status.ToString(),
            new PagedResult<GapCheckViolation>(items, page, pageSize, ordered.Count)));
    }

    /// <summary>The Jira issue ids the filter selects, or null when there is no filter.</summary>
    private async Task<HashSet<string>?> LoadSubjectIdsAsync(
        Guid projectId, ReportFilter? filter,
        System.Linq.Expressions.Expression<Func<Domain.Entities.Issue, bool>> population,
        CancellationToken ct)
    {
        var hasFilter = filter is not null
            && ((filter.Conditions is { Count: > 0 }) || filter.Filter is not null);
        if (!hasFilter) return null;

        var ids = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Where(population)
            .Select(i => i.JiraIssueId)
            .ToListAsync(ct);

        return ids.ToHashSet(StringComparer.Ordinal);
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
