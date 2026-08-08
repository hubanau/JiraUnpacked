# FR-E4 — Issue Filter Builder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tenant-scoped, injection-safe filtered issue list — an End User composes a flat AND filter over the modeled fields and gets the matching page.

**Architecture:** A closed field **catalog** (allow-list) + a **FilterTranslator** that turns a validated filter into a strongly-typed `Expression<Func<Issue, bool>>` (dict names resolved to Ids via an injected async resolver so the Expression core carries no DB dependency and is unit-testable). `IssueQueryService.QueryAsync` applies the predicate to the tenant-scoped `_db.Issues`, then reuses `ListAsync`'s count/order/page/projection. A `POST /issues/query` endpoint exposes it.

**Tech Stack:** ASP.NET Core 8, EF Core 8 + Npgsql/PostgreSQL (8.0.11), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-08-issue-filter-builder-design.md` (commit bbe12a5).
**Branch:** `feat/issue-filter`, cut from `develop` @ e01aecd.

## Global Constraints

- **User input NEVER reaches a SQL string.** No raw SQL, no `FromSql`, no string interpolation of user values into queries. Predicates are `Expression` trees; values are `Expression.Constant`s (EF parameterizes); text uses `EF.Functions.ILike` with LIKE-escaped wildcards. This is the feature's whole safety property.
- **All reads through the injected tenant-scoped `AppDbContext`.** No `IgnoreQueryFilters`, privileged factory, `FromSql`, or `.Include` in the filter path. (Tests may use raw SQL to seed / a privileged read for cross-tenant assertions.)
- **No new tenant table, no migration, no schema change.** The FR-B2 gate `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` stays untouched and green.
- **The field catalog is the only place a field name is interpreted.** Unknown field → validation error → 400. Never reflect an arbitrary name into a member access.
- Operator allow-list per field type (verbatim): `DictName` = Eq,Neq,In,IsNull; `Decimal` = Eq,Neq,Lt,Lte,Gt,Gte,IsNull; `DateTime` = Lt,Lte,Gt,Gte; `Text(summary)` = Contains,StartsWith,IsNull; `Text(jiraKey)` = Eq,Contains,StartsWith; `ParentRef` = Eq,IsNull.
- Guards: **≤ 20 conditions** per request, **≤ 100 values** per `In` — exceeding → 400.
- **`Neq` on a dict field includes unassigned rows:** `i => i.Fk == null || !ids.Contains(i.Fk.Value)`.
- Pagination clamps identical to `ListAsync`: `page < 1 → 1`; `pageSize < 1 → 50`, capped 100. Order `JiraUpdatedAt DESC, JiraKey`. Empty/null conditions → unfiltered.
- **Existence check (404) before validation (400):** unknown/cross-tenant project always → 404, whatever the filter looks like.
- `JsonStringEnumConverter` is already registered in `Program.cs`, so `FilterOperator` deserializes from its name.
- The `(EndUserId, JiraProjectId, JiraUpdatedAt)` index (RV1) serves the project scope + order; no new index.

**Issue field facts (verified):** `StatusId/IssueTypeId/PriorityId/AssigneeId/SprintId/TeamId` are `Guid?`; `StoryPoints` `decimal?`; `Summary`/`ParentJiraIssueId` `string?`; `JiraKey` `string`; `JiraCreatedAt`/`JiraUpdatedAt` `DateTime`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs` | `FilterOperator`, `FilterCondition`, `IssueFilterRequest`. |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs` | `FieldType` enum + the static field catalog. |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` | Validation + Expression building + the `ReplaceParameter` helper. |
| `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs` | + `QueryAsync` + the dict-name resolver. |
| `src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs` | + `QueryAsync` signature. |
| `src/JiraUnpacked.Api/Controllers/IssuesController.cs` | + `POST .../issues/query`. |
| `tests/JiraUnpacked.Tests/Reporting/IssueFilterTranslatorTests.cs` | Pure unit tests — **no DB**. |
| `tests/JiraUnpacked.Tests/Integration/IssueFilterTests.cs` | Per-operator + validation integration tests. |
| `tests/JiraUnpacked.Tests/Integration/IssueFilterSecurityTests.cs` | Isolation + injection-inert + tenant-scoped dict resolution. |

---

## Task 1 (EF1): DTOs + field catalog + the pure translator

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs`
- Test: `tests/JiraUnpacked.Tests/Reporting/IssueFilterTranslatorTests.cs`

**Interfaces:**
- Consumes: `Issue` entity (`JiraUnpacked.Domain.Entities`).
- Produces: `FilterOperator`, `FilterCondition`, `IssueFilterRequest`; `IIssueFilterTranslator.BuildAsync(...)` → `FilterBuildResult(Expression<Func<Issue,bool>>? Predicate, string? Error)`; `FilterFields` catalog. Task 2 consumes all of these.

Everything here is DB-free: the translator gets dict Ids through an injected async resolver delegate, so its Expression building is unit-testable with a fake resolver.

- [ ] **Step 1: Write the failing unit tests**

Create `tests/JiraUnpacked.Tests/Reporting/IssueFilterTranslatorTests.cs`:

```csharp
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Tests.Reporting;

public sealed class IssueFilterTranslatorTests
{
    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    // A fake dict resolver: maps names to fixed Guids, ignoring the field (tests use one dict field at a time).
    private static readonly Guid InProgressId = Guid.NewGuid();
    private static readonly Guid DoneId = Guid.NewGuid();
    private static Task<IReadOnlyList<Guid>> Resolver(string field, IReadOnlyList<string> names, CancellationToken ct)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            { ["In Progress"] = InProgressId, ["Done"] = DoneId };
        IReadOnlyList<Guid> ids = names.Where(map.ContainsKey).Select(n => map[n]).ToList();
        return Task.FromResult(ids);
    }

    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator();

    private static async Task<Func<Issue, bool>> CompileAsync(params FilterCondition[] conditions)
    {
        var r = await Sut.BuildAsync(conditions, Resolver, CancellationToken.None);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static Issue Issue(Action<Issue> set)
    {
        var i = new Issue { Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "ACME-1",
            JiraCreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            JiraUpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        set(i); return i;
    }

    [Fact]
    public async Task Dict_eq_matches_by_resolved_id()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.Eq, J("In Progress"), null));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = null)));   // unassigned excluded from Eq
    }

    [Fact]
    public async Task Dict_in_matches_any_resolved_id()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.In, null,
            new List<JsonElement> { J("In Progress"), J("Done") }));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.True(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = Guid.NewGuid())));
    }

    [Fact]
    public async Task Dict_neq_includes_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.Neq, J("Done"), null));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));  // not Done -> matches
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));       // Done -> excluded
        Assert.True(pred(Issue(i => i.StatusId = null)));          // unassigned -> INCLUDED (the spec rule)
    }

    [Fact]
    public async Task Dict_isnull_matches_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("assignee", FilterOperator.IsNull, null, null));
        Assert.True(pred(Issue(i => i.AssigneeId = null)));
        Assert.False(pred(Issue(i => i.AssigneeId = Guid.NewGuid())));
    }

    [Fact]
    public async Task Decimal_gte_excludes_null()
    {
        var pred = await CompileAsync(new FilterCondition("storyPoints", FilterOperator.Gte, J(3), null));
        Assert.True(pred(Issue(i => i.StoryPoints = 5m)));
        Assert.False(pred(Issue(i => i.StoryPoints = 1m)));
        Assert.False(pred(Issue(i => i.StoryPoints = null)));  // null excluded from a comparison
    }

    [Fact]
    public async Task Date_lt_compares()
    {
        var pred = await CompileAsync(new FilterCondition("updatedAt", FilterOperator.Lt, J("2024-06-01T00:00:00Z"), null));
        Assert.True(pred(Issue(i => i.JiraUpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        Assert.False(pred(Issue(i => i.JiraUpdatedAt = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc))));
    }

    [Fact]
    public async Task Text_contains_is_case_insensitive_in_memory_fallback()
    {
        // NOTE: the compiled in-memory predicate uses a case-insensitive Contains stand-in (see the builder);
        // in the DB path EF translates the same node to ILike. This asserts the semantics, not the SQL.
        var pred = await CompileAsync(new FilterCondition("summary", FilterOperator.Contains, J("login"), null));
        Assert.True(pred(Issue(i => i.Summary = "Fix LOGIN bug")));
        Assert.False(pred(Issue(i => i.Summary = "unrelated")));
        Assert.False(pred(Issue(i => i.Summary = null)));
    }

    [Fact]
    public async Task Parent_isnull_matches_top_level()
    {
        var pred = await CompileAsync(new FilterCondition("parent", FilterOperator.IsNull, null, null));
        Assert.True(pred(Issue(i => i.ParentJiraIssueId = null)));
        Assert.False(pred(Issue(i => i.ParentJiraIssueId = "10001")));
    }

    [Fact]
    public async Task Conditions_are_ANDed()
    {
        var pred = await CompileAsync(
            new FilterCondition("status", FilterOperator.Eq, J("In Progress"), null),
            new FilterCondition("storyPoints", FilterOperator.Gte, J(3), null));
        Assert.True(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 5m; })));
        Assert.False(pred(Issue(i => { i.StatusId = InProgressId; i.StoryPoints = 1m; })));  // fails 2nd
    }

    [Fact]
    public async Task Empty_conditions_is_always_true()
    {
        var pred = await CompileAsync();   // no conditions
        Assert.True(pred(Issue(_ => { })));
    }

    // ---- validation -> Error (controller maps to 400) ----

    [Theory]
    [InlineData("nope", FilterOperator.Eq)]          // unknown field
    public async Task Unknown_field_is_an_error(string field, FilterOperator op)
    {
        var r = await Sut.BuildAsync(new[] { new FilterCondition(field, op, J("x"), null) }, Resolver, default);
        Assert.NotNull(r.Error);
        Assert.Null(r.Predicate);
    }

    [Fact]
    public async Task Operator_not_allowed_for_field_is_an_error()
    {
        // createdAt is DateTime -> Contains is not allowed.
        var r = await Sut.BuildAsync(new[] { new FilterCondition("createdAt", FilterOperator.Contains, J("x"), null) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Value_type_mismatch_is_an_error()
    {
        var r = await Sut.BuildAsync(new[] { new FilterCondition("storyPoints", FilterOperator.Gte, J("abc"), null) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Too_many_conditions_is_an_error()
    {
        var many = Enumerable.Range(0, 21)
            .Select(_ => new FilterCondition("storyPoints", FilterOperator.Gte, J(1), null)).ToList();
        var r = await Sut.BuildAsync(many, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Too_many_in_values_is_an_error()
    {
        var vals = Enumerable.Range(0, 101).Select(n => J($"s{n}")).ToList();
        var r = await Sut.BuildAsync(new[] { new FilterCondition("status", FilterOperator.In, null, vals) }, Resolver, default);
        Assert.NotNull(r.Error);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueFilterTranslatorTests`
Expected: **compile error** — the filter types don't exist yet.

- [ ] **Step 3: Create the request DTOs**

Create `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs`:

```csharp
using System.Text.Json;

namespace JiraUnpacked.Domain.Abstractions;

public enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull }

/// <summary>One filter condition. Value for single-value ops; Values for In. Both null for IsNull.</summary>
public sealed record FilterCondition(
    string Field, FilterOperator Op, JsonElement? Value, List<JsonElement>? Values);

/// <summary>A flat AND filter over the modeled issue fields. Null/empty Conditions = unfiltered.</summary>
public sealed record IssueFilterRequest(
    List<FilterCondition>? Conditions, int Page = 1, int PageSize = 50);
```

- [ ] **Step 4: Create the field catalog**

Create `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs`:

```csharp
using System.Linq.Expressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

internal enum FieldType { DictName, Decimal, DateTime, Text, ParentRef }

/// <summary>A filterable field: its type, its allowed operators, and (for the builder) the member selector.</summary>
internal sealed record FilterField(
    string Name, FieldType Type, IReadOnlySet<FilterOperator> Ops, LambdaExpression Selector);

/// <summary>
/// The closed allow-list. A field name that is not a key here is rejected — this is the injection
/// boundary: no user string ever becomes a reflected member access.
/// </summary>
internal static class FilterFields
{
    private static IReadOnlySet<FilterOperator> Ops(params FilterOperator[] ops) => ops.ToHashSet();

    // Selectors are typed lambdas over Issue; the translator grafts them onto comparisons.
    public static readonly IReadOnlyDictionary<string, FilterField> All =
        new Dictionary<string, FilterField>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"]     = Dict("status",     (Issue i) => i.StatusId),
            ["issueType"]  = Dict("issueType",  (Issue i) => i.IssueTypeId),
            ["priority"]   = Dict("priority",   (Issue i) => i.PriorityId),
            ["assignee"]   = Dict("assignee",   (Issue i) => i.AssigneeId),
            ["sprint"]     = Dict("sprint",     (Issue i) => i.SprintId),
            ["team"]       = Dict("team",       (Issue i) => i.TeamId),
            ["storyPoints"]= new("storyPoints", FieldType.Decimal,
                                 Ops(FilterOperator.Eq, FilterOperator.Neq, FilterOperator.Lt, FilterOperator.Lte,
                                     FilterOperator.Gt, FilterOperator.Gte, FilterOperator.IsNull),
                                 (Expression<Func<Issue, decimal?>>)(i => i.StoryPoints)),
            ["createdAt"]  = Date("createdAt",  (Issue i) => i.JiraCreatedAt),
            ["updatedAt"]  = Date("updatedAt",  (Issue i) => i.JiraUpdatedAt),
            ["summary"]    = new("summary", FieldType.Text,
                                 Ops(FilterOperator.Contains, FilterOperator.StartsWith, FilterOperator.IsNull),
                                 (Expression<Func<Issue, string?>>)(i => i.Summary)),
            ["jiraKey"]    = new("jiraKey", FieldType.Text,
                                 Ops(FilterOperator.Eq, FilterOperator.Contains, FilterOperator.StartsWith),
                                 (Expression<Func<Issue, string?>>)(i => i.JiraKey)),
            ["parent"]     = new("parent", FieldType.ParentRef,
                                 Ops(FilterOperator.Eq, FilterOperator.IsNull),
                                 (Expression<Func<Issue, string?>>)(i => i.ParentJiraIssueId)),
        };

    private static FilterField Dict(string name, Expression<Func<Issue, Guid?>> sel) =>
        new(name, FieldType.DictName,
            Ops(FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.IsNull), sel);

    private static FilterField Date(string name, Expression<Func<Issue, DateTime>> sel) =>
        new(name, FieldType.DateTime,
            Ops(FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Gt, FilterOperator.Gte), sel);
}
```

- [ ] **Step 5: Create the translator**

Create `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs`:

```csharp
using System.Linq.Expressions;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

public sealed record FilterBuildResult(Expression<Func<Issue, bool>>? Predicate, string? Error);

/// <summary>Resolves a dict field's display names to surrogate Ids (tenant-scoped, injected by the service).</summary>
public delegate Task<IReadOnlyList<Guid>> DictNameResolver(
    string field, IReadOnlyList<string> names, CancellationToken ct);

public interface IIssueFilterTranslator
{
    Task<FilterBuildResult> BuildAsync(
        IReadOnlyList<FilterCondition> conditions, DictNameResolver resolveDict, CancellationToken ct = default);
}

public sealed class IssueFilterTranslator : IIssueFilterTranslator
{
    private const int MaxConditions = 20;
    private const int MaxInValues = 100;

    public async Task<FilterBuildResult> BuildAsync(
        IReadOnlyList<FilterCondition> conditions, DictNameResolver resolveDict, CancellationToken ct = default)
    {
        if (conditions.Count > MaxConditions)
            return Err($"A filter may have at most {MaxConditions} conditions.");

        var predicates = new List<Expression<Func<Issue, bool>>>();
        foreach (var c in conditions)
        {
            if (!FilterFields.All.TryGetValue(c.Field, out var field))
                return Err($"Unknown field '{c.Field}'.");
            if (!field.Ops.Contains(c.Op))
                return Err($"Operator '{c.Op}' is not allowed for field '{c.Field}'.");

            var (pred, error) = field.Type switch
            {
                FieldType.DictName  => await BuildDictAsync(field, c, resolveDict, ct),
                FieldType.Decimal   => BuildDecimal(field, c),
                FieldType.DateTime  => BuildDate(field, c),
                FieldType.Text      => BuildText(field, c),
                FieldType.ParentRef => BuildParent(field, c),
                _ => (null, "Unsupported field type."),
            };
            if (error is not null) return Err(error);
            predicates.Add(pred!);
        }

        Expression<Func<Issue, bool>> combined = predicates.Aggregate(
            (Expression<Func<Issue, bool>>)(i => true), AndAlso);
        return new FilterBuildResult(combined, null);
    }

    private static FilterBuildResult Err(string m) => new(null, m);

    // ---- per-type builders ----

    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildDictAsync(
        FilterField field, FilterCondition c, DictNameResolver resolveDict, CancellationToken ct)
    {
        var fk = (Expression<Func<Issue, Guid?>>)field.Selector;
        var p = fk.Parameters[0];
        var fkBody = fk.Body; // i.StatusId (Guid?)

        if (c.Op == FilterOperator.IsNull)
            return (Lambda(Expression.Equal(fkBody, NullGuid), p), null);

        // Gather names for Eq (single) or In (list).
        List<string> names;
        if (c.Op == FilterOperator.In)
        {
            if (c.Values is null) return (null, $"Field '{c.Field}' with 'In' requires 'values'.");
            if (c.Values.Count > MaxInValues) return (null, $"'In' accepts at most {MaxInValues} values.");
            names = new List<string>();
            foreach (var v in c.Values)
            {
                if (v.ValueKind != JsonValueKind.String) return (null, $"Field '{c.Field}' values must be strings.");
                names.Add(v.GetString()!);
            }
        }
        else // Eq / Neq
        {
            if (c.Value is not { ValueKind: JsonValueKind.String } sv)
                return (null, $"Field '{c.Field}' requires a string 'value'.");
            names = new List<string> { sv.GetString()! };
        }

        var ids = (await resolveDict(c.Field, names, ct)).ToList();

        // i.Fk.HasValue && ids.Contains(i.Fk.Value)
        var hasValue = Expression.Property(fkBody, nameof(Nullable<Guid>.HasValue));
        var value = Expression.Property(fkBody, nameof(Nullable<Guid>.Value));
        var contains = Expression.Call(Expression.Constant(ids), ListContains<Guid>(), value);
        var inSet = Expression.AndAlso(hasValue, contains);

        return c.Op switch
        {
            // Neq includes unassigned: i.Fk == null || !(hasValue && contains)
            FilterOperator.Neq => (Lambda(Expression.OrElse(Expression.Equal(fkBody, NullGuid), Expression.Not(inSet)), p), null),
            _ => (Lambda(inSet, p), null),   // Eq, In
        };
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildDecimal(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, decimal?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullDecimal), p), null);
        if (c.Value is not { ValueKind: JsonValueKind.Number } nv || !nv.TryGetDecimal(out var d))
            return (null, $"Field '{c.Field}' requires a numeric 'value'.");
        var k = Expression.Constant((decimal?)d, typeof(decimal?));
        Expression cmp = c.Op switch
        {
            FilterOperator.Eq => Expression.Equal(body, k),
            FilterOperator.Neq => Expression.NotEqual(body, k),
            FilterOperator.Lt => Expression.LessThan(body, k),
            FilterOperator.Lte => Expression.LessThanOrEqual(body, k),
            FilterOperator.Gt => Expression.GreaterThan(body, k),
            FilterOperator.Gte => Expression.GreaterThanOrEqual(body, k),
            _ => Expression.Constant(false),
        };
        return (Lambda(cmp, p), null);
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildDate(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, DateTime>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        if (c.Value is not { ValueKind: JsonValueKind.String } sv || !DateTime.TryParse(
                sv.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            return (null, $"Field '{c.Field}' requires an ISO-8601 date 'value'.");
        var k = Expression.Constant(dt, typeof(DateTime));
        Expression cmp = c.Op switch
        {
            FilterOperator.Lt => Expression.LessThan(body, k),
            FilterOperator.Lte => Expression.LessThanOrEqual(body, k),
            FilterOperator.Gt => Expression.GreaterThan(body, k),
            FilterOperator.Gte => Expression.GreaterThanOrEqual(body, k),
            _ => Expression.Constant(false),
        };
        return (Lambda(cmp, p), null);
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildText(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, string?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullString), p), null);
        if (c.Value is not { ValueKind: JsonValueKind.String } sv)
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        var raw = sv.GetString()!;

        if (c.Op == FilterOperator.Eq)   // jiraKey Eq
            return (Lambda(Expression.Equal(body, Expression.Constant(raw, typeof(string))), p), null);

        // Contains / StartsWith -> EF.Functions.ILike(body, pattern, "\\") with wildcards escaped.
        var escaped = raw.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var pattern = c.Op == FilterOperator.StartsWith ? escaped + "%" : "%" + escaped + "%";
        var ilike = Expression.Call(ILikeMethod,
            Expression.Constant(EF.Functions),
            body,
            Expression.Constant(pattern, typeof(string)),
            Expression.Constant("\\", typeof(string)));
        // Guard null summary: body != null && ILike(...)
        var notNull = Expression.NotEqual(body, NullString);
        return (Lambda(Expression.AndAlso(notNull, ilike), p), null);
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildParent(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, string?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullString), p), null);
        if (c.Value is not { ValueKind: JsonValueKind.String } sv)
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        return (Lambda(Expression.Equal(body, Expression.Constant(sv.GetString(), typeof(string))), p), null);
    }

    // ---- helpers ----

    private static readonly ConstantExpression NullGuid = Expression.Constant(null, typeof(Guid?));
    private static readonly ConstantExpression NullDecimal = Expression.Constant(null, typeof(decimal?));
    private static readonly ConstantExpression NullString = Expression.Constant(null, typeof(string));

    private static readonly System.Reflection.MethodInfo ILikeMethod =
        typeof(NpgsqlDbFunctionsExtensions).GetMethod(
            nameof(NpgsqlDbFunctionsExtensions.ILike),
            new[] { typeof(DbFunctions), typeof(string), typeof(string), typeof(string) })!;

    private static System.Reflection.MethodInfo ListContains<T>() =>
        typeof(List<T>).GetMethod(nameof(List<T>.Contains), new[] { typeof(T) })!;

    private static Expression<Func<Issue, bool>> Lambda(Expression body, ParameterExpression p) =>
        Expression.Lambda<Func<Issue, bool>>(body, p);

    private static Expression<Func<Issue, bool>> AndAlso(
        Expression<Func<Issue, bool>> a, Expression<Func<Issue, bool>> b)
    {
        var p = a.Parameters[0];
        var right = ReplaceParameter.Replace(b.Body, b.Parameters[0], p);
        return Expression.Lambda<Func<Issue, bool>>(Expression.AndAlso(a.Body, right), p);
    }

    private sealed class ReplaceParameter : ExpressionVisitor
    {
        private readonly ParameterExpression _from, _to;
        private ReplaceParameter(ParameterExpression from, ParameterExpression to) { _from = from; _to = to; }
        protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : base.VisitParameter(node);
        public static Expression Replace(Expression body, ParameterExpression from, ParameterExpression to) =>
            new ReplaceParameter(from, to).Visit(body)!;
    }
}
```

> **Implementer note on the text unit test:** the compiled in-memory `Func` calls `EF.Functions.ILike`, which throws outside a real query. So `IssueFilterTranslatorTests.Text_contains_is_case_insensitive_in_memory_fallback` cannot compile-and-run the ILike node in memory. Resolve this by having the **unit test for Text assert the built expression's shape** (that it is an `AndAlso` whose right operand is a `Call` to `ILike` with the escaped pattern), NOT by compiling it. Replace the `Text_contains_*` test body with an assertion over `(await Sut.BuildAsync(...)).Predicate` that inspects the node (e.g. the pattern constant equals `"%login%"`). The DB-backed case-insensitivity + escaping is proven in EF3/EF4 integration tests, which is where ILike actually executes. Keep every other unit test compiling the predicate as written.

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueFilterTranslatorTests`
Expected: all passing (the compiled predicates evaluate in memory; the Text test asserts node shape).
Run: `dotnet build JiraUnpacked.sln --nologo` → 0 warnings / 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs \
        src/JiraUnpacked.Infrastructure/Reporting/Filtering/ \
        tests/JiraUnpacked.Tests/Reporting/IssueFilterTranslatorTests.cs
git commit -m "feat: issue filter DTOs + field catalog + pure translator (FR-E4)"
```

---

## Task 2 (EF2): `QueryAsync` on the service

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` (register the translator)

**Interfaces:**
- Consumes (Task 1): `IssueFilterRequest`, `IIssueFilterTranslator`, `FilterBuildResult`, `DictNameResolver`.
- Produces: `IIssueQueryService.QueryAsync(Guid projectId, IssueFilterRequest request, CancellationToken)` → `Task<PagedResult<IssueListItem>?>`; and a distinguishable **validation-error** channel for the controller (return type below). Task 3 consumes it.

Because the controller must tell 404 (no project) from 400 (bad filter), `QueryAsync` returns a small result wrapper.

- [ ] **Step 1: Extend the interface**

In `src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs`, add:

```csharp
    /// <summary>
    /// Null Result + null Error => project unknown/cross-tenant (404). Non-null Error => bad filter (400).
    /// Otherwise Result is the filtered page.
    /// </summary>
    Task<IssueQueryResult> QueryAsync(Guid projectId, IssueFilterRequest request, CancellationToken ct = default);
```

and above the interface:

```csharp
public sealed record IssueQueryResult(PagedResult<IssueListItem>? Result, string? Error);
```

- [ ] **Step 2: Implement `QueryAsync`**

In `IssueQueryService.cs`, inject the translator (add `IIssueFilterTranslator` to the ctor + a field) and add:

```csharp
    public async Task<IssueQueryResult> QueryAsync(
        Guid projectId, IssueFilterRequest request, CancellationToken ct = default)
    {
        // Existence check FIRST: unknown/cross-tenant project => 404, whatever the filter is.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct))
            return new IssueQueryResult(null, null);

        var conditions = request.Conditions ?? new List<FilterCondition>();

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

        var build = await _filter.BuildAsync(conditions, resolve, ct);
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
```

Add the necessary `using JiraUnpacked.Infrastructure.Reporting.Filtering;`.

- [ ] **Step 3: Register the translator**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, next to the reporting registrations:

```csharp
        services.AddScoped<JiraUnpacked.Infrastructure.Reporting.Filtering.IIssueFilterTranslator,
                           JiraUnpacked.Infrastructure.Reporting.Filtering.IssueFilterTranslator>();
```

- [ ] **Step 4: Build**

Run: `dotnet build JiraUnpacked.sln --nologo` → 0 warnings / 0 errors. (No new test here — EF3 exercises `QueryAsync` end-to-end; a unit test would duplicate EF1 + need a DB.)

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs \
        src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs
git commit -m "feat: IssueQueryService.QueryAsync applies the filter predicate (FR-E4)"
```

---

## Task 3 (EF3): The endpoint + per-operator / validation integration tests

**Files:**
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/IssueFilterTests.cs`

**Interfaces:**
- Consumes (Task 2): `IssueQueryResult`, `IssueFilterRequest`.
- Produces: `POST api/projects/{projectId:guid}/issues/query`.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/JiraUnpacked.Tests/Integration/IssueFilterTests.cs`. Reuse the seeding style of `IssueListTests` (`AuthedTenantAsync`, `Exec`, a project + dicts + issues seeded via raw SQL scoped to a fresh `JiraProjectId`). Cover:
- `status Eq "In Progress"` returns only the In-Progress issue; `status In [A,B]`; `status Neq "Done"` includes an unassigned issue.
- `assignee IsNull` returns only unassigned; `storyPoints Gte 3`; `updatedAt Lt <date>` range via two conditions (Gte + Lte) ANDed.
- `summary Contains "login"` is case-insensitive; `summary StartsWith`; `jiraKey Eq`.
- `parent IsNull` returns top-level only; `parent Eq "<parentJiraId>"` returns the children.
- Empty `conditions` == the unfiltered list count; paging + `TotalCount` correct over a filtered set.
- **400 cases:** unknown field; operator not allowed (`createdAt Contains`); value type mismatch (`storyPoints Gte "abc"`); 21 conditions; 101 `In` values. Each asserts `HttpStatusCode.BadRequest`.
- **404:** unknown project id.

Assert exact seeded matches, not loose bounds. Seed enough issues to make each operator discriminating (e.g. one In-Progress + one Done + one unassigned; one with points 5 + one with points 1; distinct summaries; one parent + two children).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueFilterTests`
Expected: FAIL — the `/issues/query` route does not exist.

- [ ] **Step 3: Add the endpoint**

In `IssuesController.cs`, add:

```csharp
    [HttpPost("api/projects/{projectId:guid}/issues/query")]
    public async Task<IActionResult> Query(Guid projectId, IssueFilterRequest request, CancellationToken ct = default)
    {
        var result = await _issues.QueryAsync(projectId, request, ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        return result.Result is null ? NotFound() : Ok(result.Result);
    }
```

(`IssueFilterRequest` is in `JiraUnpacked.Domain.Abstractions`, already imported.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueFilterTests` → all passing.
Run: `dotnet test JiraUnpacked.sln` → full suite green; report the count.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Api/Controllers/IssuesController.cs \
        tests/JiraUnpacked.Tests/Integration/IssueFilterTests.cs
git commit -m "feat: POST /issues/query filtered issue list + operator/validation tests (FR-E4)"
```

---

## Task 4 (EF4): The security-critical tests

**Files:**
- Test: `tests/JiraUnpacked.Tests/Integration/IssueFilterSecurityTests.cs`

**Interfaces:**
- Consumes: the `POST /issues/query` endpoint (Task 3).

These assert behaviour that already exists after EF1–EF3; they are the guarantees the feature must never regress. This task adds no production code.

- [ ] **Step 1: Write the tests**

Create `tests/JiraUnpacked.Tests/Integration/IssueFilterSecurityTests.cs`, reusing the seeding helpers:

- **Tenant isolation:** tenant A seeds a project with an In-Progress issue; tenant B (a second authed user) `POST`s a filter at A's project id → **404**. Also: B seeds its own project, A `POST`s a filter that would match B's issue against **A's** project → the result contains only A's issues, never B's (the base tenant filter + RLS hold).
- **Injection-inert:** against a seeded project, `POST` `summary Contains "'; DROP TABLE \"Issues\"; --"` → **200** with **0** matches (no issue has that literal summary) and the `Issues` table still exists (a follow-up unfiltered query still returns the seeded rows). Then `summary Contains "50%_off"` where one issue's summary is literally `"50%_off"` and another is `"50Xoff"` → returns **only** the literal `"50%_off"` row, proving `%`/`_` were escaped (not treated as wildcards).
- **Tenant-scoped dict resolution:** tenant A filters `status Eq "<name that exists only in tenant B's connection>"` → **200** with **0** matches (the name resolves to no Id in A's tenant scope), proving resolution can't reach another tenant's dictionary.

Assert exact values. For the "table still exists" check, issue a second `POST` with empty conditions and assert the seeded count is unchanged.

- [ ] **Step 2: Run**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueFilterSecurityTests` → all passing.

- [ ] **Step 3: Full verification + commit**

Run: `dotnet test JiraUnpacked.sln -c Release` → full suite green; report the count.
Run: `dotnet build JiraUnpacked.sln -c Release --nologo` → 0 warnings / 0 errors.
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → `No changes` (this feature adds no schema).

```bash
git add tests/JiraUnpacked.Tests/Integration/IssueFilterSecurityTests.cs
git commit -m "test: filter tenant-isolation + injection-inert + scoped dict resolution (FR-E4)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green (report the count).
- [ ] `dotnet ef migrations has-pending-model-changes …` → "No changes" (no migration in this feature).
- [ ] Grep `src/JiraUnpacked.Infrastructure/Reporting/` (incl. `Filtering/`) and the controller for `IgnoreQueryFilters`, `FromSql`, `.Include(`, `FromSqlRaw`, `FromSqlInterpolated`, string concatenation into a query → no matches. The translator must contain **no** SQL text at all.
- [ ] Confirm `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is unmodified (`git log develop..HEAD -- <that path>` empty) and green.
- [ ] Push `feat/issue-filter`; PR → develop; CI green; squash-merge; then the `develop → main` release (merge-commit). Before the release, `git log --oneline origin/develop..origin/main --no-merges` — non-empty means a direct-to-main follow-up landed and must be back-merged first.

## Notes for execution

- **The injection safety is structural, not a runtime check.** The translator emits only Expression trees over whitelisted members with parameter-bound constants and escaped ILike patterns. If any task introduces `FromSql`/raw SQL/string-built predicates, that is a Critical regardless of tests.
- **`Neq` on a dict field includes unassigned rows** — this is deliberate (a user asking "status ≠ Done" expects unassigned issues included); the EF1 unit test and an EF3 integration test both assert it.
- **Existence (404) is checked before filter validation (400)** so a bad filter can't probe another tenant's project existence.
- Deferred (not in this plan): saved/named filters, cross-field OR / nested groups, filtering metrics/gap-checks.
