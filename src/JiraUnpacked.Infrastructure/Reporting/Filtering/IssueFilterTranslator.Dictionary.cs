using System.Linq.Expressions;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>Dictionary fields, smart sprint values and status category: names and states resolved to ids.</summary>
public sealed partial class IssueFilterTranslator
{
    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildDictAsync(
        FilterField field, FilterCondition c, DictNameResolver resolveDict, CancellationToken ct)
    {
        var fk = (Expression<Func<Issue, Guid?>>)field.Selector;
        var p = fk.Parameters[0];
        var fkBody = fk.Body; // i.StatusId (Guid?)
        var isEmpty = Expression.Equal(fkBody, NullGuid);

        if (c.Op == FilterOperator.IsNull) return (Lambda(isEmpty, p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(Expression.NotEqual(fkBody, NullGuid), p), null);

        var (pick, error) = ReadDictValues(field, c);
        if (error is not null) return (null, error);

        // Names and smart values resolve separately into ONE id set: "Sprint 7 or the active
        // sprint" is a single In. In [null] has nothing to resolve, so no round trip.
        var ids = new List<Guid>();
        if (pick!.Names.Count > 0) ids.AddRange(await resolveDict(field.Name, pick.Names, ct));
        if (pick.States.Count > 0)
            ids.AddRange(await resolveDict(FilterLookups.SprintState, pick.States.Distinct().ToList(), ct));

        // i.Fk.HasValue && ids.Contains(i.Fk.Value)
        var hasValue = Expression.Property(fkBody, nameof(Nullable<Guid>.HasValue));
        var value = Expression.Property(fkBody, nameof(Nullable<Guid>.Value));
        var contains = Expression.Call(Expression.Constant(ids), ListContains<Guid>(), value);
        var inSet = Expression.AndAlso(hasValue, contains);

        return c.Op switch
        {
            // One meaning of "empty" across the product: it matches only when asked for. "Is not"
            // and "is none of" therefore require a value — a NULL null-in-NotIn adds nothing.
            FilterOperator.Neq or FilterOperator.NotIn =>
                (Lambda(Expression.AndAlso(hasValue, Expression.Not(contains)), p), null),
            FilterOperator.In when pick.WantsEmpty => (Lambda(Expression.OrElse(isEmpty, inSet), p), null),
            _ => (Lambda(inSet, p), null),   // Eq, In
        };
    }

    /// <summary>
    /// What a dictionary condition asked for: display names, sprint states (from smart values), and
    /// whether (Empty) — a JSON null inside 'values' — was among them.
    /// </summary>
    private sealed record DictPick(List<string> Names, List<string> States, bool WantsEmpty);

    private static (DictPick?, string?) ReadDictValues(FilterField field, FilterCondition c)
    {
        var pick = new DictPick(new List<string>(), new List<string>(), false);

        if (c.Op is FilterOperator.In or FilterOperator.NotIn)
        {
            if (c.Values is null) return (null, $"Field '{c.Field}' with '{c.Op}' requires 'values'.");
            if (c.Values.Count > MaxInValues) return (null, $"'{c.Op}' accepts at most {MaxInValues} values.");
            var wantsEmpty = false;
            foreach (var v in c.Values)
            {
                switch (v.ValueKind)
                {
                    case JsonValueKind.Null:
                        wantsEmpty = true;
                        break;
                    case JsonValueKind.String:
                        pick.Names.Add(v.GetString()!);
                        break;
                    case JsonValueKind.Object:
                        if (ReadSmart(field, v, pick.States) is { } smartError) return (null, smartError);
                        break;
                    default:
                        return (null, $"Field '{c.Field}' values must be strings, or null for (Empty).");
                }
            }
            return (pick with { WantsEmpty = wantsEmpty }, null);
        }

        // Eq / Neq: one name, or one smart value.
        if (c.Value is { ValueKind: JsonValueKind.String } sv)
        {
            pick.Names.Add(sv.GetString()!);
        }
        else if (c.Value is { ValueKind: JsonValueKind.Object } ov)
        {
            if (ReadSmart(field, ov, pick.States) is { } smartError) return (null, smartError);
        }
        else
        {
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        }
        return (pick, null);
    }

    /// <summary>A smart value, {"smart": "active"}: valid on sprint only, and only with a known key.</summary>
    private static string? ReadSmart(FilterField field, JsonElement v, List<string> states)
    {
        if (field.Name != "sprint")
            return $"Smart values are only available on 'sprint', not '{field.Name}'.";
        if (!v.TryGetProperty("smart", out var key) || key.ValueKind != JsonValueKind.String)
            return "A smart value must look like {\"smart\": \"active\"}.";
        var smart = SmartSprints.Find(key.GetString()!);
        if (smart is null)
            return $"Unknown smart value '{key.GetString()}'. Use one of: {string.Join(", ", SmartSprints.All.Select(s => s.Key))}.";
        states.AddRange(smart.States);
        return null;
    }

    /// <summary>
    /// Status category, through the status: category names → the ids of the statuses in those
    /// categories → StatusId in set. No join in the issue query. (Empty) is an issue with no
    /// status OR whose status has no category — both answer "which category?" with nothing, and
    /// the empty rule applies to that meaning exactly as it does to every dictionary field.
    /// </summary>
    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildStatusCategoryAsync(
        FilterField field, FilterCondition c, DictNameResolver resolveDict, CancellationToken ct)
    {
        var fk = (Expression<Func<Issue, Guid?>>)field.Selector;   // i.StatusId
        var p = fk.Parameters[0];
        var fkBody = fk.Body;
        var hasValue = Expression.Property(fkBody, nameof(Nullable<Guid>.HasValue));
        var value = Expression.Property(fkBody, nameof(Nullable<Guid>.Value));
        Expression IdIn(List<Guid> ids) => Expression.Call(Expression.Constant(ids), ListContains<Guid>(), value);

        // Read first, so a malformed condition is refused before any lookup runs.
        DictPick? pick = null;
        if (c.Op is not (FilterOperator.IsNull or FilterOperator.IsNotNull))
        {
            var (read, error) = ReadDictValues(field, c);
            if (error is not null) return (null, error);
            pick = read;
        }

        var uncategorised = (await resolveDict(FilterLookups.UncategorisedStatus, Array.Empty<string>(), ct)).ToList();
        // AndAlso/OrElse short-circuit before .Value in memory; in SQL they are plain NULL checks.
        var isEmpty = Expression.OrElse(Expression.Equal(fkBody, NullGuid), IdIn(uncategorised));
        var categorised = Expression.AndAlso(hasValue, Expression.Not(IdIn(uncategorised)));

        if (c.Op == FilterOperator.IsNull) return (Lambda(isEmpty, p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(categorised, p), null);

        var ids = pick!.Names.Count == 0
            ? new List<Guid>()
            : (await resolveDict(FilterLookups.StatusCategory, pick.Names, ct)).ToList();
        var inSet = Expression.AndAlso(hasValue, IdIn(ids));

        return c.Op switch
        {
            // "Is not Done" = has a category, and it is not Done.
            FilterOperator.Neq or FilterOperator.NotIn =>
                (Lambda(Expression.AndAlso(categorised, Expression.Not(IdIn(ids))), p), null),
            FilterOperator.In when pick.WantsEmpty => (Lambda(Expression.OrElse(isEmpty, inSet), p), null),
            _ => (Lambda(inSet, p), null),   // Eq, In
        };
    }

    private static readonly ConstantExpression NullGuid = Expression.Constant(null, typeof(Guid?));
}
