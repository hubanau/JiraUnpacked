using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// The selectable values for one dictionary field, drawn from the issues of THIS project.
///
/// Deliberately not the dictionary table wholesale. A Jira site's status list is site-wide, so
/// offering a status no issue here carries produces a filter that returns nothing for a reason
/// the reader cannot see — indistinguishable, on screen, from having mistyped it.
///
/// Reads go through the tenant-scoped context, and every dictionary table carries its own tenant
/// query filter, so another tenant's values are unreachable rather than merely filtered out.
/// </summary>
public sealed class FilterValueService : IFilterValueService
{
    /// <summary>
    /// Bounds a pathological dictionary. A field with more distinct values than this is not
    /// usefully a dropdown, and the picker degrades to the text box it replaced.
    /// </summary>
    private const int MaxValues = 1000;

    private readonly AppDbContext _db;
    public FilterValueService(AppDbContext db) => _db = db;

    public async Task<FilterValuesResult> GetAsync(Guid projectId, string field, CancellationToken ct = default)
    {
        // Tenant-scoped existence check FIRST: an unknown or cross-tenant project is a 404
        // whatever the field, so a caller cannot probe field names against someone else's project.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct))
            return new FilterValuesResult(null, 0, null);

        var issues = _db.Issues.Where(i => i.JiraProjectId == projectId);

        // This switch over literal names IS the closed catalog: no caller-supplied string ever
        // reaches a query. Each arm pairs one name per issue that points at a dictionary row (an
        // inner join on the FK) with the issues that point at nothing.
        (IQueryable<string> Names, IQueryable<Issue> Empty)? source = field switch
        {
            "status"    => (issues.Join(_db.Statuses,   i => i.StatusId,    d => (Guid?)d.Id, (_, d) => d.Name),        issues.Where(i => i.StatusId == null)),
            "issueType" => (issues.Join(_db.IssueTypes, i => i.IssueTypeId, d => (Guid?)d.Id, (_, d) => d.Name),        issues.Where(i => i.IssueTypeId == null)),
            "priority"  => (issues.Join(_db.Priorities, i => i.PriorityId,  d => (Guid?)d.Id, (_, d) => d.Name),        issues.Where(i => i.PriorityId == null)),
            "assignee"  => (issues.Join(_db.JiraUsers,  i => i.AssigneeId,  d => (Guid?)d.Id, (_, d) => d.DisplayName), issues.Where(i => i.AssigneeId == null)),
            "sprint"    => (issues.Join(_db.Sprints,    i => i.SprintId,    d => (Guid?)d.Id, (_, d) => d.Name),        issues.Where(i => i.SprintId == null)),
            "team"      => (issues.Join(_db.Teams,      i => i.TeamId,      d => (Guid?)d.Id, (_, d) => d.Name),        issues.Where(i => i.TeamId == null)),
            _           => null,
        };

        if (source is null)
            return new FilterValuesResult(
                null, 0,
                $"'{field}' has no value list. Only dictionary fields can be picked from: "
                + "status, issueType, priority, assignee, sprint, team.");

        var (names, empty) = source.Value;

        // Grouped by NAME, not by row: the filter resolves names, so two dictionary rows that
        // share a name are one choice, and its count is their sum.
        var rows = await names
            .GroupBy(n => n)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderBy(x => x.Name)
            .Take(MaxValues)
            .ToListAsync(ct);
        var emptyCount = await empty.CountAsync(ct);

        return new FilterValuesResult(
            rows.Select(r => new FilterValueCount(r.Name, r.Count)).ToList(), emptyCount, null);
    }
}
