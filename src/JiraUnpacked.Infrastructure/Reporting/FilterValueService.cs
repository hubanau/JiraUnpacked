using JiraUnpacked.Domain.Abstractions;
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
            return new FilterValuesResult(null, null);

        var issues = _db.Issues.Where(i => i.JiraProjectId == projectId);

        // This switch over literal names IS the closed catalog: no caller-supplied string ever
        // reaches a query. Each arm selects the dictionary rows some issue in this project points
        // at, which is both the correct answer and an indexed lookup on the FK.
        IQueryable<string>? names = field switch
        {
            "status"    => _db.Statuses.Where(s => issues.Any(i => i.StatusId == s.Id)).Select(s => s.Name),
            "issueType" => _db.IssueTypes.Where(t => issues.Any(i => i.IssueTypeId == t.Id)).Select(t => t.Name),
            "priority"  => _db.Priorities.Where(p => issues.Any(i => i.PriorityId == p.Id)).Select(p => p.Name),
            "assignee"  => _db.JiraUsers.Where(u => issues.Any(i => i.AssigneeId == u.Id)).Select(u => u.DisplayName),
            "sprint"    => _db.Sprints.Where(sp => issues.Any(i => i.SprintId == sp.Id)).Select(sp => sp.Name),
            "team"      => _db.Teams.Where(tm => issues.Any(i => i.TeamId == tm.Id)).Select(tm => tm.Name),
            _           => null,
        };

        if (names is null)
            return new FilterValuesResult(
                null,
                $"'{field}' has no value list. Only dictionary fields can be picked from: "
                + "status, issueType, priority, assignee, sprint, team.");

        var values = await names.Distinct().OrderBy(n => n).Take(MaxValues).ToListAsync(ct);
        return new FilterValuesResult(values, null);
    }
}
