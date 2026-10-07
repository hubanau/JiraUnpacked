using System.Linq.Expressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

public sealed record FilterBuildResult(Expression<Func<Issue, bool>>? Predicate, string? Error);

/// <summary>
/// Resolves display names to surrogate Ids (tenant-scoped, injected by the service). `field` is a
/// dict field's name — or one of the closed <see cref="FilterLookups"/> keys, for lookups that
/// are not by display name. Nothing a caller sends ever becomes a lookup key.
/// </summary>
public delegate Task<IReadOnlyList<Guid>> DictNameResolver(
    string field, IReadOnlyList<string> names, CancellationToken ct);

/// <summary>
/// Lookups the translator asks the resolver for besides display names. A closed set: the
/// compiler's resolver has an arm for each, over the same tenant-scoped context as the names.
/// </summary>
public static class FilterLookups
{
    /// <summary>names = sprint states ("active", "future", "closed") → ids of the sprints in them.</summary>
    public const string SprintState = "sprint.state";

    /// <summary>names = status category names → ids of the statuses in those categories.</summary>
    /// <remarks>Dotted like the others, so it cannot be mistaken for the 'statusCategory' field.</remarks>
    public const string StatusCategory = "status.category";

    /// <summary>names ignored → ids of the statuses with no category (null, empty or whitespace).</summary>
    public const string UncategorisedStatus = "status.category.none";
}

public interface IIssueFilterTranslator
{
    /// <summary>Flat AND over a condition list — the original FR-E4 shape. Empty = unfiltered.</summary>
    /// <param name="history">
    /// The tenant-scoped changelog the history conditions query (the compiler passes
    /// <c>_db.IssueChangeHistory</c>). Last and optional so every existing positional call keeps
    /// compiling; without it a history condition is refused, never silently dropped.
    /// </param>
    Task<FilterBuildResult> BuildAsync(
        IReadOnlyList<FilterCondition> conditions, DictNameResolver resolveDict, CancellationToken ct = default,
        IQueryable<IssueChangeHistory>? history = null);

    /// <summary>Boolean tree. Every group must be non-empty; the guards are counted over the whole tree.</summary>
    /// <param name="history">As for the flat overload.</param>
    Task<FilterBuildResult> BuildAsync(
        FilterGroup group, DictNameResolver resolveDict, CancellationToken ct = default,
        IQueryable<IssueChangeHistory>? history = null);
}

/// <summary>
/// Turns a request's filter into a predicate over Issue. Split by concern across partial files:
/// this one walks the tree, enforces the guards and dispatches by field type; .Dictionary,
/// .Dates, .Scalars and .History build each type's conditions; .Expressions holds the shared helpers.
/// </summary>
public sealed partial class IssueFilterTranslator : IIssueFilterTranslator
{
    public const int MaxConditions = 20;
    public const int MaxInValues = 100;
    public const int MaxGroups = 10;
    public const int MaxDepth = 3;

    /// <summary>Running totals for the guards. Counted over the WHOLE tree: a per-node limit is
    /// defeated by adding nodes, which is the shape a hostile request would take.</summary>
    private sealed class Budget { public int Conditions; public int Groups; public int History; }

    public async Task<FilterBuildResult> BuildAsync(
        IReadOnlyList<FilterCondition> conditions, DictNameResolver resolveDict, CancellationToken ct = default,
        IQueryable<IssueChangeHistory>? history = null)
    {
        // Preserved verbatim from FR-E4: an omitted or empty 'conditions' has always meant
        // "no filter", never "an empty group". Only a group a caller actually wrote is rejected
        // for being empty (see BuildGroupAsync).
        if (conditions.Count == 0) return new FilterBuildResult(MatchAll, null);

        return await BuildAsync(
            new FilterGroup(FilterLogic.And, conditions.ToList(), null), resolveDict, ct, history);
    }

    public async Task<FilterBuildResult> BuildAsync(
        FilterGroup group, DictNameResolver resolveDict, CancellationToken ct = default,
        IQueryable<IssueChangeHistory>? history = null)
    {
        // A root switched off — or one whose every part is switched off — is "no filter": the
        // user turned the parts off on purpose. A group WRITTEN empty is still refused below.
        if (group.Disabled == true) return new FilterBuildResult(MatchAll, null);

        var (predicate, error) = await BuildGroupAsync(group, resolveDict, history, new Budget(), 1, ct);
        return error is not null ? Err(error) : new FilterBuildResult(predicate ?? MatchAll, null);
    }

    /// <summary>
    /// A null Predicate with a null Error means every part of this group is switched off: the
    /// group is absent, and its parent joins only its other parts.
    /// </summary>
    private async Task<(Expression<Func<Issue, bool>>? Predicate, string? Error)> BuildGroupAsync(
        FilterGroup group, DictNameResolver resolveDict, IQueryable<IssueChangeHistory>? history,
        Budget budget, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
            return (null, $"A filter may nest at most {MaxDepth} levels deep.");
        if (++budget.Groups > MaxGroups)
            return (null, $"A filter may contain at most {MaxGroups} groups.");

        var conditions = group.Conditions ?? new List<FilterCondition>();
        var groups = group.Groups ?? new List<FilterGroup>();

        // An empty And is vacuously true and an empty Or is vacuously false. Same client bug,
        // opposite blast radius, and neither is what the caller meant — so refuse both. Checked
        // on what was WRITTEN, before disabled parts are set aside.
        if (conditions.Count == 0 && groups.Count == 0)
            return (null, "A filter group must contain at least one condition or nested group.");

        var parts = new List<Expression<Func<Issue, bool>>>();

        // Disabled parts are set aside before anything looks at them: not validated (a row
        // switched off because it is broken must not break the filter) and not counted.
        foreach (var c in conditions.Where(c => c.Disabled != true))
        {
            if (++budget.Conditions > MaxConditions)
                return (null, $"A filter may have at most {MaxConditions} conditions.");
            // Each history condition is a correlated subquery over the largest table: capped on its own too.
            if (IsHistory(c) && ++budget.History > MaxHistoryConditions)
                return (null, $"A filter may have at most {MaxHistoryConditions} history conditions.");

            var (predicate, error) = await BuildConditionAsync(c, resolveDict, history, ct);
            if (error is not null) return (null, error);
            parts.Add(predicate!);
        }

        foreach (var g in groups.Where(g => g.Disabled != true))
        {
            var (predicate, error) = await BuildGroupAsync(g, resolveDict, history, budget, depth + 1, ct);
            if (error is not null) return (null, error);
            if (predicate is not null) parts.Add(predicate);
        }

        if (parts.Count == 0) return (null, null);

        // Seedless Aggregate on purpose: seeding with `i => true` is harmless under AndAlso but
        // would make every Or group match everything. parts is non-empty by the check above.
        Func<Expression<Func<Issue, bool>>, Expression<Func<Issue, bool>>, Expression<Func<Issue, bool>>> combine =
            group.Logic == FilterLogic.Or ? OrElse : AndAlso;
        return (parts.Aggregate(combine), null);
    }

    private async Task<(Expression<Func<Issue, bool>>?, string?)> BuildConditionAsync(
        FilterCondition c, DictNameResolver resolveDict, IQueryable<IssueChangeHistory>? history, CancellationToken ct)
    {
        if (!FilterFields.All.TryGetValue(c.Field, out var field))
            return (null, $"Unknown field '{c.Field}'.");
        if (!field.Ops.Contains(c.Op))
            return (null, $"Operator '{c.Op}' is not allowed for field '{c.Field}'.");

        return field.Type switch
        {
            FieldType.DictName  => await BuildDictAsync(field, c, resolveDict, ct),
            FieldType.StatusCategory => await BuildStatusCategoryAsync(field, c, resolveDict, ct),
            FieldType.Decimal   => BuildDecimal(field, c),
            FieldType.DateTime  => BuildDate(field, c),
            FieldType.Text      => BuildText(field, c),
            FieldType.ParentRef => BuildParent(field, c),
            FieldType.History   => BuildHistory(c, history),
            _ => (null, "Unsupported field type."),
        };
    }

    private static readonly Expression<Func<Issue, bool>> MatchAll = i => true;

    private static FilterBuildResult Err(string m) => new(null, m);
}
