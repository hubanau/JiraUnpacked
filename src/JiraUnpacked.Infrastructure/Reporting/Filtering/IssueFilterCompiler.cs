using System.Linq.Expressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>Null Predicate + non-null Error => a 400. Otherwise Predicate is always non-null.</summary>
public sealed record CompiledFilter(Expression<Func<Issue, bool>>? Predicate, string? Error);

/// <summary>
/// Turns a request's filter into a predicate, resolving dictionary display names to surrogate
/// Ids through the injected tenant-scoped AppDbContext.
///
/// This exists so the issue query, the metrics and the gap-checks share ONE implementation of
/// the resolver rather than three copies that drift. It is also the single place where the
/// "conditions XOR filter" rule is enforced, so no caller can accidentally accept both.
/// </summary>
public interface IIssueFilterCompiler
{
    Task<CompiledFilter> CompileAsync(ReportFilter? filter, CancellationToken ct = default);
}

public sealed class IssueFilterCompiler : IIssueFilterCompiler
{
    private static readonly Expression<Func<Issue, bool>> MatchAll = i => true;

    private readonly AppDbContext _db;
    private readonly IIssueFilterTranslator _translator;

    public IssueFilterCompiler(AppDbContext db, IIssueFilterTranslator translator)
    {
        _db = db;
        _translator = translator;
    }

    public async Task<CompiledFilter> CompileAsync(ReportFilter? filter, CancellationToken ct = default)
    {
        var conditions = filter?.Conditions ?? new List<FilterCondition>();
        var group = filter?.Filter;

        // Both populated is an error, not a merge: silently AND-ing them would make the request
        // mean something the caller did not write.
        if (conditions.Count > 0 && group is not null)
            return new CompiledFilter(null, "Send either 'conditions' or 'filter', not both.");

        if (conditions.Count == 0 && group is null)
            return new CompiledFilter(MatchAll, null);

        var build = group is not null
            ? await _translator.BuildAsync(group, ResolveDictAsync, ct)
            : await _translator.BuildAsync(conditions, ResolveDictAsync, ct);

        return build.Error is not null
            ? new CompiledFilter(null, build.Error)
            : new CompiledFilter(build.Predicate, null);
    }

    /// <summary>
    /// Tenant-scoped dict-name resolution: display name(s) -> surrogate Ids for the right
    /// dictionary. Reads go through the injected AppDbContext, so a name that exists only in
    /// another tenant's connection resolves to no Id and therefore matches nothing.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveDictAsync(
        string field, IReadOnlyList<string> names, CancellationToken ct)
    {
        IReadOnlyList<Guid> ids = field switch
        {
            "status"    => await _db.Statuses.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(ct),
            "issueType" => await _db.IssueTypes.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(ct),
            "priority"  => await _db.Priorities.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(ct),
            "assignee"  => await _db.JiraUsers.Where(x => names.Contains(x.DisplayName)).Select(x => x.Id).ToListAsync(ct),
            "sprint"    => await _db.Sprints.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(ct),
            "team"      => await _db.Teams.Where(x => names.Contains(x.Name)).Select(x => x.Id).ToListAsync(ct),
            _ => Array.Empty<Guid>(),
        };
        return ids;
    }
}
