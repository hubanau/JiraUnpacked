// src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

public sealed class IssueQueryService : IIssueQueryService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;
    private readonly AppDbContext _db;

    public IssueQueryService(AppDbContext db) => _db = db;

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

    public Task<IssueDetail?> GetDetailAsync(Guid projectId, string jiraIssueId, CancellationToken ct = default) =>
        throw new NotImplementedException("RV2");
}
