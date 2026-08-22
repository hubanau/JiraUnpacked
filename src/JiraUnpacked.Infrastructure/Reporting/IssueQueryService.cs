// src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

public sealed class IssueQueryService : IIssueQueryService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;
    private readonly AppDbContext _db;
    private readonly IIssueFilterTranslator _filter;

    public IssueQueryService(AppDbContext db, IIssueFilterTranslator filter)
    {
        _db = db;
        _filter = filter;
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
            .Select(i => new IssueListItem(
                i.JiraIssueId, i.JiraKey, i.Summary,
                _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault(),
                _db.Priorities.Where(p => p.Id == i.PriorityId).Select(p => p.Name).FirstOrDefault(),
                _db.JiraUsers.Where(u => u.Id == i.AssigneeId).Select(u => u.DisplayName).FirstOrDefault(),
                _db.Sprints.Where(sp => sp.Id == i.SprintId).Select(sp => sp.Name).FirstOrDefault(),
                _db.Teams.Where(tm => tm.Id == i.TeamId).Select(tm => tm.Name).FirstOrDefault(),
                i.StoryPoints, i.ParentJiraIssueId, i.JiraCreatedAt, i.JiraUpdatedAt))
            .ToListAsync(ct);

        return new PagedResult<IssueListItem>(items, page, pageSize, total);
    }

    public async Task<IssueQueryResult> QueryAsync(
        Guid projectId, IssueFilterRequest request, CancellationToken ct = default)
    {
        // Existence check FIRST: unknown/cross-tenant project => 404, whatever the filter is.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct))
            return new IssueQueryResult(null, null);

        var conditions = request.Conditions ?? new List<FilterCondition>();

        // Both populated is an error, not a merge: silently AND-ing them would make the request
        // mean something the client did not write.
        if (conditions.Count > 0 && request.Filter is not null)
            return new IssueQueryResult(null, "Send either 'conditions' or 'filter', not both.");

        // Tenant-scoped dict-name resolver: name(s) -> surrogate Ids for the right dictionary.
        DictNameResolver resolve = async (field, names, token) =>
        {
            IReadOnlyList<Guid> ids = field switch
            {
                "status"    => await _db.Statuses.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(token),
                "issueType" => await _db.IssueTypes.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(token),
                "priority"  => await _db.Priorities.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(token),
                "assignee"  => await _db.JiraUsers.Where(x => names.Contains(x.DisplayName)).Select(x => x.Id).ToListAsync(token),
                "sprint"    => await _db.Sprints.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(token),
                "team"      => await _db.Teams.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(token),
                _ => Array.Empty<Guid>(),
            };
            return ids;
        };

        var build = request.Filter is { } group
            ? await _filter.BuildAsync(group, resolve, ct)
            : await _filter.BuildAsync(conditions, resolve, ct);
        if (build.Error is not null) return new IssueQueryResult(null, build.Error);

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);

        var q = _db.Issues.Where(i => i.JiraProjectId == projectId).Where(build.Predicate!);
        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(i => i.JiraUpdatedAt).ThenBy(i => i.JiraKey)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new IssueListItem(
                i.JiraIssueId, i.JiraKey, i.Summary,
                _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault(),
                _db.Priorities.Where(p2 => p2.Id == i.PriorityId).Select(p2 => p2.Name).FirstOrDefault(),
                _db.JiraUsers.Where(u => u.Id == i.AssigneeId).Select(u => u.DisplayName).FirstOrDefault(),
                _db.Sprints.Where(sp => sp.Id == i.SprintId).Select(sp => sp.Name).FirstOrDefault(),
                _db.Teams.Where(tm => tm.Id == i.TeamId).Select(tm => tm.Name).FirstOrDefault(),
                i.StoryPoints, i.ParentJiraIssueId, i.JiraCreatedAt, i.JiraUpdatedAt))
            .ToListAsync(ct);

        return new IssueQueryResult(new PagedResult<IssueListItem>(items, page, pageSize, total), null);
    }

    public async Task<IssueDetail?> GetDetailAsync(Guid projectId, string jiraIssueId, CancellationToken ct = default)
    {
        var issue = await _db.Issues
            .Where(i => i.JiraProjectId == projectId && i.JiraIssueId == jiraIssueId)
            .Select(i => new IssueListItem(
                i.JiraIssueId, i.JiraKey, i.Summary,
                _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault(),
                _db.Priorities.Where(p => p.Id == i.PriorityId).Select(p => p.Name).FirstOrDefault(),
                _db.JiraUsers.Where(u => u.Id == i.AssigneeId).Select(u => u.DisplayName).FirstOrDefault(),
                _db.Sprints.Where(sp => sp.Id == i.SprintId).Select(sp => sp.Name).FirstOrDefault(),
                _db.Teams.Where(tm => tm.Id == i.TeamId).Select(tm => tm.Name).FirstOrDefault(),
                i.StoryPoints, i.ParentJiraIssueId, i.JiraCreatedAt, i.JiraUpdatedAt))
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
