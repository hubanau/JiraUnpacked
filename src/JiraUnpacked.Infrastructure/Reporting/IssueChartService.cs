using System.Globalization;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// The issue list's chart: the issues the list's filter matches, grouped by one field.
///
/// The filter goes through the same compiler as the list, so the chart's total IS the list's
/// count — a chart that disagrees with the number above the table would be believed over it.
/// Grouping is by NAME, like the value picker (FilterValueService): two dictionary rows that share
/// a name are one bar. One grouped query per chart; the groups (one per distinct value) are then
/// ordered and trimmed in memory.
/// </summary>
public sealed class IssueChartService : IIssueChartService
{
    /// <summary>The fields a chart can group by. The switch in GroupAsync is the real allow-list; this is its error message.</summary>
    private const string Allowed =
        "status, statusCategory, issueType, priority, assignee, sprint, team, storyPoints, createdAt, updatedAt";

    private readonly AppDbContext _db;
    private readonly IIssueFilterCompiler _filter;

    public IssueChartService(AppDbContext db, IIssueFilterCompiler filter)
    {
        _db = db;
        _filter = filter;
    }

    /// <summary>One issue's value for the grouped field and its points. A class, not a record: EF groups by a member-initialised projection.</summary>
    private sealed class KeyRow
    {
        public string? Key { get; init; }
        public decimal? Points { get; init; }
    }

    public async Task<IssueChartResult> BuildAsync(
        Guid projectId, IssueChartRequest request, CancellationToken ct = default)
    {
        // Existence check FIRST: unknown/cross-tenant project => 404, whatever else is wrong.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct))
            return new IssueChartResult(null, null);

        var groupBy = request.GroupBy ?? "";
        if (!IsKnown(groupBy))
            return new IssueChartResult(null, $"'{groupBy}' cannot be charted. Group by one of: {Allowed}.");

        var build = await _filter.CompileAsync(new ReportFilter(request.Conditions, request.Filter), ct);
        if (build.Error is not null) return new IssueChartResult(null, build.Error);

        var issues = _db.Issues.Where(i => i.JiraProjectId == projectId).Where(build.Predicate!);

        var groups = await GroupAsync(issues, groupBy, ct);
        var withoutPoints = await issues.CountAsync(i => i.StoryPoints == null, ct);

        var keyOrdered = IsKeyOrdered(groupBy);
        var ordered = keyOrdered
            ? groups.OrderBy(g => g.Key is null).ThenBy(g => SortValue(groupBy, g.Key)).ToList()
            : groups.OrderByDescending(g => g.Issues)
                .ThenBy(g => g.Key is null).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();

        var (kept, other) = ChartGroupTrimmer.Trim(ordered, keyOrdered);

        return new IssueChartResult(new IssueChart(
            groupBy, ordered.Sum(g => g.Issues), ordered.Sum(g => g.StoryPoints), withoutPoints,
            kept, other is not null, other), null);
    }

    private static bool IsKnown(string groupBy) => groupBy is
        "status" or "statusCategory" or "issueType" or "priority" or "assignee" or "sprint" or "team"
        or "storyPoints" or "createdAt" or "updatedAt";

    /// <summary>Fields whose values are a scale (a number, a month): shown in key order, never ranked.</summary>
    private static bool IsKeyOrdered(string groupBy) => groupBy is "storyPoints" or "createdAt" or "updatedAt";

    /// <summary>
    /// Keys of a key-ordered field, comparable: "yyyy-MM" sorts as text; Points must sort as
    /// numbers ("10" after "8"). (Empty) is already placed last by the caller; it only needs a value.
    /// </summary>
    private static decimal SortValue(string groupBy, string? key) => key is null ? decimal.MaxValue
        : groupBy == "storyPoints" ? decimal.Parse(key, CultureInfo.InvariantCulture)
        : int.Parse(key.Replace("-", ""), CultureInfo.InvariantCulture);   // 2026-09 -> 202609

    private async Task<List<ChartGroup>> GroupAsync(IQueryable<Domain.Entities.Issue> issues, string groupBy, CancellationToken ct)
    {
        // Literal arms only: no caller-supplied string reaches a query. Each dictionary arm is a
        // LEFT join, so an issue pointing at nothing is a row with a null key — the (Empty) group.
        IQueryable<KeyRow>? byName = groupBy switch
        {
            "status" =>
                from i in issues
                join d in _db.Statuses on i.StatusId equals d.Id into ds
                from d in ds.DefaultIfEmpty()
                select new KeyRow { Key = d.Name, Points = i.StoryPoints },
            // Through the status; a blank category is none, as in the filter and the list's column.
            "statusCategory" =>
                from i in issues
                join d in _db.Statuses on i.StatusId equals d.Id into ds
                from d in ds.DefaultIfEmpty()
                select new KeyRow
                {
                    Key = d == null || string.IsNullOrWhiteSpace(d.StatusCategory) ? null : d.StatusCategory,
                    Points = i.StoryPoints,
                },
            "issueType" =>
                from i in issues
                join d in _db.IssueTypes on i.IssueTypeId equals d.Id into ds
                from d in ds.DefaultIfEmpty()
                select new KeyRow { Key = d.Name, Points = i.StoryPoints },
            "priority" =>
                from i in issues
                join d in _db.Priorities on i.PriorityId equals d.Id into ds
                from d in ds.DefaultIfEmpty()
                select new KeyRow { Key = d.Name, Points = i.StoryPoints },
            "assignee" =>
                from i in issues
                join d in _db.JiraUsers on i.AssigneeId equals d.Id into ds
                from d in ds.DefaultIfEmpty()
                select new KeyRow { Key = d.DisplayName, Points = i.StoryPoints },
            "sprint" =>
                from i in issues
                join d in _db.Sprints on i.SprintId equals d.Id into ds
                from d in ds.DefaultIfEmpty()
                select new KeyRow { Key = d.Name, Points = i.StoryPoints },
            "team" =>
                from i in issues
                join d in _db.Teams on i.TeamId equals d.Id into ds
                from d in ds.DefaultIfEmpty()
                select new KeyRow { Key = d.Name, Points = i.StoryPoints },
            _ => null,
        };

        if (byName is not null)
        {
            var rows = await byName
                .GroupBy(r => r.Key)
                .Select(g => new { g.Key, Issues = g.Count(), Points = g.Sum(r => r.Points) ?? 0m })
                .ToListAsync(ct);
            return rows.Select(r => new ChartGroup(r.Key, r.Issues, r.Points)).ToList();
        }

        if (groupBy == "storyPoints")
        {
            var rows = await issues
                .GroupBy(i => i.StoryPoints)
                .Select(g => new { g.Key, Issues = g.Count(), Points = g.Sum(i => i.StoryPoints) ?? 0m })
                .ToListAsync(ct);
            // Invariant and without trailing zeros: the key goes back into a filter as a number.
            return rows.Select(r => new ChartGroup(
                r.Key?.ToString("0.############################", CultureInfo.InvariantCulture),
                r.Issues, r.Points)).ToList();
        }

        // Months. The columns are UTC instants (timestamptz); Npgsql reads .Year/.Month of a UTC
        // DateTime in UTC, whatever the session's zone — so a bucket is a UTC calendar month,
        // exactly what a click's date-only Between filter (read as UTC) selects.
        var months = groupBy == "createdAt"
            ? issues.Select(i => new { i.JiraCreatedAt.Year, i.JiraCreatedAt.Month, i.StoryPoints })
            : issues.Select(i => new { i.JiraUpdatedAt.Year, i.JiraUpdatedAt.Month, i.StoryPoints });
        var monthRows = await months
            .GroupBy(m => new { m.Year, m.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Issues = g.Count(), Points = g.Sum(m => m.StoryPoints) ?? 0m })
            .ToListAsync(ct);
        return monthRows
            .Select(r => new ChartGroup($"{r.Year:D4}-{r.Month:D2}", r.Issues, r.Points))
            .ToList();
    }
}
