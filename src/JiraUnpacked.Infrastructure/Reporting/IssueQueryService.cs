// src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs
using System.Linq.Expressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

public sealed class IssueQueryService : IIssueQueryService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;
    private readonly AppDbContext _db;
    private readonly IIssueFilterCompiler _filter;

    public IssueQueryService(AppDbContext db, IIssueFilterCompiler filter)
    {
        _db = db;
        _filter = filter;
    }

    /// <summary>
    /// The one projection every issue read uses. It was written out three times, and the list
    /// and the filtered query MUST return the same shape — nothing but repetition was keeping
    /// them in step, so a field added to one would silently be missing from the other.
    ///
    /// Instance-level rather than static because the dictionary lookups are correlated
    /// subqueries over _db; EF composes the expression into each query the same way as inline.
    /// </summary>
    private Expression<Func<Issue, IssueListItem>> ToListItem => i => new IssueListItem(
        i.JiraIssueId, i.JiraKey, i.Summary,
        _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
        _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault(),
        _db.Priorities.Where(p => p.Id == i.PriorityId).Select(p => p.Name).FirstOrDefault(),
        _db.JiraUsers.Where(u => u.Id == i.AssigneeId).Select(u => u.DisplayName).FirstOrDefault(),
        _db.Sprints.Where(sp => sp.Id == i.SprintId).Select(sp => sp.Name).FirstOrDefault(),
        _db.Teams.Where(tm => tm.Id == i.TeamId).Select(tm => tm.Name).FirstOrDefault(),
        i.StoryPoints, i.ParentJiraIssueId, i.JiraCreatedAt, i.JiraUpdatedAt);

    /// <summary>
    /// Orders by one catalogued column, then ALWAYS by JiraKey.
    ///
    /// Instance-level rather than a static expression table for the same reason ToListItem is:
    /// the dictionary columns order by a display name that only a correlated subquery over _db
    /// can reach. Ordering by the FK id instead would order by a random GUID — plausible-looking
    /// and wrong in a way one page would never reveal.
    ///
    /// The JiraKey tiebreaker is not tidiness. Sorting by a column with duplicates is not a total
    /// order, and without one Postgres may put a tied row on two pages and another on none.
    /// </summary>
    private IOrderedQueryable<Issue> ApplySort(IQueryable<Issue> q, string? sortBy, bool descending)
    {
        IOrderedQueryable<Issue> By<TKey>(System.Linq.Expressions.Expression<Func<Issue, TKey>> key) =>
            descending ? q.OrderByDescending(key) : q.OrderBy(key);

        var ordered = (sortBy ?? SortFields.Default) switch
        {
            "jiraKey"     => By(i => i.JiraKey),
            "summary"     => By(i => i.Summary),
            "status"      => By(i => _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault()),
            "issueType"   => By(i => _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault()),
            "priority"    => By(i => _db.Priorities.Where(p => p.Id == i.PriorityId).Select(p => p.Name).FirstOrDefault()),
            "assignee"    => By(i => _db.JiraUsers.Where(u => u.Id == i.AssigneeId).Select(u => u.DisplayName).FirstOrDefault()),
            "sprint"      => By(i => _db.Sprints.Where(sp => sp.Id == i.SprintId).Select(sp => sp.Name).FirstOrDefault()),
            "team"        => By(i => _db.Teams.Where(tm => tm.Id == i.TeamId).Select(tm => tm.Name).FirstOrDefault()),
            "storyPoints" => By(i => i.StoryPoints),
            "createdAt"   => By(i => i.JiraCreatedAt),
            _             => By(i => i.JiraUpdatedAt),   // updatedAt, the default
        };

        return ordered.ThenBy(i => i.JiraKey);
    }

    public async Task<PagedResult<IssueListItem>?> ListAsync(Guid projectId, int page, int pageSize, CancellationToken ct = default)
    {
        // Tenant-scoped existence check: a cross-tenant/unknown project is filtered out => null => 404.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return null;

        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var q = _db.Issues.Where(i => i.JiraProjectId == projectId);
        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(i => i.JiraUpdatedAt).ThenBy(i => i.JiraKey)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(ToListItem)
            .ToListAsync(ct);

        return new PagedResult<IssueListItem>(items, page, pageSize, total);
    }

    public async Task<IssueQueryResult> QueryAsync(
        Guid projectId, IssueFilterRequest request, CancellationToken ct = default)
    {
        // Existence check FIRST: unknown/cross-tenant project => 404, whatever the filter is.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct))
            return new IssueQueryResult(null, null);

        // The dict-name resolver, the conditions/filter XOR rule and the guards all live in
        // the shared compiler, so this path, the metrics and the gap-checks cannot diverge.
        var build = await _filter.CompileAsync(new ReportFilter(request.Conditions, request.Filter), ct);
        if (build.Error is not null) return new IssueQueryResult(null, build.Error);

        if (!SortFields.IsKnown(request.SortBy))
            return new IssueQueryResult(null, SortFields.Describe(request.SortBy));

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);

        var q = _db.Issues.Where(i => i.JiraProjectId == projectId).Where(build.Predicate!);
        var total = await q.CountAsync(ct);

        var items = await ApplySort(q, request.SortBy, request.SortDescending)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(ToListItem)
            .ToListAsync(ct);

        return new IssueQueryResult(new PagedResult<IssueListItem>(items, page, pageSize, total), null);
    }

    /// <summary>
    /// An export bigger than this is refused. Holding a connection open while 92,000 rows stream
    /// is a request to wait minutes for a file; narrowing the filter is the honest answer, and
    /// the count is returned so the caller can say by how much.
    /// </summary>
    private const int MaxExportRows = 50_000;

    public async Task<IssueExportInfo> PrepareExportAsync(
        Guid projectId, IssueFilterRequest request, CancellationToken ct = default)
    {
        var key = await _db.JiraProjects
            .Where(p => p.Id == projectId).Select(p => p.JiraProjectKey).FirstOrDefaultAsync(ct);
        if (key is null) return new IssueExportInfo(null, 0, null);

        var build = await _filter.CompileAsync(new ReportFilter(request.Conditions, request.Filter), ct);
        if (build.Error is not null) return new IssueExportInfo(key, 0, build.Error);

        if (!SortFields.IsKnown(request.SortBy))
            return new IssueExportInfo(key, 0, SortFields.Describe(request.SortBy));

        var count = await _db.Issues
            .Where(i => i.JiraProjectId == projectId).Where(build.Predicate!).CountAsync(ct);

        return count > MaxExportRows
            ? new IssueExportInfo(key, count,
                $"This filter matches {count:N0} issues, more than the {MaxExportRows:N0} an export "
                + "can stream. Narrow the filter and try again.")
            : new IssueExportInfo(key, count, null);
    }

    public async IAsyncEnumerable<IssueListItem> StreamExportAsync(
        Guid projectId, IssueFilterRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var build = await _filter.CompileAsync(new ReportFilter(request.Conditions, request.Filter), ct);
        if (build.Error is not null) yield break;   // PrepareExportAsync already reported it

        var q = _db.Issues.Where(i => i.JiraProjectId == projectId).Where(build.Predicate!);

        // AsAsyncEnumerable, never ToListAsync: rows reach the response as they are read.
        await foreach (var item in ApplySort(q, request.SortBy, request.SortDescending)
            .Select(ToListItem).AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return item;
        }
    }

    public async Task<IssueDetail?> GetDetailAsync(Guid projectId, string jiraIssueId, CancellationToken ct = default)
    {
        var issue = await _db.Issues
            .Where(i => i.JiraProjectId == projectId && i.JiraIssueId == jiraIssueId)
            .Select(ToListItem)
            .FirstOrDefaultAsync(ct);
        if (issue is null) return null;

        IssueRef? parent = issue.ParentJiraIssueId is null ? null : await _db.Issues
            .Where(i => i.JiraProjectId == projectId && i.JiraIssueId == issue.ParentJiraIssueId)
            .Select(i => new IssueRef(i.JiraIssueId, i.JiraKey, i.Summary))
            .FirstOrDefaultAsync(ct);

        var children = await _db.Issues
            .Where(i => i.JiraProjectId == projectId && i.ParentJiraIssueId == jiraIssueId)
            .OrderBy(i => i.JiraKey)
            .Select(i => new IssueRef(i.JiraIssueId, i.JiraKey, i.Summary))
            .ToListAsync(ct);

        var history = await _db.IssueChangeHistory
            .Where(h => h.JiraProjectId == projectId && h.JiraIssueId == jiraIssueId)
            .OrderBy(h => h.ChangedAt)
            .Select(h => new ChangeHistoryEntry(h.Field, h.FromValue, h.ToValue, h.Author, h.ChangedAt))
            .ToListAsync(ct);

        return new IssueDetail(issue, parent, children, history);
    }
}
