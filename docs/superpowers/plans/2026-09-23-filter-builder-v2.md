# Filter Builder v2 (Groups, Not In, Explicit Empty, Multi-Select Picker) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the issue-list filter express real questions: AND/OR on chosen conditions via nested groups, exclusion lists (`NotIn`), "empty" only when asked for, and values picked from a searchable multi-select with counts. Disabled and duplicated parts come with it.

**Architecture:** The query layer already nests (`FilterGroup` is recursive and guarded), so the server work is small and local: three appended operators, one meaning of "empty" in the translator, a `Disabled` flag the translator skips, counts on `filter-values`, and a tree-shaped `filter` on `export.csv`. The SPA replaces its flat condition list with one `EditorGroup` tree (wire `FilterGroup` + client-only ids) edited by a recursive `GroupEditor`, persisted unpruned in the URL and pruned before every request.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit; React 19, react-router-dom 7, @tanstack/react-query 5, Vitest + Testing Library + user-event 14 + MSW 2, Tailwind 4.

**Spec:** `docs/superpowers/specs/2026-09-23-filter-builder-v2-design.md`

## Global Constraints

- **Work only in the worktree `C:/jw-filters`** (branch `feat/filter-builder-v2`). Never read or write `C:\Users\AliaksandrHubanau\Private\Claude\JiraUnpacked` — other sessions use that checkout. Do not push.
- **Integration tests use their own database:** every `dotnet test` run sets `JU_TEST_DATABASE=ju_test_filters` (Postgres = the `jiraunpacked-db` container on `localhost:5432`, already running). Bash: `JU_TEST_DATABASE=ju_test_filters dotnet test …`. PowerShell: `$env:JU_TEST_DATABASE='ju_test_filters'; dotnet test …`. The suite never truncates; the shared default DB collides with parallel branches.
- **Web setup once per worktree:** `cd src/JiraUnpacked.Web && npm ci` (the worktree has no `node_modules`).
- SPA keeps **exactly four runtime deps**: `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`. **No new dependencies of any kind** (runtime or dev).
- Filter input never reaches a SQL string: closed catalogs + Expression trees; `EF.Functions.ILike` with backslash-first wildcard escaping for text.
- All reads through the injected tenant-scoped `AppDbContext`. No `IgnoreQueryFilters`, no `FromSql`.
- **No schema change, no migration.** `Disabled` lives only in request JSON and the existing `SavedFilters.DefinitionJson` jsonb.
- `FilterOperator` gains `NotIn`, `IsNotNull`, `NotContains` **appended** after `IsNull`. Enums stay serialised by name.
- Empty rule (spec §1): `Eq`/`In` match empty only if (Empty) is chosen; `Neq`/`NotIn` never; `IsNull` yes; `IsNotNull` no; `Lt/Lte/Gt/Gte/Contains/StartsWith/NotContains` never.
- Wire (spec §1): a JSON `null` inside `values` = (Empty). `Eq`/`Neq` with (Empty) are normalised by the editor to `IsNull`/`IsNotNull`; a `value` of JSON null on `Eq`/`Neq` stays a 400.
- Operator labels, verbatim (wire keeps enum names): `Eq` is · `Neq` is not · `In` is any of · `NotIn` is none of · `IsNull` is empty · `IsNotNull` is not empty · `Lt` < · `Lte` ≤ · `Gt` > · `Gte` ≥ · `Contains` contains · `NotContains` does not contain · `StartsWith` starts with.
- Limits (`maxConditions`, `maxInValues`, `maxGroups`, `maxDepth`) always come from `GET /api/filter-catalog`, never hard-coded in the SPA. Disabled nodes do not count, on either side.
- `src/ui/` imports neither `src/api/` nor `src/auth/`; new components live in `src/features/issues/`. Colours only through `--ju-*` CSS variables.
- Only `ju.username` and `ju.theme` may go to `localStorage`. List state lives in the URL.

## Review Focus

1. **Typing into a value box while the URL is being rewritten** — the user expects focus and the half-typed text to stay put. React Router applies URL writes in a transition, so a naive "parse the URL every render" mints fresh ids and remounts every row per keystroke. Pinned by Task 6's `keeps the editor’s ids across its own URL writes` test plus the existing `user.type` tests.
2. **A link made before this change** (`filter=[…]&logic=Or`) — expected to open meaning the same thing and to be rewritten as a tree on the first edit. Pinned by Task 5 (`reads a legacy flat list`), Task 6 (`reads a legacy flat link … writes a tree without it`) and Task 4 (`A_legacy_array_filter_still_honours_its_logic_param`).
3. **A dictionary name containing a space or comma in the fallback (typed) list** — "In Progress" must stay one value, not two chips. Pinned by Task 8 (`never splits without a separator`) and Task 9 (`falls back to typing …`).
4. **A value in a link that the picker no longer offers** (renamed status, value from another project) — must stay visible and removable, not be silently dropped or shown as a different value. Pinned by Task 8 (`keeps a chosen value the list no longer offers`, `still shows a chosen value the list does not offer`).
5. **Export of a tree with (Empty) and disabled parts, with a stale `logic` param still in the link** — must contain exactly the rows the list shows. Pinned by Task 4 (`Exports_a_filter_tree_and_ignores_the_legacy_logic_param`).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs` | + `NotIn`, `IsNotNull`, `NotContains`; + `Disabled` on `FilterCondition`/`FilterGroup` |
| `src/JiraUnpacked.Domain/Abstractions/IFilterValueService.cs` | + `FilterValueCount`; `FilterValuesResult` carries counts + `EmptyCount` |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs` | per-field operator sets (spec §2 table) |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` | empty rule, new operators, disabled skipping |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs` | explicit operator display order |
| `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs` | per-value counts + empty count |
| `src/JiraUnpacked.Api/Controllers/IssuesController.cs` | `filter-values` response shape; `export.csv` accepts a tree |
| `src/JiraUnpacked.Web/src/api/issues.ts` | wire types (+ops, `disabled`, null values), `getFilterValues` shape, `exportIssuesCsv(filter)` |
| `src/JiraUnpacked.Web/src/features/issues/filterTree.ts` | **new** — `EditorGroup` model: ids, prune, count, clone, URL (de)serialise, legacy parse |
| `src/JiraUnpacked.Web/src/features/issues/useListState.ts` | holds the tree; URL round-trip that keeps ids across its own writes |
| `src/JiraUnpacked.Web/src/features/issues/GroupEditor.tsx` | **new** — recursive group: logic, add/duplicate/disable/remove, limits |
| `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.tsx` | catalog fetch, root `GroupEditor`, Clear all, empty state |
| `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` | field/operator/value controls per spec §6 table |
| `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx` | **new** — `SingleValueSelect`, `MultiValuePicker` |
| `src/JiraUnpacked.Web/src/features/issues/ChipInput.tsx` | **new** — typed/pasted list entry as chips |
| `src/JiraUnpacked.Web/src/features/issues/styles.ts` | **new** — shared `INPUT_CLASS` |
| `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.ts` | + `OPERATOR_LABELS`, `operatorLabel` |
| `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx` | tree → pruned request/export |
| `src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts` | **new** — the catalog as the server now serves it, for tests |

---

### Task 1: New operators and one meaning of "empty" (server)

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs`
- Test: `tests/JiraUnpacked.Tests/Reporting/IssueFilterTranslatorTests.cs`
- Create: `tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/IssueFilterTests.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs`

**Interfaces:**
- Produces: `enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull, NotIn, IsNotNull, NotContains }`.
- Produces: `FilterCatalogService.DisplayOrder` (`internal static readonly FilterOperator[]`) — catalog operators are listed in this order: `Eq, Neq, In, NotIn, Lt, Lte, Gt, Gte, Contains, NotContains, StartsWith, IsNull, IsNotNull`.
- Produces (catalog operator lists, in that order): dict fields `Eq, Neq, In, NotIn, IsNull, IsNotNull`; storyPoints `Eq, Neq, Lt, Lte, Gt, Gte, IsNull, IsNotNull`; createdAt/updatedAt `Lt, Lte, Gt, Gte`; summary `Contains, NotContains, StartsWith, IsNull, IsNotNull`; jiraKey `Eq, In, NotIn, Contains, StartsWith`; parent `Eq, IsNull, IsNotNull`.

- [ ] **Step 1: Write the failing translator tests**

In `tests/JiraUnpacked.Tests/Reporting/IssueFilterTranslatorTests.cs`, add `using System.Text.Json.Serialization;` at the top, add these helpers right after the existing `J(...)` helper:

```csharp
    private static readonly JsonElement JNull = JsonSerializer.SerializeToElement<object?>(null);

    /// <summary>A `values` list; a C# null becomes a JSON null, which is how (Empty) travels.</summary>
    private static List<JsonElement> Vals(params object?[] vs) =>
        vs.Select(v => v is null ? JNull : J(v)).ToList();

    /// <summary>Finds `NOT ILike(...)` anywhere under AndAlso/OrElse nodes.</summary>
    private static MethodCallExpression? FindNegatedIlike(Expression e) => e switch
    {
        UnaryExpression { NodeType: ExpressionType.Not, Operand: MethodCallExpression { Method.Name: "ILike" } call } => call,
        BinaryExpression b => FindNegatedIlike(b.Left) ?? FindNegatedIlike(b.Right),
        _ => null,
    };
```

**Replace** the existing `Dict_neq_includes_unassigned` test with:

```csharp
    [Fact]
    public async Task Dict_neq_excludes_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.Neq, J("Done"), null));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));
        // Empty matches only when asked for: "is not Done" says nothing about issues with no status.
        Assert.False(pred(Issue(i => i.StatusId = null)));
    }
```

Then append these tests to the class:

```csharp
    [Fact]
    public void A_json_null_inside_values_arrives_as_a_Null_element()
    {
        // The wire representation of (Empty) rests on this; pin it rather than assume it.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var c = JsonSerializer.Deserialize<FilterCondition>(
            """{"field":"sprint","op":"In","values":["S1",null]}""", options)!;

        Assert.Equal(2, c.Values!.Count);
        Assert.Equal(JsonValueKind.String, c.Values[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, c.Values[1].ValueKind);
    }

    [Fact]
    public async Task Dict_notin_excludes_the_listed_values_and_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.NotIn, null, Vals("Done")));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = null)));
    }

    [Fact]
    public async Task Dict_in_with_a_null_value_also_matches_unassigned()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.In, null, Vals("Done", null)));
        Assert.True(pred(Issue(i => i.StatusId = DoneId)));
        Assert.True(pred(Issue(i => i.StatusId = null)));
        Assert.False(pred(Issue(i => i.StatusId = InProgressId)));
    }

    [Fact]
    public async Task Dict_in_with_only_null_is_is_empty()
    {
        var pred = await CompileAsync(new FilterCondition("sprint", FilterOperator.In, null, Vals((object?)null)));
        Assert.True(pred(Issue(i => i.SprintId = null)));
        Assert.False(pred(Issue(i => i.SprintId = Guid.NewGuid())));
    }

    [Fact]
    public async Task Dict_notin_accepts_a_null_value_as_redundant()
    {
        var pred = await CompileAsync(new FilterCondition("status", FilterOperator.NotIn, null, Vals("Done", null)));
        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.False(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = null)));
    }

    [Fact]
    public async Task Dict_in_rejects_a_value_that_is_neither_string_nor_null()
    {
        var r = await Sut.BuildAsync(
            new[] { new FilterCondition("status", FilterOperator.In, null, Vals("Done", 5)) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Dict_isnotnull_matches_only_assigned()
    {
        var pred = await CompileAsync(new FilterCondition("assignee", FilterOperator.IsNotNull, null, null));
        Assert.True(pred(Issue(i => i.AssigneeId = Guid.NewGuid())));
        Assert.False(pred(Issue(i => i.AssigneeId = null)));
    }

    [Fact]
    public async Task Decimal_neq_excludes_null()
    {
        // C# null semantics (which EF reproduces in SQL) make a bare `StoryPoints != 5` TRUE for NULL.
        var pred = await CompileAsync(new FilterCondition("storyPoints", FilterOperator.Neq, J(5), null));
        Assert.True(pred(Issue(i => i.StoryPoints = 3m)));
        Assert.False(pred(Issue(i => i.StoryPoints = 5m)));
        Assert.False(pred(Issue(i => i.StoryPoints = null)));
    }

    [Fact]
    public async Task Decimal_isnotnull_matches_only_estimated()
    {
        var pred = await CompileAsync(new FilterCondition("storyPoints", FilterOperator.IsNotNull, null, null));
        Assert.True(pred(Issue(i => i.StoryPoints = 0m)));
        Assert.False(pred(Issue(i => i.StoryPoints = null)));
    }

    [Fact]
    public async Task Summary_isnotnull_matches_only_issues_with_a_summary()
    {
        var pred = await CompileAsync(new FilterCondition("summary", FilterOperator.IsNotNull, null, null));
        Assert.True(pred(Issue(i => i.Summary = "x")));
        Assert.False(pred(Issue(i => i.Summary = null)));
    }

    [Fact]
    public async Task Parent_isnotnull_matches_only_children()
    {
        var pred = await CompileAsync(new FilterCondition("parent", FilterOperator.IsNotNull, null, null));
        Assert.True(pred(Issue(i => i.ParentJiraIssueId = "10001")));
        Assert.False(pred(Issue(i => i.ParentJiraIssueId = null)));
    }

    [Fact]
    public async Task JiraKey_in_and_notin_match_exact_keys()
    {
        var inList = await CompileAsync(new FilterCondition("jiraKey", FilterOperator.In, null, Vals("ACME-1", "ACME-3")));
        Assert.True(inList(Issue(i => i.JiraKey = "ACME-1")));
        Assert.False(inList(Issue(i => i.JiraKey = "ACME-2")));

        var notIn = await CompileAsync(new FilterCondition("jiraKey", FilterOperator.NotIn, null, Vals("ACME-1", "ACME-3")));
        Assert.True(notIn(Issue(i => i.JiraKey = "ACME-2")));
        Assert.False(notIn(Issue(i => i.JiraKey = "ACME-3")));
    }

    [Fact]
    public async Task JiraKey_in_rejects_null_because_a_key_is_never_empty()
    {
        var r = await Sut.BuildAsync(
            new[] { new FilterCondition("jiraKey", FilterOperator.In, null, Vals("ACME-1", null)) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Summary_notcontains_guards_null_and_negates_an_escaped_ilike()
    {
        // ILike has no in-memory implementation (see Text_contains_builds_ilike_call_with_escaped_pattern),
        // so this asserts the tree: `Summary != null && !ILike(Summary, "%a\%b%", "\")`.
        var r = await Sut.BuildAsync(
            new[] { new FilterCondition("summary", FilterOperator.NotContains, J("a%b"), null) }, Resolver, default);
        Assert.Null(r.Error);

        var body = Assert.IsAssignableFrom<BinaryExpression>(r.Predicate!.Body);
        Assert.Equal(ExpressionType.AndAlso, body.NodeType);
        var guard = Assert.IsAssignableFrom<BinaryExpression>(body.Left);
        Assert.Equal(ExpressionType.NotEqual, guard.NodeType);
        Assert.Null(Assert.IsAssignableFrom<ConstantExpression>(guard.Right).Value);

        var call = FindNegatedIlike(body);
        Assert.NotNull(call);
        Assert.Equal(@"%a\%b%", Assert.IsAssignableFrom<ConstantExpression>(call!.Arguments[2]).Value);
    }

    [Fact]
    public async Task NotIn_with_more_than_the_limit_is_an_error()
    {
        var vals = Enumerable.Range(0, 101).Select(n => J($"s{n}")).ToList();
        var r = await Sut.BuildAsync(new[] { new FilterCondition("status", FilterOperator.NotIn, null, vals) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Eq_with_a_json_null_value_is_still_an_error()
    {
        // The editor turns "is (Empty)" into IsNull before sending; a null value on the wire is a client bug.
        var r = await Sut.BuildAsync(new[] { new FilterCondition("status", FilterOperator.Eq, JNull, null) }, Resolver, default);
        Assert.NotNull(r.Error);
    }

    [Theory]
    [InlineData("createdAt", FilterOperator.IsNotNull)]
    [InlineData("jiraKey", FilterOperator.IsNull)]
    [InlineData("jiraKey", FilterOperator.IsNotNull)]
    [InlineData("jiraKey", FilterOperator.NotContains)]
    [InlineData("parent", FilterOperator.Neq)]
    [InlineData("parent", FilterOperator.In)]
    [InlineData("storyPoints", FilterOperator.NotIn)]
    [InlineData("summary", FilterOperator.NotIn)]
    [InlineData("status", FilterOperator.NotContains)]
    public async Task Operators_outside_a_fields_set_are_rejected(string field, FilterOperator op)
    {
        var r = await Sut.BuildAsync(new[] { new FilterCondition(field, op, J("x"), Vals("x")) }, Resolver, default);
        Assert.Equal($"Operator '{op}' is not allowed for field '{field}'.", r.Error);
    }
```

- [ ] **Step 2: Write the failing catalog unit test**

Create `tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Tests.Reporting;

public sealed class FilterCatalogServiceTests
{
    private static List<string> OpsOf(string field) =>
        new FilterCatalogService().Get().Fields.Single(f => f.Name == field).Operators.ToList();

    [Fact]
    public void Operators_are_listed_in_reading_order_not_declaration_order()
    {
        // FilterOperator is append-only (NotIn/IsNotNull/NotContains went last), so ordinal order
        // would put "is empty" between "is any of" and "is none of" in the editor.
        Assert.Equal(new[] { "Eq", "Neq", "In", "NotIn", "IsNull", "IsNotNull" }, OpsOf("status"));
        Assert.Equal(new[] { "Eq", "Neq", "Lt", "Lte", "Gt", "Gte", "IsNull", "IsNotNull" }, OpsOf("storyPoints"));
        Assert.Equal(new[] { "Lt", "Lte", "Gt", "Gte" }, OpsOf("createdAt"));
        Assert.Equal(new[] { "Contains", "NotContains", "StartsWith", "IsNull", "IsNotNull" }, OpsOf("summary"));
        Assert.Equal(new[] { "Eq", "In", "NotIn", "Contains", "StartsWith" }, OpsOf("jiraKey"));
        Assert.Equal(new[] { "Eq", "IsNull", "IsNotNull" }, OpsOf("parent"));
    }

    [Fact]
    public void Every_operator_has_a_display_position()
    {
        foreach (var op in Enum.GetValues<FilterOperator>())
            Assert.Contains(op, FilterCatalogService.DisplayOrder);
    }
}
```

- [ ] **Step 3: Run the unit tests to verify they fail**

Run: `cd C:/jw-filters && dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueFilterTranslatorTests|FullyQualifiedName~FilterCatalogServiceTests" -v q`
Expected: build FAILS — `FilterOperator` has no `NotIn`/`IsNotNull`/`NotContains`; `FilterCatalogService.DisplayOrder` does not exist.

- [ ] **Step 4: Append the operators**

In `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs` replace the enum line and the `FilterCondition` doc comment:

```csharp
/// <summary>
/// Serialised and stored BY NAME. New members are appended, never inserted: the ordinal is a
/// history, not a meaning (the catalog's display order lives in FilterCatalogService).
/// </summary>
public enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull, NotIn, IsNotNull, NotContains }
```

```csharp
/// <summary>
/// One filter condition. Value for single-value ops; Values for In/NotIn, where a JSON null
/// element stands for (Empty). Both null for IsNull/IsNotNull.
/// </summary>
```

- [ ] **Step 5: Widen the per-field operator sets**

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs` replace the `storyPoints`, `summary`, `jiraKey`, `parent` entries and the `Dict` helper:

```csharp
            ["storyPoints"]= new("storyPoints", FieldType.Decimal,
                                 Ops(FilterOperator.Eq, FilterOperator.Neq, FilterOperator.Lt, FilterOperator.Lte,
                                     FilterOperator.Gt, FilterOperator.Gte, FilterOperator.IsNull, FilterOperator.IsNotNull),
                                 (Expression<Func<Issue, decimal?>>)(i => i.StoryPoints)),
```

```csharp
            ["summary"]    = new("summary", FieldType.Text,
                                 Ops(FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.StartsWith,
                                     FilterOperator.IsNull, FilterOperator.IsNotNull),
                                 (Expression<Func<Issue, string?>>)(i => i.Summary)),
            ["jiraKey"]    = new("jiraKey", FieldType.Text,
                                 Ops(FilterOperator.Eq, FilterOperator.In, FilterOperator.NotIn,
                                     FilterOperator.Contains, FilterOperator.StartsWith),
                                 (Expression<Func<Issue, string?>>)(i => i.JiraKey)),
            ["parent"]     = new("parent", FieldType.ParentRef,
                                 Ops(FilterOperator.Eq, FilterOperator.IsNull, FilterOperator.IsNotNull),
                                 (Expression<Func<Issue, string?>>)(i => i.ParentJiraIssueId)),
```

```csharp
    private static FilterField Dict(string name, Expression<Func<Issue, Guid?>> sel) =>
        new(name, FieldType.DictName,
            Ops(FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.NotIn,
                FilterOperator.IsNull, FilterOperator.IsNotNull), sel);
```

- [ ] **Step 6: Implement the empty rule in the translator**

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` replace `BuildDictAsync`, `BuildDecimal`, `BuildText` and `BuildParent` with:

```csharp
    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildDictAsync(
        FilterField field, FilterCondition c, DictNameResolver resolveDict, CancellationToken ct)
    {
        var fk = (Expression<Func<Issue, Guid?>>)field.Selector;
        var p = fk.Parameters[0];
        var fkBody = fk.Body; // i.StatusId (Guid?)
        var isEmpty = Expression.Equal(fkBody, NullGuid);

        if (c.Op == FilterOperator.IsNull) return (Lambda(isEmpty, p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(Expression.NotEqual(fkBody, NullGuid), p), null);

        // Names to resolve, and whether (Empty) — a JSON null inside 'values' — was asked for.
        List<string> names;
        var wantsEmpty = false;
        if (c.Op is FilterOperator.In or FilterOperator.NotIn)
        {
            if (c.Values is null) return (null, $"Field '{c.Field}' with '{c.Op}' requires 'values'.");
            if (c.Values.Count > MaxInValues) return (null, $"'{c.Op}' accepts at most {MaxInValues} values.");
            names = new List<string>();
            foreach (var v in c.Values)
            {
                if (v.ValueKind == JsonValueKind.Null) { wantsEmpty = true; continue; }
                if (v.ValueKind != JsonValueKind.String)
                    return (null, $"Field '{c.Field}' values must be strings, or null for (Empty).");
                names.Add(v.GetString()!);
            }
        }
        else // Eq / Neq
        {
            if (c.Value is not { ValueKind: JsonValueKind.String } sv)
                return (null, $"Field '{c.Field}' requires a string 'value'.");
            names = new List<string> { sv.GetString()! };
        }

        // In [null] is IsNull by another name: nothing to resolve, so no round trip.
        var ids = names.Count == 0 ? new List<Guid>() : (await resolveDict(field.Name, names, ct)).ToList();

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
            FilterOperator.In when wantsEmpty => (Lambda(Expression.OrElse(isEmpty, inSet), p), null),
            _ => (Lambda(inSet, p), null),   // Eq, In
        };
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildDecimal(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, decimal?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        var notNull = Expression.NotEqual(body, NullDecimal);
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullDecimal), p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(notNull, p), null);
        if (c.Value is not { ValueKind: JsonValueKind.Number } nv || !nv.TryGetDecimal(out var d))
            return (null, $"Field '{c.Field}' requires a numeric 'value'.");
        var k = Expression.Constant((decimal?)d, typeof(decimal?));
        Expression cmp = c.Op switch
        {
            FilterOperator.Eq => Expression.Equal(body, k),
            // Guarded explicitly: under C# null semantics, which EF reproduces in SQL, a bare
            // `StoryPoints != k` is TRUE for NULL — "is not 5" would claim every unestimated issue.
            FilterOperator.Neq => Expression.AndAlso(notNull, Expression.NotEqual(body, k)),
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
        var notNull = Expression.NotEqual(body, NullString);
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullString), p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(notNull, p), null);

        if (c.Op is FilterOperator.In or FilterOperator.NotIn)   // jiraKey
        {
            if (c.Values is null) return (null, $"Field '{c.Field}' with '{c.Op}' requires 'values'.");
            if (c.Values.Count > MaxInValues) return (null, $"'{c.Op}' accepts at most {MaxInValues} values.");
            var keys = new List<string>();
            foreach (var v in c.Values)
            {
                // A key is never empty, so there is no (Empty) to ask for: a null here is a client bug.
                if (v.ValueKind != JsonValueKind.String) return (null, $"Field '{c.Field}' values must be strings.");
                keys.Add(v.GetString()!);
            }
            var inList = Expression.Call(Expression.Constant(keys), ListContains<string>(), body);
            return c.Op == FilterOperator.In
                ? (Lambda(inList, p), null)
                : (Lambda(Expression.AndAlso(notNull, Expression.Not(inList)), p), null);
        }

        if (c.Value is not { ValueKind: JsonValueKind.String } sv)
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        var raw = sv.GetString()!;

        if (c.Op == FilterOperator.Eq)   // jiraKey Eq
            return (Lambda(Expression.Equal(body, Expression.Constant(raw, typeof(string))), p), null);

        // Contains / NotContains / StartsWith -> EF.Functions.ILike(body, pattern, "\\") with wildcards escaped.
        var escaped = raw.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var pattern = c.Op == FilterOperator.StartsWith ? escaped + "%" : "%" + escaped + "%";
        var ilike = Expression.Call(ILikeMethod,
            Expression.Constant(EF.Functions),
            body,
            Expression.Constant(pattern, typeof(string)),
            Expression.Constant("\\", typeof(string)));
        // A NULL summary never matches, not even "does not contain": body != null && [NOT] ILike(...)
        Expression match = c.Op == FilterOperator.NotContains ? Expression.Not(ilike) : ilike;
        return (Lambda(Expression.AndAlso(notNull, match), p), null);
    }

    private static (Expression<Func<Issue, bool>>?, string?) BuildParent(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, string?>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];
        if (c.Op == FilterOperator.IsNull) return (Lambda(Expression.Equal(body, NullString), p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(Expression.NotEqual(body, NullString), p), null);
        if (c.Value is not { ValueKind: JsonValueKind.String } sv)
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        return (Lambda(Expression.Equal(body, Expression.Constant(sv.GetString(), typeof(string))), p), null);
    }
```

- [ ] **Step 7: Give the catalog an explicit display order**

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs` replace the class body (keep the file's usings/namespace; update the class doc comment's sentence about ordinal order to point at `DisplayOrder`):

```csharp
public sealed class FilterCatalogService : IFilterCatalogService
{
    /// <summary>
    /// The order the editor offers operators in: equality, lists, comparisons, text matching,
    /// then the two that take no value. Explicit because FilterOperator is append-only, which
    /// makes its declaration order a history rather than a reading order. Every member must
    /// appear here — FilterCatalogServiceTests checks.
    /// </summary>
    internal static readonly FilterOperator[] DisplayOrder =
    {
        FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.NotIn,
        FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Gt, FilterOperator.Gte,
        FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.StartsWith,
        FilterOperator.IsNull, FilterOperator.IsNotNull,
    };

    public FilterCatalogView Get() => new(
        FilterFields.All.Values
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new FilterFieldInfo(
                f.Name,
                f.Type.ToString(),
                f.Ops.OrderBy(o => Array.IndexOf(DisplayOrder, o)).Select(o => o.ToString()).ToList()))
            .ToList(),
        IssueFilterTranslator.MaxConditions,
        IssueFilterTranslator.MaxInValues,
        IssueFilterTranslator.MaxGroups,
        IssueFilterTranslator.MaxDepth);
}
```

- [ ] **Step 8: Run the unit tests to verify they pass**

Run: `cd C:/jw-filters && dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueFilterTranslatorTests|FullyQualifiedName~FilterCatalogServiceTests|FullyQualifiedName~IssueFilterGroupTests" -v q`
Expected: PASS.

- [ ] **Step 9: Update and extend the endpoint tests**

In `tests/JiraUnpacked.Tests/Integration/IssueFilterTests.cs` add a helper next to the other private statics:

```csharp
    private static string[] Keys(PageDto page) =>
        page.Items.Select(i => i.JiraKey).OrderBy(k => k, StringComparer.Ordinal).ToArray();
```

**Replace** `Status_neq_includes_an_unassigned_issue` with:

```csharp
    [Fact]
    public async Task Status_neq_excludes_issues_with_no_status()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("status", "Neq", value: "Done") }));

        // Only ACME-1 has a status other than Done. ACME-3..6 have none, and "is not Done" no
        // longer claims them: empty matches only when asked for.
        Assert.Equal(new[] { "ACME-1" }, Keys(page));
    }
```

**Replace** `StoryPoints_neq_includes_the_null_points_issue` (and its section comment) with the version below — same seeding plus a 3-point issue, opposite expectation:

```csharp
    // ---- storyPoints Neq on a NULL decimal: EF's C# null semantics make a bare `!= 5` match NULLs,
    // ---- so the translator guards it — "is not 5" means "has points, and they are not 5" ----

    [Fact]
    public async Task StoryPoints_neq_excludes_unestimated_issues()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
            await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
            foreach (var (jid, key, summary, points) in new (string, string, string, decimal?)[]
                     { ("10001", "ACME-1", "Has five", 5m), ("10002", "ACME-2", "No points", null), ("10003", "ACME-3", "Has three", 3m) })
            {
                await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StoryPoints"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,@ji,@k,@s,@sp,now(),now(),now())",
                    ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("ji", jid), ("k", key), ("s", summary), ("sp", points));
            }
        }

        var page = await QueryOkAsync(client, projId, Req(new() { Cond("storyPoints", "Neq", value: 5) }));

        Assert.Equal(new[] { "ACME-3" }, Keys(page));   // the NULL-points issue is no longer included
    }
```

Append these endpoint tests (all against the six-issue fixture documented at the top of the file):

```csharp
    [Fact]
    public async Task Status_not_in_excludes_the_listed_values_and_empty()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("status", "NotIn", values: new[] { "Done" }) }));

        Assert.Equal(new[] { "ACME-1" }, Keys(page));
    }

    [Fact]
    public async Task Status_in_with_empty_also_returns_issues_with_no_status()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("status", "In", values: new object?[] { "Done", null }) }));

        Assert.Equal(new[] { "ACME-2", "ACME-3", "ACME-4", "ACME-5", "ACME-6" }, Keys(page));
    }

    [Fact]
    public async Task Assignee_is_not_empty_returns_only_assigned_issues()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId, Req(new() { Cond("assignee", "IsNotNull") }));

        Assert.Equal(new[] { "ACME-1", "ACME-2", "ACME-4", "ACME-5", "ACME-6" }, Keys(page));
    }

    [Fact]
    public async Task Summary_not_contains_is_case_insensitive()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId,
            Req(new() { Cond("summary", "NotContains", value: "TASK") }));

        Assert.Equal(new[] { "ACME-1", "ACME-2", "ACME-3", "ACME-4" }, Keys(page));
    }

    [Fact]
    public async Task JiraKey_in_and_not_in_select_and_exclude_exact_keys()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var inList = await QueryOkAsync(client, projectId,
            Req(new() { Cond("jiraKey", "In", values: new[] { "ACME-1", "ACME-3" }) }));
        var notIn = await QueryOkAsync(client, projectId,
            Req(new() { Cond("jiraKey", "NotIn", values: new[] { "ACME-1", "ACME-3" }) }));

        Assert.Equal(new[] { "ACME-1", "ACME-3" }, Keys(inList));
        Assert.Equal(new[] { "ACME-2", "ACME-4", "ACME-5", "ACME-6" }, Keys(notIn));
    }

    [Fact]
    public async Task Parent_is_not_empty_returns_the_children()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedFilterFixtureAsync(tenant);

        var page = await QueryOkAsync(client, projectId, Req(new() { Cond("parent", "IsNotNull") }));

        Assert.Equal(new[] { "ACME-5", "ACME-6" }, Keys(page));
    }
```

In `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs` replace the two operator-set assertions (`status` and `jiraKey`) with:

```csharp
        var status = catalog.Fields.Single(f => f.Name == "status");
        Assert.True(new HashSet<string> { "Eq", "Neq", "In", "NotIn", "IsNull", "IsNotNull" }.SetEquals(status.Operators));

        var jiraKey = catalog.Fields.Single(f => f.Name == "jiraKey");
        Assert.True(new HashSet<string> { "Eq", "In", "NotIn", "Contains", "StartsWith" }.SetEquals(jiraKey.Operators));

        var storyPoints = catalog.Fields.Single(f => f.Name == "storyPoints");
        Assert.True(new HashSet<string> { "Eq", "Neq", "Lt", "Lte", "Gt", "Gte", "IsNull", "IsNotNull" }.SetEquals(storyPoints.Operators));

        var summary = catalog.Fields.Single(f => f.Name == "summary");
        Assert.True(new HashSet<string> { "Contains", "NotContains", "StartsWith", "IsNull", "IsNotNull" }.SetEquals(summary.Operators));

        var parent = catalog.Fields.Single(f => f.Name == "parent");
        Assert.True(new HashSet<string> { "Eq", "IsNull", "IsNotNull" }.SetEquals(parent.Operators));
```

- [ ] **Step 10: Run the integration tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_filters dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueFilterTests|FullyQualifiedName~FilterCatalogEndpointTests|FullyQualifiedName~IssueFilterGroupEndpointTests|FullyQualifiedName~IssueFilterSecurityTests|FullyQualifiedName~SavedFilterTests" -v q`
Expected: PASS.

- [ ] **Step 11: Run the whole .NET suite** (metrics and gap-checks share the translator; `Neq` changed for them too, on purpose)

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_filters dotnet test JiraUnpacked.sln -v q`
Expected: PASS.

- [ ] **Step 12: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs src/JiraUnpacked.Infrastructure/Reporting/Filtering tests/JiraUnpacked.Tests/Reporting tests/JiraUnpacked.Tests/Integration/IssueFilterTests.cs tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs
git commit -m "feat(filters): NotIn, IsNotNull, NotContains; empty matches only when asked for"
```

---

### Task 2: Disabled conditions and groups (server)

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs`
- Test: `tests/JiraUnpacked.Tests/Reporting/IssueFilterGroupTests.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/IssueFilterGroupEndpointTests.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs`

**Interfaces:**
- Consumes: Task 1's operators.
- Produces: `record FilterCondition(string Field, FilterOperator Op, JsonElement? Value, List<JsonElement>? Values, bool? Disabled = null)` and `record FilterGroup(FilterLogic Logic, List<FilterCondition>? Conditions, List<FilterGroup>? Groups, bool? Disabled = null)`. JSON name `disabled`. Existing 4- and 3-argument constructions keep compiling.
- Produces (semantics): a node with `Disabled == true` is skipped with its whole subtree — not validated, not counted. An enabled group whose every part is disabled is absent. A root that is disabled or has nothing enabled = match all. A group **written** with no parts at all (root or nested) is still the empty-group 400. An enabled group whose parts are all disabled still counts as one group and still obeys the depth limit.

- [ ] **Step 1: Write the failing translator tests**

Append to `tests/JiraUnpacked.Tests/Reporting/IssueFilterGroupTests.cs` (inside the class; `Chain`, `Siblings`, `Group`, `Status` already exist there):

```csharp
    private static FilterCondition Off(FilterCondition c) => c with { Disabled = true };
    private static FilterGroup Off(FilterGroup g) => g with { Disabled = true };

    [Fact]
    public async Task A_disabled_condition_is_skipped_without_being_validated()
    {
        // Unknown field AND a value of the wrong type: either is a 400 if the row is looked at.
        // A row switched off because it is broken must not break the filter.
        var pred = await CompileAsync(new FilterGroup(FilterLogic.And, new List<FilterCondition>
        {
            Status("Done"),
            Off(new FilterCondition("secret", FilterOperator.Eq, J(42), null)),
        }, null));

        Assert.True(pred(Issue(i => i.StatusId = DoneId)));
        Assert.False(pred(Issue(i => i.StatusId = InProgressId)));
    }

    [Fact]
    public async Task Disabled_conditions_do_not_count_toward_the_limit()
    {
        var conditions = Enumerable.Range(0, IssueFilterTranslator.MaxConditions).Select(_ => Status("Done")).ToList();
        conditions.Add(Off(Status("Done")));   // the 21st, switched off

        var r = await Sut.BuildAsync(new FilterGroup(FilterLogic.And, conditions, null), Resolver, CancellationToken.None);

        Assert.Null(r.Error);
    }

    [Fact]
    public async Task A_disabled_group_is_skipped_whole_even_past_the_depth_limit()
    {
        var pred = await CompileAsync(new FilterGroup(FilterLogic.And,
            new List<FilterCondition> { Status("Done") },
            new List<FilterGroup> { Off(Chain(IssueFilterTranslator.MaxDepth + 1)) }));

        Assert.True(pred(Issue(i => i.StatusId = DoneId)));
    }

    [Fact]
    public async Task Disabled_groups_do_not_count_toward_the_group_limit()
    {
        var root = Siblings(IssueFilterTranslator.MaxGroups - 1);   // root + 9 = at the limit
        root.Groups!.Add(Off(Group(FilterLogic.And, Status("Done"))));

        var r = await Sut.BuildAsync(root, Resolver, CancellationToken.None);

        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData(FilterLogic.And)]
    [InlineData(FilterLogic.Or)]
    public async Task A_group_whose_parts_are_all_disabled_is_treated_as_absent(FilterLogic inner)
    {
        // Kept as an empty group it would be vacuously true under And (an outer Or would then
        // match everything) or vacuously false under Or (an outer And would match nothing).
        // Absent, the outer group means exactly its other parts.
        var allOff = new FilterGroup(inner, new List<FilterCondition> { Off(Status("In Progress")) }, null);

        var underOr = await CompileAsync(new FilterGroup(FilterLogic.Or,
            new List<FilterCondition> { Status("Done") }, new List<FilterGroup> { allOff }));
        Assert.True(underOr(Issue(i => i.StatusId = DoneId)));
        Assert.False(underOr(Issue(i => i.StatusId = InProgressId)));

        var underAnd = await CompileAsync(new FilterGroup(FilterLogic.And,
            new List<FilterCondition> { Status("Done") }, new List<FilterGroup> { allOff }));
        Assert.True(underAnd(Issue(i => i.StatusId = DoneId)));
        Assert.False(underAnd(Issue(i => i.StatusId = InProgressId)));
    }

    [Theory]
    [InlineData(FilterLogic.And)]
    [InlineData(FilterLogic.Or)]
    public async Task A_root_with_nothing_enabled_matches_every_issue(FilterLogic logic)
    {
        var pred = await CompileAsync(new FilterGroup(logic,
            new List<FilterCondition> { Off(Status("Done")) },
            new List<FilterGroup> { Off(Group(FilterLogic.And, Status("Done"))) }));

        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
        Assert.True(pred(Issue(i => i.StatusId = null)));
    }

    [Fact]
    public async Task A_disabled_root_matches_every_issue()
    {
        var pred = await CompileAsync(Off(Group(FilterLogic.And, Status("Done"))));

        Assert.True(pred(Issue(i => i.StatusId = InProgressId)));
    }

    [Fact]
    public async Task A_nested_group_written_with_no_parts_is_still_rejected()
    {
        // Switched-off parts are a choice; a group with nothing in it at all is a client bug.
        var error = await ErrorAsync(new FilterGroup(FilterLogic.And,
            new List<FilterCondition> { Off(Status("Done")) },
            new List<FilterGroup> { new(FilterLogic.Or, null, null) }));

        Assert.Equal("A filter group must contain at least one condition or nested group.", error);
    }

    [Fact]
    public async Task The_flat_conditions_path_skips_disabled_rows_too()
    {
        var r = await Sut.BuildAsync(new[] { Off(Status("Done")) }, Resolver, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.True(r.Predicate!.Compile()(Issue(i => i.StatusId = InProgressId)));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd C:/jw-filters && dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueFilterGroupTests" -v q`
Expected: build FAILS — `FilterCondition`/`FilterGroup` have no `Disabled`.

- [ ] **Step 3: Add the flag**

In `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs` replace the two records (keep the doc comments above them, adding the `Disabled` sentence):

```csharp
/// <summary>
/// One filter condition. Value for single-value ops; Values for In/NotIn, where a JSON null
/// element stands for (Empty). Both null for IsNull/IsNotNull. Disabled = kept in the definition
/// but skipped entirely: not validated, not counted, not applied.
/// </summary>
public sealed record FilterCondition(
    string Field, FilterOperator Op, JsonElement? Value, List<JsonElement>? Values, bool? Disabled = null);

/// <summary>
/// A boolean group: its conditions and its nested groups are all joined by <see cref="Logic"/>.
/// A group must be WRITTEN with at least one part — an empty And is vacuously true and an empty
/// Or vacuously false, so an empty group is rejected rather than guessed at. Parts switched off
/// with <see cref="Disabled"/> are a different thing: a group whose every part is off is simply
/// absent, and a root with nothing on matches everything.
/// </summary>
public sealed record FilterGroup(
    FilterLogic Logic, List<FilterCondition>? Conditions, List<FilterGroup>? Groups, bool? Disabled = null);
```

- [ ] **Step 4: Skip disabled nodes in the translator**

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` replace the group overload of `BuildAsync` and `BuildGroupAsync`:

```csharp
    public async Task<FilterBuildResult> BuildAsync(
        FilterGroup group, DictNameResolver resolveDict, CancellationToken ct = default)
    {
        // A root switched off — or one whose every part is switched off — is "no filter": the
        // user turned the parts off on purpose. A group WRITTEN empty is still refused below.
        if (group.Disabled == true) return new FilterBuildResult(MatchAll, null);

        var (predicate, error) = await BuildGroupAsync(group, resolveDict, new Budget(), 1, ct);
        return error is not null ? Err(error) : new FilterBuildResult(predicate ?? MatchAll, null);
    }

    /// <summary>
    /// A null Predicate with a null Error means every part of this group is switched off: the
    /// group is absent, and its parent joins only its other parts.
    /// </summary>
    private async Task<(Expression<Func<Issue, bool>>? Predicate, string? Error)> BuildGroupAsync(
        FilterGroup group, DictNameResolver resolveDict, Budget budget, int depth, CancellationToken ct)
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

            var (predicate, error) = await BuildConditionAsync(c, resolveDict, ct);
            if (error is not null) return (null, error);
            parts.Add(predicate!);
        }

        foreach (var g in groups.Where(g => g.Disabled != true))
        {
            var (predicate, error) = await BuildGroupAsync(g, resolveDict, budget, depth + 1, ct);
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
```

- [ ] **Step 5: Run the unit tests**

Run: `cd C:/jw-filters && dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueFilterGroupTests|FullyQualifiedName~IssueFilterTranslatorTests" -v q`
Expected: PASS.

- [ ] **Step 6: Endpoint and saved-filter tests**

Append to `tests/JiraUnpacked.Tests/Integration/IssueFilterGroupEndpointTests.cs`:

```csharp
    [Fact]
    public async Task Disabled_parts_are_left_out_of_the_query()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        // Were the switched-off arm applied, Bob's three issues would join ACME-1.
        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(new
        {
            logic = "Or",
            conditions = new object[]
            {
                Cond("status", "Eq", value: "In Progress"),
                new { field = "assignee", op = "Eq", value = "Bob", disabled = true },
            },
        }));

        Assert.Equal(new[] { "ACME-1" }, Keys(page));
    }

    [Fact]
    public async Task A_filter_with_everything_switched_off_returns_every_issue()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(new
        {
            logic = "And",
            conditions = new object[] { new { field = "status", op = "Eq", value = "Done", disabled = true } },
        }));

        Assert.Equal(6, page.TotalCount);
    }
```

Append to `tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs`:

```csharp
    [Fact]
    public async Task Disabled_parts_are_saved_unvalidated_and_come_back_disabled()
    {
        var (client, projectId) = await SeedAsync();

        // The switched-off row names a field that does not exist. Validating it would make it
        // impossible to save a view with a broken row turned off — sub-project 2 depends on this.
        var created = await CreateOkAsync(client, projectId, new
        {
            name = "Partly off",
            filter = new
            {
                logic = "And",
                conditions = new object[]
                {
                    new { field = "status", op = "Eq", value = "Done" },
                    new { field = "nope", op = "Eq", value = "x", disabled = true },
                },
                groups = new[]
                {
                    new
                    {
                        logic = "Or",
                        disabled = true,
                        conditions = new[] { new { field = "assignee", op = "Eq", value = "Bob" } },
                    },
                },
            },
        });

        Assert.Null(created.Filter!.Conditions![0].Disabled);
        Assert.True(created.Filter.Conditions[1].Disabled);
        Assert.True(created.Filter.Groups!.Single().Disabled);
        // jsonb::text renders `"key": value` with a space after the colon.
        Assert.Contains("\"disabled\": true", await ReadDefinitionAsync(created.Id));
    }
```

In `IssueFilterGroupEndpointTests`, `Keys` already exists. Nothing in `SavedFilterService` changes: it validates through `IIssueFilterCompiler`, which now skips disabled parts, and `DefinitionJson` (`WhenWritingNull`) omits `disabled` when it is null.

- [ ] **Step 7: Run the integration tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_filters dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueFilterGroupEndpointTests|FullyQualifiedName~SavedFilterTests" -v q`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs tests/JiraUnpacked.Tests
git commit -m "feat(filters): switch conditions and groups off without deleting them"
```

---

### Task 3: Value counts and the empty count on `filter-values`

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/IFilterValueService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs` (the `FilterValues` action only)
- Test: `tests/JiraUnpacked.Tests/Integration/FilterValuesTests.cs`

**Interfaces:**
- Produces: `record FilterValueCount(string Name, int Count)`; `record FilterValuesResult(IReadOnlyList<FilterValueCount>? Values, int EmptyCount, string? Error)`.
- Produces (HTTP): `GET /api/projects/{id}/filter-values?field=` → `200 { "field": "status", "values": [ { "name": "Backlog", "count": 42 } ], "emptyCount": 7 }`, values ordered by name, capped at 1000; counts over the whole project. 400/404 unchanged.

- [ ] **Step 1: Write the failing tests**

In `tests/JiraUnpacked.Tests/Integration/FilterValuesTests.cs` replace the `ValuesResponse` record with:

```csharp
    private sealed record ValueDto(string Name, int Count);
    private sealed record ValuesResponse(string Field, List<ValueDto> Values, int EmptyCount);
```

Update the three existing assertions to read names:

```csharp
        Assert.Equal(new[] { "To Do" }, body.Values.Select(v => v.Name));
```
```csharp
        Assert.Contains("Mine Only", body.Values.Select(v => v.Name));
        Assert.DoesNotContain("Other Project Only", body.Values.Select(v => v.Name));
```
```csharp
        Assert.Contains("Belongs To A", body.Values.Select(v => v.Name));
        Assert.DoesNotContain("Belongs To B", body.Values.Select(v => v.Name));
```

Add:

```csharp
    [Fact]
    public async Task Counts_each_value_and_the_issues_that_have_none()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var at = DateTime.UtcNow;
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.ToDoStatusId, null, at);
        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "b", ctx.ToDoStatusId, null, at);
        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "c", ctx.DoneStatusId, null, at);
        await MetricsTestData.SeedIssueAsync(ctx, "10004", "ACME-4", "d", null, null, at);
        await MetricsTestData.SeedIssueAsync(ctx, "10005", "ACME-5", "e", null, null, at);

        var status = await GetValuesAsync(client, ctx.ProjectId, "status");

        Assert.Equal(new[] { new ValueDto("Done", 1), new ValueDto("To Do", 2) }, status.Values);
        Assert.Equal(2, status.EmptyCount);

        // No issue here has an assignee: nothing to pick, and every issue is behind (Empty).
        var assignee = await GetValuesAsync(client, ctx.ProjectId, "assignee");

        Assert.Empty(assignee.Values);
        Assert.Equal(5, assignee.EmptyCount);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_filters dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~FilterValuesTests" -v q`
Expected: FAIL — `values` is a string array (JSON deserialisation of `ValueDto` fails).

- [ ] **Step 3: Change the result record**

Replace the body of `src/JiraUnpacked.Domain/Abstractions/IFilterValueService.cs` (keep the namespace):

```csharp
/// <summary>One pickable value and how many of this project's issues carry it.</summary>
public sealed record FilterValueCount(string Name, int Count);

/// <summary>
/// Values + null Error => 200. Null Values + Error => 400. Both null => the project is unknown
/// or belongs to another tenant => 404. EmptyCount is how many of the project's issues have no
/// value for the field — the number behind the picker's (Empty) entry.
/// </summary>
public sealed record FilterValuesResult(IReadOnlyList<FilterValueCount>? Values, int EmptyCount, string? Error);

/// <summary>
/// The values a dictionary field actually takes in one project, with counts, for the filter's
/// value picker. Counts are over the whole project, not the current filter: cheap, stable while
/// the user edits, and honest about what exists.
/// </summary>
public interface IFilterValueService
{
    Task<FilterValuesResult> GetAsync(Guid projectId, string field, CancellationToken ct = default);
}
```

- [ ] **Step 4: Count in the service**

In `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs` add `using JiraUnpacked.Domain.Entities;` and replace `GetAsync`:

```csharp
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
```

- [ ] **Step 5: Shape the response**

In `IssuesController.FilterValues` replace the last line:

```csharp
        return result.Values is null
            ? NotFound()
            : Ok(new { field, values = result.Values, emptyCount = result.EmptyCount });
```

- [ ] **Step 6: Run the tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_filters dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~FilterValuesTests" -v q`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain/Abstractions/IFilterValueService.cs src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs src/JiraUnpacked.Api/Controllers/IssuesController.cs tests/JiraUnpacked.Tests/Integration/FilterValuesTests.cs
git commit -m "feat(filters): count each picker value and the issues with none"
```

---

### Task 4: CSV export takes a filter tree; end-to-end nested query

**Files:**
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs` (the `Export` action)
- Test: `tests/JiraUnpacked.Tests/Integration/IssueExportTests.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/IssueFilterGroupEndpointTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2 (operators, `disabled`).
- Produces (HTTP): `GET …/issues/export.csv?filter=` — first non-whitespace char `{` → a `FilterGroup` (the `logic` param is ignored); otherwise a `FilterCondition[]` joined by `logic` as before. Unreadable JSON of either shape → 400 "The filter in the link could not be read."

- [ ] **Step 1: Write the failing tests**

Append to `tests/JiraUnpacked.Tests/Integration/IssueExportTests.cs`:

```csharp
    [Fact]
    public async Task Exports_a_filter_tree_and_ignores_the_legacy_logic_param()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        for (var i = 0; i < 3; i++) await Seed(ctx, $"DONE-{i}", ctx.DoneStatusId);
        for (var i = 0; i < 2; i++) await Seed(ctx, $"TODO-{i}", ctx.ToDoStatusId);
        await MetricsTestData.SeedIssueAsync(ctx, "90001", "NONE-0", "no status", null, null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Done OR (Empty), with a switched-off To Do arm that must stay out. `logic=And` belongs
        // to the old array format; applied to this tree it would turn the Or into an And and
        // export nothing.
        var tree = """{"logic":"Or","conditions":[{"field":"status","op":"Eq","value":"Done"},{"field":"status","op":"In","values":[null]},{"field":"status","op":"Eq","value":"To Do","disabled":true}]}""";

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?filter={Uri.EscapeDataString(tree)}&logic=And&cols=jiraKey");

        var keys = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(new[] { "DONE-0", "DONE-1", "DONE-2", "NONE-0" }, keys);
    }

    [Fact]
    public async Task A_legacy_array_filter_still_honours_its_logic_param()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        for (var i = 0; i < 3; i++) await Seed(ctx, $"DONE-{i}", ctx.DoneStatusId);
        for (var i = 0; i < 2; i++) await Seed(ctx, $"TODO-{i}", ctx.ToDoStatusId);

        var legacy = """[{"field":"status","op":"Eq","value":"Done"},{"field":"status","op":"Eq","value":"To Do"}]""";

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?filter={Uri.EscapeDataString(legacy)}&logic=Or&cols=jiraKey");

        Assert.Equal(5 + 1, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);   // + header
    }
```

Append to `tests/JiraUnpacked.Tests/Integration/IssueFilterGroupEndpointTests.cs`:

```csharp
    [Fact]
    public async Task Three_levels_with_not_in_and_explicit_empty_select_exactly_the_intended_rows()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);

        //     assignee NotIn [Alice]                                             -> ACME-4, 5, 6 (ACME-3 has none: excluded)
        // AND ( parent IsNotNull                                                 -> ACME-5, 6
        //       OR ( summary NotContains "epic" AND status In [(Empty)] ) )      -> ACME-3, 5, 6
        var page = await FilterTestData.QueryOkAsync(client, projectId, Req(new
        {
            logic = "And",
            conditions = new[] { Cond("assignee", "NotIn", values: new[] { "Alice" }) },
            groups = new[]
            {
                new
                {
                    logic = "Or",
                    conditions = new[] { Cond("parent", "IsNotNull") },
                    groups = new[]
                    {
                        Group("And", new[]
                        {
                            Cond("summary", "NotContains", value: "epic"),
                            Cond("status", "In", values: new object?[] { null }),
                        }),
                    },
                },
            },
        }));

        // ACME-4 is Bob's but is the epic and has no parent; ACME-3 fits the inner arm but has
        // no assignee, and "is none of" never matches an empty field.
        Assert.Equal(new[] { "ACME-5", "ACME-6" }, Keys(page));
    }
```

- [ ] **Step 2: Run to verify the export test fails**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_filters dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueExportTests|FullyQualifiedName~IssueFilterGroupEndpointTests" -v q`
Expected: `Exports_a_filter_tree_and_ignores_the_legacy_logic_param` FAILS with a 400 (an object is not a `List<FilterCondition>`). The other new tests PASS already (they exercise Tasks 1–2).

- [ ] **Step 3: Read either shape**

In `IssuesController.Export` replace everything from `List<FilterCondition>? conditions;` down to and including `var request = new IssueFilterRequest(...)` with:

```csharp
        FilterGroup? group;
        try
        {
            group = ReadExportFilter(filter, logic);
        }
        catch (JsonException)
        {
            // A URL is user-editable and chat clients truncate them. Say so rather than 500.
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "The filter in the link could not be read.");
        }

        var request = new IssueFilterRequest(null, group, 1, 1, sortBy, sortDescending);
```

and add this method to the controller:

```csharp
    /// <summary>
    /// `filter` in either shape a link may carry, told apart by its first character. A JSON
    /// object is the boolean tree the SPA writes now, carrying its own logic — so `logic` is
    /// ignored for it. A JSON array is the flat list links carried before groups existed,
    /// joined by `logic` exactly as before.
    /// </summary>
    private static FilterGroup? ReadExportFilter(string? filter, string? logic)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;

        if (filter.TrimStart().StartsWith('{'))
            return JsonSerializer.Deserialize<FilterGroup>(filter, FilterJson);

        var conditions = JsonSerializer.Deserialize<List<FilterCondition>>(filter, FilterJson);
        return conditions is { Count: > 0 }
            ? new FilterGroup(logic == "Or" ? FilterLogic.Or : FilterLogic.And, conditions, null)
            : null;
    }
```

Also update the `Export` summary comment's first sentence to mention that `filter` may be a tree or a legacy list.

- [ ] **Step 4: Run the tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_filters dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueExportTests|FullyQualifiedName~IssueFilterGroupEndpointTests" -v q`
Expected: PASS (including the existing `Refuses_an_unreadable_filter_rather_than_failing_with_a_500`, whose `{not-json` now fails on the object path).

- [ ] **Step 5: Run the whole .NET suite**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_filters dotnet test JiraUnpacked.sln -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Api/Controllers/IssuesController.cs tests/JiraUnpacked.Tests/Integration/IssueExportTests.cs tests/JiraUnpacked.Tests/Integration/IssueFilterGroupEndpointTests.cs
git commit -m "feat(export): accept a filter tree; legacy flat links keep their logic"
```

---

### Task 5: SPA filter-tree model

**Files:**
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts` (types only)
- Create: `src/JiraUnpacked.Web/src/features/issues/filterTree.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/filterTree.test.ts`
- Create: `src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts`

**Interfaces:**
- Produces (types in `api/issues.ts`): `FilterOperator` adds `'NotIn' | 'IsNotNull' | 'NotContains'`; `FilterCondition { field; op; value?: string | number | null; values?: (string | number | null)[]; disabled?: boolean }`; `FilterGroup { logic; conditions?; groups?; disabled?: boolean }`.
- Produces (`filterTree.ts`): `EditorCondition` (= `FilterCondition & { id: string }`), `EditorGroup { id; logic; conditions: EditorCondition[]; groups: EditorGroup[]; disabled?: boolean }`, `TreeCounts { conditions: number; groups: number }`, and functions `newId(): string`, `emptyRoot(): EditorGroup`, `blankCondition(): EditorCondition` (status / Eq / `''`), `blankGroup(): EditorGroup` (And + one blank condition), `isEmptyTree(g): boolean`, `takesNoValue(op): boolean`, `takesList(op): boolean`, `isCompleteCondition(c: FilterCondition): boolean`, `toWireCondition(c: FilterCondition): FilterCondition`, `pruneTree(root: EditorGroup, knownFields?: ReadonlySet<string>): FilterGroup | undefined`, `countEnabled(g: EditorGroup): TreeCounts`, `cloneCondition(c): EditorCondition`, `cloneGroup(g): EditorGroup`, `toggleDisabled<T extends { disabled?: boolean }>(node: T): T`, `withIdentityOf(row: EditorCondition, next: FilterCondition): EditorCondition`, `insertAfter<T extends { id: string }>(list: T[], id: string, item: T): T[]`, `toUrlJson(root: EditorGroup): string | null`, `parseFilterParam(raw: string | null, legacyLogic: string | null): EditorGroup`.
- Produces: `FILTER_CATALOG: FilterCatalog` in `src/test/filterCatalogFixture.ts`.

- [ ] **Step 1: Widen the wire types**

In `src/JiraUnpacked.Web/src/api/issues.ts` replace the `FilterOperator` type, the `FilterCondition` interface and the `FilterGroup` interface:

```ts
export type FilterOperator =
  | 'Eq' | 'Neq' | 'In' | 'NotIn' | 'Lt' | 'Lte' | 'Gt' | 'Gte'
  | 'Contains' | 'NotContains' | 'StartsWith' | 'IsNull' | 'IsNotNull';
```

```ts
/**
 * Mirrors the API's FilterCondition. `value` and `values` are mutually
 * exclusive: single-value operators send `value`, `In`/`NotIn` send `values`,
 * and `IsNull`/`IsNotNull` send neither. Inside `values`, `null` is (Empty).
 * A `value` of null exists only in the editor (Eq/Neq on (Empty)); pruneTree
 * turns it into IsNull/IsNotNull before anything is sent. `disabled` parts are
 * kept in the URL and skipped by the server.
 */
export interface FilterCondition {
  field: string;
  op: FilterOperator;
  value?: string | number | null;
  values?: (string | number | null)[];
  disabled?: boolean;
}

/**
 * A boolean group, sent as `filter` instead of `conditions` (never both — the
 * server returns 400 rather than merging them). A group must be written with
 * at least one part; parts switched off with `disabled` are skipped, and a
 * group whose every part is off is treated as absent.
 */
export interface FilterGroup {
  logic: FilterLogic;
  conditions?: FilterCondition[];
  groups?: FilterGroup[];
  disabled?: boolean;
}
```

Also delete the now-stale sentence in the `FilterCatalog.maxGroups` doc comment ("The builder below sends flat `conditions` … nothing here is read yet …") and replace it with: `Guards on the boolean-group shape; the tree editor sizes itself from these.`

- [ ] **Step 2: Add the test catalog fixture**

Create `src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts`:

```ts
import type { FilterCatalog } from '../api/issues';

/**
 * GET /api/filter-catalog as the server serves it after filter builder v2: every field's
 * operators in FilterCatalogService.DisplayOrder. Field order here is the tests' own (status
 * first); the server sorts fields by name, which nothing in the editor depends on.
 */
const DICT = ['Eq', 'Neq', 'In', 'NotIn', 'IsNull', 'IsNotNull'] as const;

export const FILTER_CATALOG: FilterCatalog = {
  fields: [
    { name: 'status', type: 'DictName', operators: [...DICT] },
    { name: 'issueType', type: 'DictName', operators: [...DICT] },
    { name: 'priority', type: 'DictName', operators: [...DICT] },
    { name: 'assignee', type: 'DictName', operators: [...DICT] },
    { name: 'sprint', type: 'DictName', operators: [...DICT] },
    { name: 'team', type: 'DictName', operators: [...DICT] },
    { name: 'storyPoints', type: 'Decimal', operators: ['Eq', 'Neq', 'Lt', 'Lte', 'Gt', 'Gte', 'IsNull', 'IsNotNull'] },
    { name: 'createdAt', type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
    { name: 'updatedAt', type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
    { name: 'summary', type: 'Text', operators: ['Contains', 'NotContains', 'StartsWith', 'IsNull', 'IsNotNull'] },
    { name: 'jiraKey', type: 'Text', operators: ['Eq', 'In', 'NotIn', 'Contains', 'StartsWith'] },
    { name: 'parent', type: 'ParentRef', operators: ['Eq', 'IsNull', 'IsNotNull'] },
  ],
  maxConditions: 20,
  maxInValues: 100,
  maxGroups: 10,
  maxDepth: 3,
};
```

- [ ] **Step 3: Write the failing model tests**

Create `src/JiraUnpacked.Web/src/features/issues/filterTree.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import {
  blankGroup, cloneGroup, countEnabled, emptyRoot, insertAfter, parseFilterParam, pruneTree,
  toUrlJson, toggleDisabled, withIdentityOf,
  type EditorCondition, type EditorGroup,
} from './filterTree';

const cond = (id: string, rest: Omit<EditorCondition, 'id'>): EditorCondition => ({ id, ...rest });
const group = (id: string, rest: Partial<EditorGroup> = {}): EditorGroup =>
  ({ id, logic: 'And', conditions: [], groups: [], ...rest });

describe('pruneTree', () => {
  it('sends only enabled, finished conditions, without ids', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'status', op: 'Eq', value: 'Done' }),
        cond('b', { field: 'status', op: 'Eq', value: '' }),
        cond('c', { field: 'assignee', op: 'In', values: [] }),
        cond('d', { field: 'priority', op: 'Eq', value: 'High', disabled: true }),
      ],
    });

    expect(pruneTree(tree)).toEqual({ logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] });
  });

  it('drops disabled groups and groups that pruning empties, at any depth', () => {
    const tree = group('r', {
      logic: 'Or',
      conditions: [cond('a', { field: 'summary', op: 'Contains', value: 'x' })],
      groups: [
        group('g1', { disabled: true, conditions: [cond('b', { field: 'status', op: 'IsNull' })] }),
        group('g2', { conditions: [cond('c', { field: 'status', op: 'Eq', value: '' })] }),
        group('g3', { groups: [group('g4', { conditions: [cond('d', { field: 'status', op: 'IsNotNull' })] })] }),
      ],
    });

    expect(pruneTree(tree)).toEqual({
      logic: 'Or',
      conditions: [{ field: 'summary', op: 'Contains', value: 'x' }],
      groups: [{ logic: 'And', groups: [{ logic: 'And', conditions: [{ field: 'status', op: 'IsNotNull' }] }] }],
    });
  });

  it('is undefined when nothing is left, so no filter is sent at all', () => {
    expect(pruneTree(emptyRoot())).toBeUndefined();
    expect(pruneTree(group('r', { disabled: true, conditions: [cond('a', { field: 'status', op: 'IsNull' })] })))
      .toBeUndefined();
    expect(pruneTree(group('r', { conditions: [cond('a', { field: 'status', op: 'IsNull', disabled: true })] })))
      .toBeUndefined();
  });

  it('turns (Empty) on is / is not into is empty / is not empty', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'sprint', op: 'Eq', value: null }),
        cond('b', { field: 'team', op: 'Neq', value: null }),
      ],
    });

    expect(pruneTree(tree)?.conditions).toEqual([
      { field: 'sprint', op: 'IsNull' },
      { field: 'team', op: 'IsNotNull' },
    ]);
  });

  it('keeps (Empty) inside an is-any-of list', () => {
    const tree = group('r', { conditions: [cond('a', { field: 'sprint', op: 'In', values: ['S1', null] })] });

    expect(pruneTree(tree)?.conditions).toEqual([{ field: 'sprint', op: 'In', values: ['S1', null] }]);
  });

  it('sends only the key its operator takes', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'status', op: 'IsNull', value: 'stale' }),
        cond('b', { field: 'status', op: 'In', value: 'stale', values: ['Done'] }),
      ],
    });

    expect(pruneTree(tree)?.conditions).toEqual([
      { field: 'status', op: 'IsNull' },
      { field: 'status', op: 'In', values: ['Done'] },
    ]);
  });

  it('leaves out a field the catalog no longer has', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'retired', op: 'Eq', value: 'x' }),
        cond('b', { field: 'status', op: 'Eq', value: 'Done' }),
      ],
    });

    expect(pruneTree(tree, new Set(['status']))?.conditions)
      .toEqual([{ field: 'status', op: 'Eq', value: 'Done' }]);
    // Before the catalog has loaded there is nothing to check against; the server is the backstop.
    expect(pruneTree(tree)?.conditions).toHaveLength(2);
  });
});

describe('countEnabled', () => {
  it('counts like the server: whole tree, root included, disabled subtrees not at all', () => {
    const tree = group('r', {
      conditions: [cond('a', { field: 'status', op: 'IsNull' }), cond('b', { field: 'status', op: 'IsNull', disabled: true })],
      groups: [
        group('g1', { conditions: [cond('c', { field: 'status', op: 'IsNull' })] }),
        group('g2', { disabled: true, conditions: [cond('d', { field: 'status', op: 'IsNull' })], groups: [group('g3')] }),
      ],
    });

    expect(countEnabled(tree)).toEqual({ conditions: 2, groups: 2 });
  });
});

describe('copying and identity', () => {
  it('cloneGroup deep-copies with fresh ids and no shared arrays', () => {
    const original = group('g', {
      conditions: [cond('a', { field: 'status', op: 'In', values: ['Done'] })],
      groups: [blankGroup()],
    });

    const copy = cloneGroup(original);

    expect(copy.id).not.toBe(original.id);
    expect(copy.conditions[0].id).not.toBe('a');
    expect(copy.groups[0].id).not.toBe(original.groups[0].id);
    expect(copy.conditions[0].values).not.toBe(original.conditions[0].values);
    expect(copy.conditions[0].values).toEqual(['Done']);
  });

  it('toggleDisabled switches a node off and back on without leaving a stray key', () => {
    const off = toggleDisabled(cond('a', { field: 'status', op: 'IsNull' }));
    expect(off.disabled).toBe(true);
    expect('disabled' in toggleDisabled(off)).toBe(false);
  });

  it('withIdentityOf puts a rebuilt row back with its id and on/off state', () => {
    const row = cond('a', { field: 'status', op: 'Eq', value: 'Done', disabled: true });
    expect(withIdentityOf(row, { field: 'status', op: 'Neq', value: 'Done' }))
      .toEqual({ id: 'a', field: 'status', op: 'Neq', value: 'Done', disabled: true });
  });

  it('insertAfter places the copy directly after the original', () => {
    expect(insertAfter([{ id: 'a' }, { id: 'b' }], 'a', { id: 'c' }).map((x) => x.id)).toEqual(['a', 'c', 'b']);
  });
});

describe('toUrlJson / parseFilterParam', () => {
  it('keeps disabled parts in the URL and strips ids', () => {
    const tree = group('r', {
      conditions: [cond('a', { field: 'status', op: 'Eq', value: 'Done', disabled: true })],
      groups: [group('g', { logic: 'Or', conditions: [cond('b', { field: 'sprint', op: 'In', values: [null] })] })],
    });

    const json = toUrlJson(tree)!;

    expect(json).not.toContain('"id"');
    expect(JSON.parse(json)).toEqual({
      logic: 'And',
      conditions: [{ field: 'status', op: 'Eq', value: 'Done', disabled: true }],
      groups: [{ logic: 'Or', conditions: [{ field: 'sprint', op: 'In', values: [null] }] }],
    });
  });

  it('writes nothing for an empty tree', () => {
    expect(toUrlJson(emptyRoot())).toBeNull();
  });

  it('round-trips a tree through the URL form', () => {
    const json = '{"logic":"Or","conditions":[{"field":"status","op":"NotIn","values":["Done",null]}],'
      + '"groups":[{"logic":"And","conditions":[{"field":"storyPoints","op":"Gte","value":3}],"disabled":true}]}';

    expect(toUrlJson(parseFilterParam(json, null))).toBe(json);
  });

  it('reads a legacy flat list with its old logic param', () => {
    const tree = parseFilterParam('[{"field":"status","op":"Eq","value":"Backlog"}]', 'Or');

    expect(tree.logic).toBe('Or');
    expect(tree.conditions).toEqual([expect.objectContaining({ field: 'status', op: 'Eq', value: 'Backlog' })]);
    expect(tree.groups).toEqual([]);
  });

  it('treats malformed or truncated JSON as no filter', () => {
    for (const raw of ['{not-json', '"a string"', '42', 'null']) {
      const tree = parseFilterParam(raw, null);
      expect(tree.conditions).toEqual([]);
      expect(tree.groups).toEqual([]);
    }
  });

  it('drops parts that are not conditions rather than failing the whole link', () => {
    const tree = parseFilterParam(
      '{"logic":"And","conditions":[{"field":"status"},{"op":"Eq"},{"field":"status","op":"Bogus"},'
      + '{"field":"status","op":"IsNull"}],"groups":[5]}', null);

    expect(tree.conditions.map((c) => c.op)).toEqual(['IsNull']);
    expect(tree.groups).toEqual([]);
  });

  it('gives every parsed node its own id', () => {
    const tree = parseFilterParam(
      '{"logic":"And","conditions":[{"field":"status","op":"IsNull"},{"field":"status","op":"IsNull"}]}', null);

    expect(new Set([tree.id, ...tree.conditions.map((c) => c.id)]).size).toBe(3);
  });
});
```

- [ ] **Step 4: Run to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm ci && npx vitest run src/features/issues/filterTree.test.ts`
Expected: FAIL — `./filterTree` does not exist.

- [ ] **Step 5: Implement the model**

Create `src/JiraUnpacked.Web/src/features/issues/filterTree.ts`:

```ts
import type { FilterCondition, FilterGroup, FilterLogic, FilterOperator } from '../../api/issues';

/**
 * The editor's working copy of a filter: the wire FilterGroup plus a client-only `id` on every
 * node. Rows are keyed by it, so a row keeps its identity — focus, a half-typed chip — when a
 * sibling above it is removed or duplicated. It never leaves the browser: toUrlJson and
 * pruneTree both strip it.
 */
export interface EditorCondition extends FilterCondition {
  id: string;
}

export interface EditorGroup {
  id: string;
  logic: FilterLogic;
  conditions: EditorCondition[];
  groups: EditorGroup[];
  disabled?: boolean;
}

export interface TreeCounts {
  conditions: number;
  groups: number;
}

const OPERATORS: readonly FilterOperator[] = [
  'Eq', 'Neq', 'In', 'NotIn', 'Lt', 'Lte', 'Gt', 'Gte',
  'Contains', 'NotContains', 'StartsWith', 'IsNull', 'IsNotNull',
];

let lastId = 0;
/** Unique for the page's lifetime, which is all a React key needs. */
export const newId = (): string => `f${(lastId += 1)}`;

export const emptyRoot = (): EditorGroup => ({ id: newId(), logic: 'And', conditions: [], groups: [] });

/** What "Add condition" creates. Status is the field people filter on first. */
export const blankCondition = (): EditorCondition => ({ id: newId(), field: 'status', op: 'Eq', value: '' });

/** "Add group" seeds one blank row, so a new group is never an (invalid) empty one. */
export const blankGroup = (): EditorGroup => ({
  id: newId(), logic: 'And', conditions: [blankCondition()], groups: [],
});

export const isEmptyTree = (g: EditorGroup): boolean => g.conditions.length === 0 && g.groups.length === 0;

export const takesNoValue = (op: FilterOperator): boolean => op === 'IsNull' || op === 'IsNotNull';
export const takesList = (op: FilterOperator): boolean => op === 'In' || op === 'NotIn';

// ---- sending ----

/**
 * A condition the user has started but not finished is unfinished, not invalid: it is left out
 * of the request rather than sent to earn a 400 mid-edit. A null `value` is (Empty) and complete.
 */
export function isCompleteCondition(c: FilterCondition): boolean {
  if (takesNoValue(c.op)) return true;
  if (takesList(c.op)) return (c.values?.length ?? 0) > 0;
  return c.value !== undefined && c.value !== '';
}

/** One condition as the server takes it: only its operator's key, (Empty) on is/is not as is empty/is not empty. */
export function toWireCondition(c: FilterCondition): FilterCondition {
  if ((c.op === 'Eq' || c.op === 'Neq') && c.value === null) {
    return { field: c.field, op: c.op === 'Eq' ? 'IsNull' : 'IsNotNull' };
  }
  if (takesNoValue(c.op)) return { field: c.field, op: c.op };
  if (takesList(c.op)) return { field: c.field, op: c.op, values: [...(c.values ?? [])] };
  return { field: c.field, op: c.op, value: c.value };
}

/**
 * What the server is sent: disabled parts, unfinished rows and rows on a field the catalog no
 * longer has are dropped, groups left empty by that go too, and ids go. Undefined when nothing
 * is left — the server refuses an empty group, and "no filter" is what the user has.
 *
 * `knownFields` is optional because the catalog may still be loading; until it arrives the
 * field check is skipped and the server's own validation is the backstop.
 */
export function pruneTree(root: EditorGroup, knownFields?: ReadonlySet<string>): FilterGroup | undefined {
  if (root.disabled) return undefined;
  const conditions = root.conditions
    .filter((c) => !c.disabled && isCompleteCondition(c) && (!knownFields || knownFields.has(c.field)))
    .map(toWireCondition);
  const groups = root.groups
    .map((g) => pruneTree(g, knownFields))
    .filter((g): g is FilterGroup => g !== undefined);
  if (conditions.length === 0 && groups.length === 0) return undefined;
  return {
    logic: root.logic,
    ...(conditions.length > 0 ? { conditions } : {}),
    ...(groups.length > 0 ? { groups } : {}),
  };
}

// ---- counting (matches the server's budget) ----

/**
 * Enabled conditions and groups, counted as the server counts them: over the whole tree, the
 * root included as a group, and a disabled node — with everything under it — not at all.
 */
export function countEnabled(g: EditorGroup): TreeCounts {
  if (g.disabled) return { conditions: 0, groups: 0 };
  let conditions = g.conditions.filter((c) => !c.disabled).length;
  let groups = 1;
  for (const child of g.groups) {
    const sub = countEnabled(child);
    conditions += sub.conditions;
    groups += sub.groups;
  }
  return { conditions, groups };
}

// ---- editing ----

export const cloneCondition = (c: EditorCondition): EditorCondition => ({
  ...c, id: newId(), ...(c.values ? { values: [...c.values] } : {}),
});

export const cloneGroup = (g: EditorGroup): EditorGroup => ({
  ...g, id: newId(), conditions: g.conditions.map(cloneCondition), groups: g.groups.map(cloneGroup),
});

export function toggleDisabled<T extends { disabled?: boolean }>(node: T): T {
  if (!node.disabled) return { ...node, disabled: true };
  const next = { ...node };
  delete (next as { disabled?: boolean }).disabled;
  return next;
}

/** A row ConditionRow rebuilt from scratch, put back in its place: same id, same on/off state. */
export function withIdentityOf(row: EditorCondition, next: FilterCondition): EditorCondition {
  const out: EditorCondition = { ...next, id: row.id };
  if (row.disabled) out.disabled = true;
  else delete out.disabled;
  return out;
}

export function insertAfter<T extends { id: string }>(list: T[], id: string, item: T): T[] {
  const at = list.findIndex((x) => x.id === id);
  return at < 0 ? [...list, item] : [...list.slice(0, at + 1), item, ...list.slice(at + 1)];
}

// ---- the URL form ----

function stripCondition(c: EditorCondition): FilterCondition {
  const out: FilterCondition = { field: c.field, op: c.op };
  if (c.value !== undefined) out.value = c.value;
  if (c.values !== undefined) out.values = [...c.values];
  if (c.disabled) out.disabled = true;
  return out;
}

function stripGroup(g: EditorGroup): FilterGroup {
  const out: FilterGroup = { logic: g.logic, conditions: g.conditions.map(stripCondition) };
  if (g.groups.length > 0) out.groups = g.groups.map(stripGroup);
  if (g.disabled) out.disabled = true;
  return out;
}

/**
 * The tree as the URL keeps it: every part, disabled and unfinished ones included, so a reload or
 * a shared link restores exactly what was on screen. Ids stripped; null when there is nothing.
 */
export function toUrlJson(root: EditorGroup): string | null {
  return isEmptyTree(root) ? null : JSON.stringify(stripGroup(root));
}

const isRecord = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

const notNull = <T>(x: T | null): x is T => x !== null;

function readCondition(v: unknown): EditorCondition | null {
  if (!isRecord(v) || typeof v.field !== 'string' || typeof v.op !== 'string') return null;
  if (!OPERATORS.includes(v.op as FilterOperator)) return null;
  const c: EditorCondition = { id: newId(), field: v.field, op: v.op as FilterOperator };
  if (typeof v.value === 'string' || typeof v.value === 'number' || v.value === null) c.value = v.value;
  if (Array.isArray(v.values)) {
    c.values = v.values.filter((x): x is string | number | null =>
      typeof x === 'string' || typeof x === 'number' || x === null);
  }
  if (v.disabled === true) c.disabled = true;
  return c;
}

/** Far past the server's MaxDepth: a hostile link cannot recurse the tab to death. */
const MAX_PARSE_DEPTH = 10;

function readGroup(v: unknown, depth: number): EditorGroup | null {
  if (!isRecord(v) || depth > MAX_PARSE_DEPTH) return null;
  return {
    id: newId(),
    logic: v.logic === 'Or' ? 'Or' : 'And',
    conditions: Array.isArray(v.conditions) ? v.conditions.map(readCondition).filter(notNull) : [],
    groups: Array.isArray(v.groups) ? v.groups.map((g) => readGroup(g, depth + 1)).filter(notNull) : [],
    ...(v.disabled === true ? { disabled: true } : {}),
  };
}

/**
 * Reads `filter=` (and, for old links, `logic=`). An object is the tree. An array is the flat
 * list links carried before groups existed, joined by the old `logic` param. Anything else —
 * including JSON a chat client truncated — is no filter: a broken link must never blank the page.
 */
export function parseFilterParam(raw: string | null, legacyLogic: string | null): EditorGroup {
  if (!raw) return emptyRoot();
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return emptyRoot();
  }
  if (Array.isArray(parsed)) {
    return {
      ...emptyRoot(),
      logic: legacyLogic === 'Or' ? 'Or' : 'And',
      conditions: parsed.map(readCondition).filter(notNull),
    };
  }
  return readGroup(parsed, 1) ?? emptyRoot();
}
```

- [ ] **Step 6: Run the tests, typecheck and lint**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/filterTree.test.ts && npm run typecheck && npm run lint`
Expected: PASS, no type or lint errors (the widened `FilterCondition` types are backward compatible with the existing components).

- [ ] **Step 7: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src/api/issues.ts src/JiraUnpacked.Web/src/features/issues/filterTree.ts src/JiraUnpacked.Web/src/features/issues/filterTree.test.ts src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts
git commit -m "feat(web): filter tree model — prune, count, clone, URL form, legacy links"
```

---

### Task 6: The tree lives in the URL and drives the request and export

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/useListState.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/useListState.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.tsx` (interim: props become the tree; still a flat editor over the root)
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts` (`ExportView`, `exportIssuesCsv`)
- Test: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`
- Test: `src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx`
- Test: `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx`

**Interfaces:**
- Consumes: Task 5's `EditorGroup`, `parseFilterParam`, `toUrlJson`, `pruneTree`, `blankCondition`, `emptyRoot`, `isEmptyTree`, `withIdentityOf`.
- Produces: `useListState(): { tree: EditorGroup; setTree(next: EditorGroup): void; sortBy; sortDescending; columns; setSort; setColumns }` — `conditions`, `logic`, `setConditions`, `setLogic` are gone. `tree` keeps its identity (and every node id) across the hook's own URL writes.
- Produces: `useFilterCatalog()` exported from `FilterBuilder.tsx` (the shared catalog query); `FilterBuilder` props `{ projectId: string; tree: EditorGroup; onChange(next: EditorGroup): void }`.
- Produces: `ExportView { filter?: FilterGroup; sortBy; sortDescending; columns }`; `exportIssuesCsv` sends `filter` as the pruned tree JSON and never sends `logic`.

- [ ] **Step 1: Write the failing hook tests**

Replace `src/JiraUnpacked.Web/src/features/issues/useListState.test.tsx` with:

```tsx
import { renderHook, act } from '@testing-library/react';
import { MemoryRouter, useNavigate, useSearchParams } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { useListState } from './useListState';
import { emptyRoot, type EditorGroup } from './filterTree';

const wrap = (initial: string) =>
  function Wrapper({ children }: { children: React.ReactNode }) {
    return <MemoryRouter initialEntries={[initial]}>{children}</MemoryRouter>;
  };

/** The hook plus what it cannot report about itself: the URL it wrote, and a way to navigate. */
const useProbe = () => ({ state: useListState(), params: useSearchParams()[0], navigate: useNavigate() });

const ONE_ROW: EditorGroup = {
  id: 'root-1', logic: 'And', groups: [],
  conditions: [{ id: 'row-1', field: 'summary', op: 'Contains', value: 'l' }],
};

describe('useListState', () => {
  it('writes the tree to the URL as JSON without ids, and drops the legacy logic param', () => {
    const legacy = encodeURIComponent('[{"field":"status","op":"Eq","value":"Backlog"}]');
    const { result } = renderHook(useProbe, { wrapper: wrap(`/projects/p1/issues?filter=${legacy}&logic=Or`) });

    act(() => result.current.state.setTree(ONE_ROW));

    expect(result.current.params.get('logic')).toBeNull();
    expect(JSON.parse(result.current.params.get('filter')!)).toEqual({
      logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'l' }],
    });
  });

  it('keeps the editor’s ids across its own URL writes, so rows are not remounted mid-typing', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.setTree(ONE_ROW));
    act(() => result.current.state.setTree({
      ...ONE_ROW, conditions: [{ ...ONE_ROW.conditions[0], value: 'lo' }],
    }));

    expect(result.current.state.tree.id).toBe('root-1');
    expect(result.current.state.tree.conditions[0].id).toBe('row-1');
    expect(result.current.state.tree.conditions[0].value).toBe('lo');
  });

  it('adopts a filter from a URL it did not write (Back, a pasted link)', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });
    const tree = encodeURIComponent('{"logic":"Or","conditions":[{"field":"status","op":"IsNull"}]}');

    act(() => result.current.navigate(`/projects/p1/issues?filter=${tree}`));

    expect(result.current.state.tree.logic).toBe('Or');
    expect(result.current.state.tree.conditions).toEqual([expect.objectContaining({ field: 'status', op: 'IsNull' })]);
  });

  it('reads a legacy flat link with its logic param', () => {
    const legacy = encodeURIComponent('[{"field":"status","op":"Eq","value":"Backlog"}]');
    const { result } = renderHook(useProbe, { wrapper: wrap(`/projects/p1/issues?filter=${legacy}&logic=Or`) });

    expect(result.current.state.tree.logic).toBe('Or');
    expect(result.current.state.tree.conditions).toEqual([
      expect.objectContaining({ field: 'status', op: 'Eq', value: 'Backlog' }),
    ]);
  });

  it('removes filter= entirely for an empty tree', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.setTree(ONE_ROW));
    act(() => result.current.state.setTree(emptyRoot()));

    expect(result.current.params.has('filter')).toBe(false);
  });

  it('ignores a malformed filter param rather than crashing the page', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?filter=%7Bnot-json') });

    expect(result.current.state.tree.conditions).toEqual([]);
    expect(result.current.state.tree.groups).toEqual([]);
  });

  it('keeps the tree stable between renders', () => {
    // IssueList resets paging in an effect keyed on the tree. A fresh identity on every render
    // would make that effect fire forever, so this is load-bearing, not tidiness.
    const { result, rerender } = renderHook(useProbe, {
      wrapper: wrap('/projects/p1/issues?filter=' + encodeURIComponent('{"logic":"And","conditions":[{"field":"status","op":"IsNull"}]}')),
    });
    const first = result.current.state.tree;

    rerender();

    expect(result.current.state.tree).toBe(first);
  });

  it('reads sort and columns from the URL, and falls back to defaults', () => {
    const { result } = renderHook(useProbe, {
      wrapper: wrap('/projects/p1/issues?sortBy=storyPoints&sortDesc=false&cols=jiraKey,summary'),
    });
    expect(result.current.state.sortBy).toBe('storyPoints');
    expect(result.current.state.sortDescending).toBe(false);
    expect(result.current.state.columns).toEqual(['jiraKey', 'summary']);

    const plain = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });
    expect(plain.result.current.state.sortBy).toBe('updatedAt');
    expect(plain.result.current.state.sortDescending).toBe(true);
  });

  it('leaves defaults out of the URL so a shared link carries only deliberate choices', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?sortBy=summary') });

    act(() => result.current.state.setSort('updatedAt', true));

    expect(result.current.params.has('sortBy')).toBe(false);
  });
});
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/useListState.test.tsx`
Expected: FAIL — `setTree`/`tree` do not exist.

- [ ] **Step 3: Implement the hook**

Replace `src/JiraUnpacked.Web/src/features/issues/useListState.ts` (keep `DEFAULT_SORT`/`DEFAULT_COLUMNS` and the module doc comment; the body changes):

```ts
import { useCallback, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { parseFilterParam, toUrlJson, type EditorGroup } from './filterTree';

export const DEFAULT_SORT = 'updatedAt';

/** The columns the table has always shown. Ids match the filter and sort vocabulary. */
export const DEFAULT_COLUMNS = [
  'jiraKey', 'summary', 'status', 'issueType', 'assignee', 'sprint', 'storyPoints', 'updatedAt',
];

/** How many of this hook's own recent writes it recognises when the URL catches up with them. */
const REMEMBERED_WRITES = 20;

/**
 * The list's whole view — filter tree, sort, columns — read from and written to the query string.
 *
 * The URL rather than localStorage: only ju.username and ju.theme may be persisted, and a URL is
 * shareable, survives reload and Back. A default is written as an ABSENT param, so an untouched
 * list has a clean URL. The filter is written unpruned (disabled parts survive a reload) and
 * always as a tree; the legacy `logic` param is read once and dropped on the first write.
 */
export function useListState() {
  const [params, setParams] = useSearchParams();
  const rawFilter = params.get('filter');
  const rawLogic = params.get('logic');

  // The strings this hook itself wrote to `filter=`. React Router applies URL updates in a
  // transition, so our own write can land a render or more after the edit. When it does, that
  // is an echo, not news: re-parsing it would mint fresh ids and remount every row, dropping
  // focus and any half-typed value mid-keystroke. Read in render, only ever written in setTree.
  const recentWrites = useRef<string[]>([]);

  const [held, setHeld] = useState(() => ({
    raw: rawFilter, logic: rawLogic, tree: parseFilterParam(rawFilter, rawLogic),
  }));

  let tree = held.tree;
  if (held.raw !== rawFilter || held.logic !== rawLogic) {
    // The URL moved. Our own write arriving keeps the tree already held (it may even be newer
    // than the URL); anything else — Back, a pasted link — is adopted from the URL.
    const echo = rawLogic === null && recentWrites.current.includes(rawFilter ?? '');
    if (!echo) tree = parseFilterParam(rawFilter, rawLogic);
    setHeld({ raw: rawFilter, logic: rawLogic, tree });
  }

  const patch = useCallback((next: Record<string, string | null>) => {
    setParams((prev) => {
      const out = new URLSearchParams(prev);
      for (const [key, value] of Object.entries(next)) {
        if (value === null) out.delete(key);
        else out.set(key, value);
      }
      return out;
    }, { replace: true });
  }, [setParams]);

  const setTree = useCallback((next: EditorGroup) => {
    const raw = toUrlJson(next);
    recentWrites.current = [...recentWrites.current.slice(-(REMEMBERED_WRITES - 1)), raw ?? ''];
    setHeld((h) => ({ ...h, tree: next }));
    patch({ filter: raw, logic: null });
  }, [patch]);

  const columnsParam = params.get('cols');
  const columns = useMemo(
    () => columnsParam?.split(',').filter(Boolean) ?? DEFAULT_COLUMNS,
    [columnsParam],
  );

  return {
    tree,
    setTree,
    sortBy: params.get('sortBy') ?? DEFAULT_SORT,
    sortDescending: params.get('sortDesc') !== 'false',
    columns,

    setSort: (by: string, descending: boolean) =>
      patch({
        sortBy: by === DEFAULT_SORT ? null : by,
        sortDesc: descending ? null : 'false',
      }),

    setColumns: (next: string[]) =>
      patch({ cols: next.join(',') === DEFAULT_COLUMNS.join(',') ? null : next.join(',') }),
  };
}
```

- [ ] **Step 4: Run the hook tests**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/useListState.test.tsx`
Expected: PASS.

- [ ] **Step 5: Export takes the pruned tree**

In `src/JiraUnpacked.Web/src/api/issues.ts` replace `ExportView` and `exportIssuesCsv`:

```ts
export interface ExportView {
  /** The pruned tree the list was queried with; undefined = no filter. */
  filter?: FilterGroup;
  sortBy: string;
  sortDescending: boolean;
  columns: string[];
}

/**
 * The CSV of the view currently on screen. Fetched with the session rather than linked to — see
 * apiDownload for why a link cannot work. The server caps the export's size, which is what makes
 * holding it in memory acceptable. `filter` is always the tree: the server reads a JSON object as
 * a FilterGroup with its own logic, so no `logic` param is sent.
 */
export const exportIssuesCsv = (projectId: string, view: ExportView): Promise<Download> => {
  const params = new URLSearchParams();
  if (view.filter) params.set('filter', JSON.stringify(view.filter));
  params.set('sortBy', view.sortBy);
  params.set('sortDescending', String(view.sortDescending));
  params.set('cols', view.columns.join(','));
  return apiDownload(`/api/projects/${projectId}/issues/export.csv?${params.toString()}`);
};
```

- [ ] **Step 6: FilterBuilder takes the tree (interim flat editor)**

Replace `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.tsx` with the version below. It still edits only the root's own conditions — Task 7 replaces the body with `GroupEditor`. The Neq note is deleted: `Neq` no longer matches empty fields (Task 1).

```tsx
import { useQuery } from '@tanstack/react-query';
import type { FilterCondition, FilterLogic } from '../../api/issues';
import { getFilterCatalog } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { ApiError } from '../../api/errors';
import { Button, ErrorState, Field, Select, Spinner } from '../../ui';
import { ConditionRow } from './ConditionRow';
import { blankCondition, emptyRoot, isEmptyTree, withIdentityOf, type EditorGroup } from './filterTree';

/**
 * The server's filter-field allow-list and guards. Static and tenant-independent, hence
 * `staleTime: Infinity`: fetched once per page load and shared through the query cache by every
 * caller (FilterBuilder renders from it; IssueList prunes retired fields with it).
 */
export function useFilterCatalog() {
  return useQuery({
    queryKey: queryKeys.filterCatalog(),
    queryFn: getFilterCatalog,
    staleTime: Infinity,
  });
}

interface FilterBuilderProps {
  /** Needed only to fetch this project's filter values; the builder holds no other project state. */
  projectId: string;
  tree: EditorGroup;
  onChange: (next: EditorGroup) => void;
}

/**
 * Holds no state of its own — every change flows up through `onChange`, so the caller
 * (IssueList, via the URL) is the single owner of the tree that drives its query.
 */
export function FilterBuilder({ projectId, tree, onChange }: FilterBuilderProps) {
  const { data: catalog, isPending, error } = useFilterCatalog();

  if (isPending) return <Spinner aria-label="Loading filter catalog" />;
  if (error) {
    return (
      <ErrorState
        title={error instanceof ApiError ? error.title : 'Could not load the filter catalog.'}
      />
    );
  }

  const conditions = tree.conditions;
  const atConditionLimit = conditions.length >= catalog.maxConditions;

  const handleAdd = () => {
    if (atConditionLimit) return;
    onChange({ ...tree, conditions: [...conditions, blankCondition()] });
  };

  const handleRowChange = (id: string, next: FilterCondition) =>
    onChange({ ...tree, conditions: conditions.map((c) => (c.id === id ? withIdentityOf(c, next) : c)) });

  const handleRemove = (id: string) =>
    onChange({ ...tree, conditions: conditions.filter((c) => c.id !== id) });

  if (isEmptyTree(tree)) {
    return (
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm text-[var(--ju-muted)]">No filter — showing all issues</p>
        <Button type="button" variant="secondary" onClick={handleAdd} disabled={atConditionLimit}>
          Add condition
        </Button>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <Field label="Match" id="filter-logic" hint="All: every condition must hold. Any: at least one.">
        <Select
          id="filter-logic"
          value={tree.logic}
          onChange={(e) => onChange({ ...tree, logic: e.target.value as FilterLogic })}
        >
          <option value="And">All conditions</option>
          <option value="Or">Any condition</option>
        </Select>
      </Field>

      {conditions.map((condition, index) => (
        <ConditionRow
          key={condition.id}
          index={index}
          projectId={projectId}
          condition={condition}
          catalog={catalog}
          onChange={(next) => handleRowChange(condition.id, next)}
          onRemove={() => handleRemove(condition.id)}
        />
      ))}

      <div className="flex items-center gap-2">
        <Button type="button" variant="secondary" onClick={handleAdd} disabled={atConditionLimit}>
          Add condition
        </Button>
        <Button type="button" variant="secondary" onClick={() => onChange(emptyRoot())}>
          Clear all
        </Button>
      </div>

      {atConditionLimit && (
        <p className="text-xs text-[var(--ju-muted)]">
          You can add at most {catalog.maxConditions} conditions.
        </p>
      )}
    </div>
  );
}
```

- [ ] **Step 7: IssueList sends the pruned tree**

In `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`:

1. Change the imports: `import { useEffect, useMemo, useState } from 'react';`; the type import becomes `import type { IssueFilterRequest, IssueListItem, PagedResult } from '../../api/issues';`; `import { FilterBuilder, useFilterCatalog } from './FilterBuilder';`; add `import { pruneTree } from './filterTree';`.
2. Delete the `isCompleteCondition` function and its doc comment (it now lives in `filterTree.ts`).
3. Replace everything from the `useListState()` destructuring through the `useQuery({ queryKey: queryKeys.issues(...) … })` call's preceding `request` declaration with:

```tsx
  // The view lives in the URL, not in component state: that makes a filtered list shareable as
  // a link and survives reload and Back, neither of which useState does.
  const { tree, setTree, columns, setColumns, sortBy, sortDescending, setSort } = useListState();

  const [page, setPage] = useState(1);

  // Narrowing the filter can leave the user stranded on a page that no longer exists — jump
  // back to page 1 whenever the tree changes. `tree` keeps its identity across the URL's echo
  // of our own writes (see useListState), so this fires once per edit.
  useEffect(() => {
    setPage(1);
  }, [tree]);

  // Shared with FilterBuilder through the query cache: same key, fetched once.
  const { data: catalog } = useFilterCatalog();
  const knownFields = useMemo(
    () => (catalog ? new Set(catalog.fields.map((f) => f.name)) : undefined),
    [catalog],
  );

  // What the server sees: the tree without disabled parts, unfinished rows or retired fields
  // (see pruneTree). Undefined = no filter — never an empty group, which the server refuses.
  const filter = useMemo(() => pruneTree(tree, knownFields), [tree, knownFields]);

  const paging = { page, pageSize: DEFAULT_PAGE_SIZE, sortBy, sortDescending };
  const request: IssueFilterRequest = filter ? { filter, ...paging } : paging;
```

4. The export mutation: `exportIssuesCsv(id, { filter, sortBy, sortDescending, columns })`.
5. The builder: `<FilterBuilder projectId={id} tree={tree} onChange={setTree} />`.
6. The two empty states key on whether a filter was **sent**, not on rows in the editor (a tree of only blank or disabled rows is an unfiltered list):

```tsx
      {showEmpty && !filter && (
        <EmptyState
          title="No issues yet"
          description="Run a sync to pull this project's issues from Jira."
          action={<Link to={`/projects/${id}`}>Go to sync</Link>}
        />
      )}

      {showEmpty && filter && (
        <EmptyState
          title="No issues match this filter"
          description="Try removing or loosening a condition."
        />
      )}
```

- [ ] **Step 8: Update the component tests**

`src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx`: delete the local `CATALOG` constant and the `FilterCatalog` type import; add `import { FILTER_CATALOG as CATALOG } from '../../test/filterCatalogFixture';`.

`src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`:

1. Delete the local `CATALOG` constant; add `import { FILTER_CATALOG as CATALOG } from '../../test/filterCatalogFixture';` and `import { emptyRoot } from './filterTree';`; add `useLocation` to the `react-router-dom` import; change the type import to `import type { FilterCondition } from '../../api/issues';`.
2. In the spinner and catalog-error tests replace `<FilterBuilder projectId="p1" conditions={[]} onChange={() => {}} logic="And" onLogicChange={() => {}} />` with `<FilterBuilder projectId="p1" tree={emptyRoot()} onChange={() => {}} />`.
3. In `blocks a 21st condition` replace the `FilterBuilder` element with:

```tsx
        <FilterBuilder
          projectId="p1"
          onChange={() => {}}
          tree={{
            id: 'root', logic: 'And', groups: [],
            conditions: Array.from({ length: CATALOG.maxConditions }, (_, i) => ({
              id: `c${i}`, field: 'status', op: 'Eq' as const, value: '',
            })),
          }}
        />
```

4. In `offers only the operators the chosen field allows` the expectation becomes `expect(ops).toEqual(['Eq', 'In', 'NotIn', 'Contains', 'StartsWith']);` (the fixture is the new catalog; Task 9 turns these into words).
5. Replace `shows the Neq-on-dictionary-field note only while a row uses it` with:

```tsx
  it('no longer warns that "is not" matches empty fields, because it does not', async () => {
    mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    await user.selectOptions(screen.getByLabelText(/operator for condition 1/i), 'Neq');

    expect(screen.queryByText(/also matches issues where the field is empty/i)).not.toBeInTheDocument();
  });
```

6. Append to the `FilterBuilder logic toggle` describe:

```tsx
  it('reads a legacy flat link with its old logic, and writes a tree without it on the next edit', async () => {
    mockFilterCatalog();
    const sent: Record<string, unknown>[] = [];
    server.use(http.post('/api/projects/p1/issues/query', async ({ request }) => {
      sent.push((await request.json()) as Record<string, unknown>);
      return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
    }));
    let search = '';
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={[`/projects/p1/issues?filter=${ONE_CONDITION}&logic=Or`]}>
          <Routes>
            <Route
              path="/projects/:id/issues"
              element={<><IssueList /><SearchProbe onSearch={(s) => { search = s; }} /></>}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await waitFor(() => expect(sent.at(-1)).toMatchObject({ filter: { logic: 'Or' } }));

    await user.selectOptions(await screen.findByLabelText(/match/i), 'And');

    await waitFor(() => {
      const params = new URLSearchParams(search);
      expect(params.get('logic')).toBeNull();
      expect(JSON.parse(params.get('filter')!)).toEqual({
        logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Backlog' }],
      });
    });
  });

  it('leaves a field the catalog no longer has out of the request', async () => {
    const { bodies } = mockIssuesQuery();
    const tree = {
      logic: 'And',
      conditions: [
        { field: 'retired', op: 'Eq', value: 'x' },
        { field: 'summary', op: 'Contains', value: 'login' },
      ],
    };

    renderIssueListAt(`/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(tree))}`);

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }],
    }));
  });
```

and add this helper at the bottom of the file:

```tsx
/** Reports the router's current query string, so a test can see what the list wrote to the URL. */
function SearchProbe({ onSearch }: { onSearch: (search: string) => void }) {
  onSearch(useLocation().search);
  return null;
}
```

`src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx`, in `exports the filter, sort and columns on screen`, replace the `logic` and `filter` expectations:

```tsx
    // A legacy link (flat list + logic=Or) is exported as the tree it now means; no logic param.
    expect(params.has('logic')).toBe(false);
    expect(JSON.parse(params.get('filter')!)).toEqual({
      logic: 'Or', conditions: [{ field: 'status', op: 'Eq', value: 'Backlog' }],
    });
```

- [ ] **Step 9: Run the web suite, typecheck and lint**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test`
Expected: PASS. The existing `sends value (not values) for a single-value operator` test types `login` character by character — it passing is part of Review Focus #1.

- [ ] **Step 10: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): the filter is a tree in the URL; requests and exports send it pruned"
```

---

### Task 7: Recursive GroupEditor — groups, duplicate, disable, limits

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/GroupEditor.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/GroupEditor.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` (label prop; the Remove button moves out)
- Test: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`

**Interfaces:**
- Consumes: Task 5's `countEnabled`, `cloneCondition`, `cloneGroup`, `toggleDisabled`, `withIdentityOf`, `insertAfter`, `blankCondition`, `blankGroup`, `emptyRoot`, `isEmptyTree`; Task 6's `useFilterCatalog`.
- Produces: `GroupEditor` props `{ group: EditorGroup; onChange(next: EditorGroup): void; catalog: FilterCatalog; projectId: string; counts: TreeCounts; depth: number; label: string; onRemove?(): void; onDuplicate?(): void }` (root: `depth=1`, `label=""`, no `onRemove`/`onDuplicate`).
- Produces (accessible names, used by later tests): root region `group "Filter"`, nested `group "Group 2"`; logic select labelled `Match` (root) / `Match (group 2)`; buttons `Disable filter`, `Disable group 2`, `Duplicate group 2`, `Remove group 2`, `Disable condition 2.1`, `Duplicate condition 2.1`, `Remove condition 2.1`, `Add condition` / `Add group` (root), `Add condition to group 2` / `Add group to group 2` (nested). Toggles carry `aria-pressed`. Numbering: a group's conditions are 1..n, its nested groups continue n+1…; nested labels are prefixed (`2.1`).
- Produces: `ConditionRow` props `{ projectId; condition: FilterCondition; catalog; onChange(next: FilterCondition): void; label: string }` — `index` and `onRemove` are gone.

- [ ] **Step 1: Write the failing GroupEditor tests**

Create `src/JiraUnpacked.Web/src/features/issues/GroupEditor.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import type { FilterCatalog } from '../../api/issues';
import { GroupEditor } from './GroupEditor';
import { countEnabled, type EditorCondition, type EditorGroup } from './filterTree';

const summary = (id: string, value = 'x'): EditorCondition => ({ id, field: 'summary', op: 'Contains', value });
const root = (rest: Partial<EditorGroup> = {}): EditorGroup =>
  ({ id: 'r', logic: 'And', conditions: [], groups: [], ...rest });
const nested = (id: string, rest: Partial<EditorGroup> = {}): EditorGroup =>
  ({ id, logic: 'And', conditions: [], groups: [], ...rest });

function renderEditor(initial: EditorGroup, catalog: FilterCatalog = FILTER_CATALOG) {
  // New rows default to status, whose value list is fetched. These tests are about structure.
  server.use(http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })));
  let latest = initial;
  function Harness() {
    const [tree, setTree] = useState(initial);
    return (
      <GroupEditor
        group={tree}
        onChange={(next) => { latest = next; setTree(next); }}
        catalog={catalog}
        projectId="p1"
        counts={countEnabled(tree)}
        depth={1}
        label=""
      />
    );
  }
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Harness />
    </QueryClientProvider>,
  );
  return { tree: () => latest };
}

describe('GroupEditor', () => {
  it('adds a nested group seeded with one blank condition', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({ conditions: [summary('a')] }));

    await user.click(screen.getByRole('button', { name: 'Add group' }));

    const group = screen.getByRole('group', { name: 'Group 2' });
    expect(within(group).getByLabelText('Field for condition 2.1')).toHaveValue('status');
    expect(editor.tree().groups[0].conditions).toHaveLength(1);
  });

  it('offers "Add group" down to the catalog depth and no further', async () => {
    const user = userEvent.setup();
    renderEditor(root({ conditions: [summary('a')] }));

    await user.click(screen.getByRole('button', { name: 'Add group' }));              // depth 2
    await user.click(screen.getByRole('button', { name: 'Add group to group 2' }));   // depth 3

    const deepest = screen.getByRole('group', { name: 'Group 2.2' });
    expect(within(deepest).queryByRole('button', { name: /add group/i })).toBeNull();
  });

  it('duplicates a condition directly after the original, as a separate row', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({ conditions: [summary('a', 'first'), summary('b', 'second')] }));

    await user.click(screen.getByRole('button', { name: 'Duplicate condition 1' }));

    expect(editor.tree().conditions.map((c) => c.value)).toEqual(['first', 'first', 'second']);
    expect(new Set(editor.tree().conditions.map((c) => c.id)).size).toBe(3);
  });

  it('duplicates a whole group, deep, with fresh ids', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({
      conditions: [summary('a')],
      groups: [nested('g', { logic: 'Or', conditions: [summary('b', 'inner')] })],
    }));

    await user.click(screen.getByRole('button', { name: 'Duplicate group 2' }));

    const [original, copy] = editor.tree().groups;
    expect(copy).toMatchObject({ logic: 'Or', conditions: [{ value: 'inner' }] });
    expect(copy.id).not.toBe(original.id);
    expect(copy.conditions[0].id).not.toBe(original.conditions[0].id);
  });

  it('removes a nested group and a condition', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({
      conditions: [summary('a')],
      groups: [nested('g', { conditions: [summary('b')] })],
    }));

    await user.click(screen.getByRole('button', { name: 'Remove group 2' }));
    await user.click(screen.getByRole('button', { name: 'Remove condition 1' }));

    expect(editor.tree().groups).toEqual([]);
    expect(editor.tree().conditions).toEqual([]);
  });

  it('switches a condition and a group off and on, showing the pressed state', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({
      conditions: [summary('a')],
      groups: [nested('g', { conditions: [summary('b')] })],
    }));

    await user.click(screen.getByRole('button', { name: 'Disable condition 1' }));
    await user.click(screen.getByRole('button', { name: 'Disable group 2' }));

    expect(screen.getByRole('button', { name: 'Disable condition 1' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Disable group 2' })).toHaveAttribute('aria-pressed', 'true');
    expect(editor.tree().conditions[0].disabled).toBe(true);
    expect(editor.tree().groups[0].disabled).toBe(true);

    await user.click(screen.getByRole('button', { name: 'Disable condition 1' }));
    expect(editor.tree().conditions[0].disabled).toBeUndefined();
  });

  it('stops adding conditions at the limit, and counts only enabled ones', async () => {
    const user = userEvent.setup();
    renderEditor(root({ conditions: [summary('a'), summary('b')] }), { ...FILTER_CATALOG, maxConditions: 2 });

    const add = screen.getByRole('button', { name: 'Add condition' });
    expect(add).toBeDisabled();
    expect(add).toHaveAttribute('title', 'Limit: 2 conditions');
    expect(screen.getByRole('button', { name: 'Duplicate condition 1' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Disable condition 2' }));

    expect(screen.getByRole('button', { name: 'Add condition' })).toBeEnabled();
  });

  it('stops adding and duplicating groups at the group limit', () => {
    renderEditor(
      root({ conditions: [summary('a')], groups: [nested('g', { conditions: [summary('b')] })] }),
      { ...FILTER_CATALOG, maxGroups: 2 },
    );

    const add = screen.getByRole('button', { name: 'Add group' });
    expect(add).toBeDisabled();
    expect(add).toHaveAttribute('title', 'Limit: 2 groups');
    expect(screen.getByRole('button', { name: 'Duplicate group 2' })).toBeDisabled();
  });
});
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/GroupEditor.test.tsx`
Expected: FAIL — `./GroupEditor` does not exist.

- [ ] **Step 3: ConditionRow takes a position label and loses its Remove button**

In `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` make exactly these edits (Task 9 rewrites the file; this keeps it working inside groups meanwhile):

1. In `ConditionRowProps` delete `onRemove: () => void;` and replace `index: number;` with:

```tsx
  /** Position in the tree, e.g. "1" or "2.1" — part of every control's accessible name. */
  label: string;
```

2. Destructuring: `projectId, condition, catalog, onChange, label }: ConditionRowProps) {`
3. Delete `const rowNumber = index + 1;`. Replace every `${rowNumber}` with `${label}` (field label, operator label, `valueLabel`) and every `condition-${index}-` with `condition-${label}-` (the field id, operator id and `valueFieldId`).
4. Update the comment above the `prev`/`inDraft` reset: rows are now keyed by id (`GroupEditor`), so the reset only matters when the condition's own field or operator changes.
5. Delete the trailing `<Button type="button" variant="secondary" onClick={onRemove}>Remove</Button>`.

- [ ] **Step 4: Implement GroupEditor**

Create `src/JiraUnpacked.Web/src/features/issues/GroupEditor.tsx`:

```tsx
import type { ButtonHTMLAttributes } from 'react';
import type { FilterCatalog, FilterLogic } from '../../api/issues';
import { Button, Select } from '../../ui';
import { ConditionRow } from './ConditionRow';
import {
  blankCondition, blankGroup, cloneCondition, cloneGroup, countEnabled, insertAfter, toggleDisabled,
  withIdentityOf,
  type EditorGroup, type TreeCounts,
} from './filterTree';

interface GroupEditorProps {
  group: EditorGroup;
  onChange: (next: EditorGroup) => void;
  catalog: FilterCatalog;
  projectId: string;
  /** Enabled conditions and groups over the WHOLE tree: the limits are global, as on the server. */
  counts: TreeCounts;
  /** Root = 1. "Add group" is offered only while a child would still be within catalog.maxDepth. */
  depth: number;
  /** Position label, e.g. "2" or "2.1"; empty for the root. Part of every accessible name. */
  label: string;
  /** Absent at the root: the root is cleared ("Clear all"), never removed or duplicated. */
  onRemove?: () => void;
  onDuplicate?: () => void;
}

type IconButtonProps = { label: string; glyph: string } &
  Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'>;

/** A compact button whose accessible name is `label`; the glyph is decoration. */
function IconButton({ label, glyph, ...rest }: IconButtonProps) {
  return (
    <Button type="button" variant="ghost" size="sm" aria-label={label} {...rest}>
      <span aria-hidden="true">{glyph}</span>
    </Button>
  );
}

/**
 * One boolean group: its match (All/Any), its condition rows, its nested groups (recursively),
 * and the add buttons. Holds no state — every edit rebuilds this group and hands it up.
 *
 * Conditions render before nested groups because the wire keeps them in separate lists; logic
 * is commutative, so the order is presentation only.
 */
export function GroupEditor({
  group, onChange, catalog, projectId, counts, depth, label, onRemove, onDuplicate,
}: GroupEditorProps) {
  const isRoot = depth === 1;
  const name = isRoot ? 'filter' : `group ${label}`;
  const childLabel = (position: number) => (isRoot ? String(position) : `${label}.${position}`);
  const { conditions, groups } = group;

  /** Why adding `extra` would break a limit, as the hint the disabled button shows; undefined = it fits. */
  const limitHint = (extra: TreeCounts): string | undefined => {
    if (counts.conditions + extra.conditions > catalog.maxConditions) return `Limit: ${catalog.maxConditions} conditions`;
    if (counts.groups + extra.groups > catalog.maxGroups) return `Limit: ${catalog.maxGroups} groups`;
    return undefined;
  };
  const addConditionHint = limitHint({ conditions: 1, groups: 0 });
  // A new group arrives with one blank condition, so it needs room for both.
  const addGroupHint = limitHint({ conditions: 1, groups: 1 });
  const duplicateHint = limitHint(countEnabled(group));

  const set = (patch: Partial<EditorGroup>) => onChange({ ...group, ...patch });

  return (
    <div
      role="group"
      aria-label={isRoot ? 'Filter' : `Group ${label}`}
      className={[
        'flex flex-col gap-2',
        isRoot ? '' : 'ml-4 border-l-2 border-[var(--ju-primary)] pl-3',
        group.disabled ? 'opacity-50' : '',
      ].join(' ')}
    >
      <div className="flex items-center gap-2">
        <label htmlFor={`${group.id}-logic`} className="text-sm font-medium text-[var(--ju-text)]">
          {isRoot ? 'Match' : `Match (group ${label})`}
        </label>
        <Select
          id={`${group.id}-logic`}
          value={group.logic}
          title="All: every part must hold. Any: at least one."
          onChange={(e) => set({ logic: e.target.value as FilterLogic })}
        >
          <option value="And">All</option>
          <option value="Or">Any</option>
        </Select>
        <span className="flex-1" />
        {onDuplicate && (
          <IconButton
            label={`Duplicate ${name}`}
            glyph="⎘"
            disabled={duplicateHint !== undefined}
            title={duplicateHint ?? 'Duplicate'}
            onClick={onDuplicate}
          />
        )}
        <IconButton
          label={`Disable ${name}`}
          glyph="⏻"
          aria-pressed={!!group.disabled}
          title={group.disabled ? 'Switched off — click to apply again' : 'Switch off without deleting'}
          onClick={() => onChange(toggleDisabled(group))}
        />
        {onRemove && <IconButton label={`Remove ${name}`} glyph="✕" title="Remove" onClick={onRemove} />}
      </div>

      {conditions.map((c, i) => {
        const rowLabel = childLabel(i + 1);
        // Copying a switched-off row adds nothing that counts, so it is never blocked.
        const copyHint = c.disabled ? undefined : addConditionHint;
        return (
          <div
            key={c.id}
            className={`flex items-end gap-1 rounded-md border border-[var(--ju-border)] p-2 ${c.disabled ? 'opacity-50' : ''}`}
          >
            <IconButton
              label={`Disable condition ${rowLabel}`}
              glyph="⏻"
              aria-pressed={!!c.disabled}
              title={c.disabled ? 'Switched off — click to apply again' : 'Switch off without deleting'}
              onClick={() => set({ conditions: conditions.map((x) => (x.id === c.id ? toggleDisabled(x) : x)) })}
            />
            <div className="flex-1">
              <ConditionRow
                projectId={projectId}
                catalog={catalog}
                condition={c}
                label={rowLabel}
                onChange={(next) =>
                  set({ conditions: conditions.map((x) => (x.id === c.id ? withIdentityOf(x, next) : x)) })}
              />
            </div>
            <IconButton
              label={`Duplicate condition ${rowLabel}`}
              glyph="⎘"
              disabled={copyHint !== undefined}
              title={copyHint ?? 'Duplicate'}
              onClick={() => set({ conditions: insertAfter(conditions, c.id, cloneCondition(c)) })}
            />
            <IconButton
              label={`Remove condition ${rowLabel}`}
              glyph="✕"
              title="Remove"
              onClick={() => set({ conditions: conditions.filter((x) => x.id !== c.id) })}
            />
          </div>
        );
      })}

      {groups.map((g, i) => (
        <GroupEditor
          key={g.id}
          group={g}
          catalog={catalog}
          projectId={projectId}
          counts={counts}
          depth={depth + 1}
          label={childLabel(conditions.length + i + 1)}
          onChange={(next) => set({ groups: groups.map((x) => (x.id === g.id ? next : x)) })}
          onRemove={() => set({ groups: groups.filter((x) => x.id !== g.id) })}
          onDuplicate={() => set({ groups: insertAfter(groups, g.id, cloneGroup(g)) })}
        />
      ))}

      <div className="flex items-center gap-2">
        <Button
          type="button"
          variant="secondary"
          size="sm"
          aria-label={isRoot ? undefined : `Add condition to group ${label}`}
          disabled={addConditionHint !== undefined}
          title={addConditionHint}
          onClick={() => set({ conditions: [...conditions, blankCondition()] })}
        >
          Add condition
        </Button>
        {depth < catalog.maxDepth && (
          <Button
            type="button"
            variant="secondary"
            size="sm"
            aria-label={isRoot ? undefined : `Add group to group ${label}`}
            disabled={addGroupHint !== undefined}
            title={addGroupHint}
            onClick={() => set({ groups: [...groups, blankGroup()] })}
          >
            Add group
          </Button>
        )}
      </div>
    </div>
  );
}
```

- [ ] **Step 5: FilterBuilder renders the root GroupEditor**

In `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.tsx` replace the imports and the `FilterBuilder` function (keep `useFilterCatalog` and `FilterBuilderProps`):

```tsx
import { useQuery } from '@tanstack/react-query';
import { getFilterCatalog } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { ApiError } from '../../api/errors';
import { Button, ErrorState, Spinner } from '../../ui';
import { GroupEditor } from './GroupEditor';
import { blankCondition, blankGroup, countEnabled, emptyRoot, isEmptyTree, type EditorGroup } from './filterTree';
```

```tsx
export function FilterBuilder({ projectId, tree, onChange }: FilterBuilderProps) {
  const { data: catalog, isPending, error } = useFilterCatalog();

  if (isPending) return <Spinner aria-label="Loading filter catalog" />;
  if (error) {
    return (
      <ErrorState
        title={error instanceof ApiError ? error.title : 'Could not load the filter catalog.'}
      />
    );
  }

  if (isEmptyTree(tree)) {
    return (
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm text-[var(--ju-muted)]">No filter — showing all issues</p>
        <div className="flex gap-2">
          <Button type="button" variant="secondary" onClick={() => onChange({ ...tree, conditions: [blankCondition()] })}>
            Add condition
          </Button>
          {catalog.maxDepth > 1 && (
            <Button type="button" variant="secondary" onClick={() => onChange({ ...tree, groups: [blankGroup()] })}>
              Add group
            </Button>
          )}
        </div>
      </div>
    );
  }

  const counts = countEnabled(tree);

  return (
    <div className="flex flex-col gap-3">
      <GroupEditor
        group={tree}
        onChange={onChange}
        catalog={catalog}
        projectId={projectId}
        counts={counts}
        depth={1}
        label=""
      />
      <div>
        <Button type="button" variant="secondary" size="sm" onClick={() => onChange(emptyRoot())}>
          Clear all
        </Button>
      </div>
      {counts.conditions >= catalog.maxConditions && (
        <p className="text-xs text-[var(--ju-muted)]">You can add at most {catalog.maxConditions} conditions.</p>
      )}
      {counts.groups >= catalog.maxGroups && (
        <p className="text-xs text-[var(--ju-muted)]">You can add at most {catalog.maxGroups} groups.</p>
      )}
    </div>
  );
}
```

- [ ] **Step 6: Update the FilterBuilder tests for the new controls**

In `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`:

1. `removes a condition row and excludes it from the next request`: `await user.click(screen.getByRole('button', { name: 'Remove condition 1' }));`
2. `does not bleed a half-typed In draft …`: `await user.click(screen.getByRole('button', { name: 'Remove condition 2' }));`
3. `blocks a 101st In value`: the `ConditionRow` element becomes `<ConditionRow projectId="p1" condition={condition} catalog={CATALOG} onChange={() => {}} label="1" />`.
4. Append a describe that proves the tree reaches the server:

```tsx
describe('nested groups', () => {
  it('sends the tree it shows, minus the parts switched off', async () => {
    const { bodies } = mockIssuesQuery();
    const tree = {
      logic: 'And',
      conditions: [{ field: 'summary', op: 'Contains', value: 'login' }],
      groups: [{
        logic: 'Or',
        conditions: [
          { field: 'storyPoints', op: 'Gte', value: 3 },
          { field: 'storyPoints', op: 'IsNull' },
        ],
      }],
    };
    const user = userEvent.setup({ delay: null });

    renderIssueListAt(`/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(tree))}`);

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual(tree));

    await user.click(await screen.findByRole('button', { name: 'Disable condition 2.1' }));

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And',
      conditions: [{ field: 'summary', op: 'Contains', value: 'login' }],
      groups: [{ logic: 'Or', conditions: [{ field: 'storyPoints', op: 'IsNull' }] }],
    }));
  });
});
```

- [ ] **Step 7: Run the web suite, typecheck and lint**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src/features/issues
git commit -m "feat(web): nested filter groups with duplicate, disable and catalog limits"
```

---

### Task 8: Value pickers and chip input

**Files:**
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts` (`getFilterValues` shape)
- Create: `src/JiraUnpacked.Web/src/features/issues/styles.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/ValuePicker.test.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/ChipInput.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/ChipInput.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` (one line: read names from the new shape)
- Test: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`, `ColumnChooser.test.tsx` (mock shape)

**Interfaces:**
- Consumes: Task 3's HTTP shape.
- Produces (`api/issues.ts`): `FilterValueOption { name: string; count: number }`, `FilterValues { field: string; values: FilterValueOption[]; emptyCount: number }`, `getFilterValues(projectId, field): Promise<FilterValues>`.
- Produces (`styles.ts`): `INPUT_CLASS: string`.
- Produces (`ValuePicker.tsx`): `EMPTY_LABEL = '(Empty)'`; `SingleValueSelect` props `{ value: string; options: FilterValueOption[]; emptyCount: number; onPick(name: string): void; onPickEmpty(): void } & other <select> attributes` (works as a `Field` child); `MultiValuePicker` props `{ label: string; options: FilterValueOption[]; emptyCount: number; selected: (string | number | null)[]; onChange(next: (string | null)[]): void; max: number }`.
- Produces (`ChipInput.tsx`): `ChipInputProps { id; label; values: string[]; onChange(next: string[]): void; max: number; splitOn?: RegExp; normalize?(value: string): string }` and `ChipInput`. `splitOn` must be a non-global regex.

- [ ] **Step 1: Write the failing component tests**

Create `src/JiraUnpacked.Web/src/features/issues/ValuePicker.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { MultiValuePicker, SingleValueSelect } from './ValuePicker';

const OPTIONS = [{ name: 'Backlog', count: 42 }, { name: 'In Progress', count: 5 }];

function renderMulti(initial: (string | null)[] = [], max = 100) {
  let latest = initial;
  function Harness() {
    const [selected, setSelected] = useState<(string | null)[]>(initial);
    return (
      <MultiValuePicker
        label="Values for condition 1"
        options={OPTIONS}
        emptyCount={7}
        selected={selected}
        max={max}
        onChange={(next) => { latest = next; setSelected(next); }}
      />
    );
  }
  render(<Harness />);
  return { latest: () => latest };
}

describe('MultiValuePicker', () => {
  it('lists (Empty) first, then every value, each with its count', async () => {
    renderMulti();
    await userEvent.setup().click(screen.getByText('Choose values'));

    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent))
      .toEqual(['(Empty) 7', 'Backlog 42', 'In Progress 5']);
  });

  it('adds and removes values in the order chosen, (Empty) included', async () => {
    const user = userEvent.setup();
    const picker = renderMulti();
    await user.click(screen.getByText('Choose values'));

    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));
    await user.click(screen.getByRole('checkbox', { name: /^\(empty\)/i }));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));
    await user.click(screen.getByRole('checkbox', { name: /^in progress/i }));

    expect(picker.latest()).toEqual([null, 'In Progress']);
    expect(screen.getByRole('button', { name: 'Remove (Empty)' })).toBeInTheDocument();
  });

  it('narrows the list as you search, keeping (Empty) only when it matches', async () => {
    const user = userEvent.setup();
    renderMulti();
    await user.click(screen.getByText('Choose values'));
    const search = screen.getByRole('searchbox', { name: 'Search Values for condition 1' });

    await user.type(search, 'prog');
    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent)).toEqual(['In Progress 5']);

    await user.clear(search);
    await user.type(search, 'emp');
    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent)).toEqual(['(Empty) 7']);
  });

  it('shows each chosen value once, even if a link repeats it', () => {
    renderMulti(['Backlog', 'Backlog', null]);

    expect(screen.getAllByRole('button', { name: /^remove /i }).map((b) => b.getAttribute('aria-label')))
      .toEqual(['Remove Backlog', 'Remove (Empty)']);
  });

  it('keeps a chosen value the list no longer offers, so it can still be seen and removed', async () => {
    const picker = renderMulti(['Retired status']);

    expect(screen.getByText('Retired status')).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Remove Retired status' }));

    expect(picker.latest()).toEqual([]);
  });

  it('stops at the limit', () => {
    renderMulti(['Backlog'], 1);

    expect(screen.getByRole('checkbox', { name: /^in progress/i })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: /^backlog/i })).toBeEnabled();
    expect(screen.getByText(/at most 1 values/i)).toBeInTheDocument();
  });
});

describe('SingleValueSelect', () => {
  function renderSingle(value = '') {
    const onPick = vi.fn();
    const onPickEmpty = vi.fn();
    render(
      <label>
        Value
        <SingleValueSelect value={value} options={OPTIONS} emptyCount={7} onPick={onPick} onPickEmpty={onPickEmpty} />
      </label>,
    );
    return { onPick, onPickEmpty };
  }

  it('offers (Empty) first, then each value with its count', () => {
    renderSingle();

    expect(within(screen.getByRole('combobox')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['— choose —', '(Empty) (7)', 'Backlog (42)', 'In Progress (5)']);
  });

  it('reports a value by its name, and (Empty) separately', async () => {
    const user = userEvent.setup();
    const { onPick, onPickEmpty } = renderSingle();

    await user.selectOptions(screen.getByRole('combobox'), 'In Progress (5)');
    await user.selectOptions(screen.getByRole('combobox'), '(Empty) (7)');

    expect(onPick).toHaveBeenCalledWith('In Progress');
    expect(onPickEmpty).toHaveBeenCalledTimes(1);
  });

  it('still shows a chosen value the list does not offer', () => {
    renderSingle('Retired');

    expect(screen.getByRole('combobox')).toHaveDisplayValue('Retired (0)');
  });
});
```

Create `src/JiraUnpacked.Web/src/features/issues/ChipInput.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { ChipInput, type ChipInputProps } from './ChipInput';

function renderChips(props: Partial<ChipInputProps> = {}) {
  let latest: string[] = props.values ?? [];
  function Harness() {
    const [values, setValues] = useState<string[]>(props.values ?? []);
    return (
      <ChipInput
        id="chips"
        label="Value for condition 1"
        max={100}
        {...props}
        values={values}
        onChange={(next) => { latest = next; setValues(next); }}
      />
    );
  }
  render(<Harness />);
  return { latest: () => latest, input: () => screen.getByLabelText('Value for condition 1') };
}

describe('ChipInput', () => {
  it('commits a typed value on Enter and clears the box', async () => {
    const chips = renderChips();

    await userEvent.setup().type(chips.input(), 'Done{Enter}');

    expect(chips.latest()).toEqual(['Done']);
    expect(chips.input()).toHaveValue('');
  });

  it('never splits without a separator, so "In Progress" stays one value', async () => {
    const chips = renderChips();

    await userEvent.setup().type(chips.input(), 'In Progress, Later{Enter}');

    expect(chips.latest()).toEqual(['In Progress, Later']);
  });

  it('with a separator, commits on comma or space as you type', async () => {
    const chips = renderChips({ splitOn: /[,\s]+/ });

    await userEvent.setup().type(chips.input(), 'ACME-1,ACME-2 ');

    expect(chips.latest()).toEqual(['ACME-1', 'ACME-2']);
  });

  it('splits a pasted run into normalised chips, each once', async () => {
    const user = userEvent.setup();
    const chips = renderChips({ splitOn: /[,\s]+/, normalize: (s) => s.toUpperCase() });

    await user.click(chips.input());
    await user.paste('acme-1, ACME-2\nacme-1');

    expect(chips.latest()).toEqual(['ACME-1', 'ACME-2']);
  });

  it('removes a chip', async () => {
    const chips = renderChips({ values: ['A', 'B'] });

    await userEvent.setup().click(screen.getByRole('button', { name: 'Remove A' }));

    expect(chips.latest()).toEqual(['B']);
  });

  it('stops at the limit', () => {
    renderChips({ values: ['A', 'B'], max: 2 });

    expect(screen.getByRole('button', { name: 'Add value' })).toBeDisabled();
    expect(screen.getByText(/at most 2 values/i)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/ValuePicker.test.tsx src/features/issues/ChipInput.test.tsx`
Expected: FAIL — modules do not exist.

- [ ] **Step 3: API shape and shared style**

In `src/JiraUnpacked.Web/src/api/issues.ts` replace `getFilterValues`:

```ts
export interface FilterValueOption {
  name: string;
  /** Issues in this project carrying the value — over the whole project, not the current filter. */
  count: number;
}

export interface FilterValues {
  field: string;
  values: FilterValueOption[];
  /** Issues in this project with no value for the field: the count behind (Empty). */
  emptyCount: number;
}

/** The values a dictionary field actually takes in this project, with counts. 400 for any other field. */
export const getFilterValues = (projectId: string, field: string) =>
  apiFetch<FilterValues>(
    `/api/projects/${projectId}/filter-values?field=${encodeURIComponent(field)}`);
```

Create `src/JiraUnpacked.Web/src/features/issues/styles.ts`:

```ts
/** The text/number/date input look shared by the filter editor's controls (matches ui/Select). */
export const INPUT_CLASS =
  'rounded-md border border-[var(--ju-border)] bg-[var(--ju-bg)] px-3 py-2 text-sm text-[var(--ju-text)] ' +
  'focus:outline-none focus:ring-2 focus:ring-[var(--ju-primary)] disabled:cursor-not-allowed disabled:opacity-60';
```

In `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` change the `pickable` line so the current dropdown keeps working until Task 9:

```tsx
  const pickable = wantsValueList && valueList.data ? valueList.data.values.map((v) => v.name) : null;
```

- [ ] **Step 4: Implement the pickers**

Create `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx`:

```tsx
import { useState, type SelectHTMLAttributes } from 'react';
import type { FilterValueOption } from '../../api/issues';
import { Badge, Select } from '../../ui';
import { INPUT_CLASS } from './styles';

/** How (Empty) reads wherever a value is shown. */
export const EMPTY_LABEL = '(Empty)';

// Option values are prefixed so that no real name — Jira allows almost any string — can collide
// with the (Empty) entry or the "choose" placeholder.
const VALUE_PREFIX = 'v:';
const EMPTY_VALUE = 'e:';

type SingleValueSelectProps = Omit<SelectHTMLAttributes<HTMLSelectElement>, 'value' | 'onChange'> & {
  /** The chosen name; '' = nothing chosen yet. */
  value: string;
  options: FilterValueOption[];
  emptyCount: number;
  onPick: (name: string) => void;
  /** (Empty) is not a value: the caller turns "is (Empty)" into "is empty". */
  onPickEmpty: () => void;
};

/** "is" / "is not" on a dictionary field: one value, (Empty) first, every entry with its count. */
export function SingleValueSelect({
  value, options, emptyCount, onPick, onPickEmpty, ...rest
}: SingleValueSelectProps) {
  // A value from a link that this project no longer has is still shown as what it is, not
  // replaced on screen by "— choose —" while the filter silently keeps it.
  const stale = value !== '' && !options.some((o) => o.name === value);
  return (
    <Select
      {...rest}
      value={value === '' ? '' : VALUE_PREFIX + value}
      onChange={(e) => {
        const v = e.target.value;
        if (v === EMPTY_VALUE) onPickEmpty();
        else if (v.startsWith(VALUE_PREFIX)) onPick(v.slice(VALUE_PREFIX.length));
      }}
    >
      <option value="">— choose —</option>
      <option value={EMPTY_VALUE}>{`${EMPTY_LABEL} (${emptyCount})`}</option>
      {stale && <option value={VALUE_PREFIX + value}>{`${value} (0)`}</option>}
      {options.map((o) => (
        <option key={o.name} value={VALUE_PREFIX + o.name}>{`${o.name} (${o.count})`}</option>
      ))}
    </Select>
  );
}

interface MultiValuePickerProps {
  /** Accessible name of the whole control, e.g. "Values for condition 1". */
  label: string;
  options: FilterValueOption[];
  emptyCount: number;
  /** null = (Empty). */
  selected: (string | number | null)[];
  onChange: (next: (string | null)[]) => void;
  max: number;
}

const keyOf = (v: string | null) => (v === null ? 'empty' : `v:${v}`);

/**
 * "is any of" / "is none of" on a dictionary field: chips for what is chosen, and a popover with
 * a search box and a checkbox per value — (Empty) first — each with its count. A checkbox list
 * makes choosing the same value twice impossible, which free text never could.
 *
 * A native <details> for the popover, like ColumnChooser: it opens, closes and takes focus with
 * no JavaScript and no outside-click handling to get wrong.
 */
export function MultiValuePicker({ label, options, emptyCount, selected, onChange, max }: MultiValuePickerProps) {
  const [query, setQuery] = useState('');

  // De-duplicated on the way in, so even a hand-edited link cannot show a value twice.
  const chosen = [...new Set(selected.map((v) => (v === null ? null : String(v))))];
  const isChosen = (v: string | null) => chosen.includes(v);
  const atLimit = chosen.length >= max;
  const toggle = (v: string | null) =>
    onChange(isChosen(v) ? chosen.filter((x) => x !== v) : [...chosen, v]);

  const needle = query.trim().toLowerCase();
  const matches = options.filter((o) => o.name.toLowerCase().includes(needle));
  const showEmpty = needle === '' || EMPTY_LABEL.toLowerCase().includes(needle);

  const choice = (v: string | null, text: string, count: number) => (
    <label key={keyOf(v)} className="flex items-center gap-2 text-sm text-[var(--ju-text)]">
      <input type="checkbox" checked={isChosen(v)} disabled={!isChosen(v) && atLimit} onChange={() => toggle(v)} />
      <span className="flex-1">{text}</span>{' '}
      <span className="text-xs text-[var(--ju-muted)]">{count}</span>
    </label>
  );

  return (
    <div role="group" aria-label={label} className="flex flex-col gap-1">
      <span aria-hidden="true" className="text-sm font-medium text-[var(--ju-text)]">{label}</span>
      {chosen.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {chosen.map((v) => (
            <Badge key={keyOf(v)}>
              {v ?? EMPTY_LABEL}
              <button type="button" className="ml-1" aria-label={`Remove ${v ?? EMPTY_LABEL}`} onClick={() => toggle(v)}>
                ×
              </button>
            </Badge>
          ))}
        </div>
      )}
      <details className="relative">
        <summary className="cursor-pointer list-none rounded-[var(--ju-radius)] border border-[var(--ju-border)] px-3 py-2 text-sm text-[var(--ju-text)]">
          Choose values
        </summary>
        <div className="absolute z-10 mt-1 flex max-h-72 w-64 flex-col gap-2 overflow-auto rounded-[var(--ju-radius)] border border-[var(--ju-border)] bg-[var(--ju-surface)] p-3 shadow-lg">
          <input
            type="search"
            aria-label={`Search ${label}`}
            className={INPUT_CLASS}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          {showEmpty && choice(null, EMPTY_LABEL, emptyCount)}
          {matches.map((o) => choice(o.name, o.name, o.count))}
          {!showEmpty && matches.length === 0 && (
            <p className="text-xs text-[var(--ju-muted)]">No matching values</p>
          )}
        </div>
      </details>
      {atLimit && <p className="text-xs text-[var(--ju-muted)]">You can add at most {max} values.</p>}
    </div>
  );
}
```

- [ ] **Step 5: Implement the chip input**

Create `src/JiraUnpacked.Web/src/features/issues/ChipInput.tsx`:

```tsx
import { useState, type ClipboardEvent, type KeyboardEvent } from 'react';
import { Badge, Button, Field } from '../../ui';
import { INPUT_CLASS } from './styles';

export interface ChipInputProps {
  id: string;
  label: string;
  values: string[];
  onChange: (next: string[]) => void;
  max: number;
  /**
   * Where a typed or pasted run breaks into separate values. Must not be global (/g), because
   * it is also used with .test(). Omitted = never split: the whole entry is one value, which is
   * right for dictionary names — "In Progress" is one status.
   */
  splitOn?: RegExp;
  /** e.g. upper-casing Jira keys, so "acme-1" and "ACME-1" are one chip. */
  normalize?: (value: string) => string;
}

const same = (s: string) => s;

/**
 * Free-text list entry: each committed value becomes a chip. Enter (or a separator key, when
 * there is one) commits; a pasted run is split into chips in one go. Values are trimmed,
 * normalised and de-duplicated, so the list can never carry the same value twice.
 */
export function ChipInput({ id, label, values, onChange, max, splitOn, normalize = same }: ChipInputProps) {
  const [draft, setDraft] = useState('');
  const shown = [...new Set(values)];
  const atLimit = shown.length >= max;

  const commit = (text: string) => {
    const next = [...shown];
    for (const token of splitOn ? text.split(splitOn) : [text]) {
      const value = normalize(token.trim());
      if (value && !next.includes(value) && next.length < max) next.push(value);
    }
    if (next.length !== shown.length) onChange(next);
    setDraft('');
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter' || (splitOn !== undefined && splitOn.test(event.key))) {
      event.preventDefault();
      commit(draft);
    }
  };

  const handlePaste = (event: ClipboardEvent<HTMLInputElement>) => {
    if (!splitOn) return;
    const text = event.clipboardData.getData('text/plain');
    if (!splitOn.test(text.trim())) return;   // a single value pastes into the box as usual
    event.preventDefault();
    commit(draft + text);
  };

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-end gap-2">
        <Field label={label} id={id}>
          <input
            type="text"
            className={INPUT_CLASS}
            value={draft}
            disabled={atLimit}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={handleKeyDown}
            onPaste={handlePaste}
          />
        </Field>
        <Button type="button" variant="secondary" onClick={() => commit(draft)} disabled={atLimit}>
          Add value
        </Button>
      </div>
      {atLimit && <p className="text-xs text-[var(--ju-muted)]">You can add at most {max} values.</p>}
      {shown.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {shown.map((v) => (
            <Badge key={v}>
              {v}
              <button
                type="button"
                aria-label={`Remove ${v}`}
                className="ml-1"
                onClick={() => onChange(shown.filter((x) => x !== v))}
              >
                ×
              </button>
            </Badge>
          ))}
        </div>
      )}
    </div>
  );
}
```

- [ ] **Step 6: Update the mocks that return the old shape**

In `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`, `value picker` describe:

```tsx
  function mockValues(values: string[]) {
    server.use(http.get('/api/projects/p1/filter-values', () =>
      HttpResponse.json({ field: 'status', values: values.map((name) => ({ name, count: 1 })), emptyCount: 0 })));
  }
```

In the same describe's first test change the option lookup to `within(control).getByRole('option', { name: /^In Progress/ })`, and in `does not ask for a value list when the operator takes no value` change the handler's body to `HttpResponse.json({ field: 'status', values: [{ name: 'Backlog', count: 1 }], emptyCount: 0 })`.

In `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx` (`exports the filter, sort and columns on screen`):

```tsx
    server.use(http.get('/api/projects/p1/filter-values', () =>
      HttpResponse.json({ field: 'status', values: [{ name: 'Backlog', count: 1 }], emptyCount: 0 })));
```

- [ ] **Step 7: Run the web suite, typecheck and lint**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): multi-select value picker with search, counts and (Empty); chip input"
```

---

### Task 9: ConditionRow v2 — worded operators, (Empty), pickers, key chips

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.test.ts`
- Modify (rewrite): `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`
- Test: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`

**Interfaces:**
- Consumes: Task 8's `SingleValueSelect`, `MultiValuePicker`, `ChipInput`, `INPUT_CLASS`, `getFilterValues`; Task 5's `takesList`, `takesNoValue`; Task 7's `ConditionRow` props (unchanged here).
- Produces: `OPERATOR_LABELS: Record<FilterOperator, string>`, `operatorLabel(op: FilterOperator): string` in `filterCatalogHelpers.ts`.
- Produces (value control by field type/operator, spec §6): dict `Eq`/`Neq` → `SingleValueSelect` (choosing (Empty) emits `IsNull`/`IsNotNull`); dict `In`/`NotIn` → `MultiValuePicker`; jiraKey `In`/`NotIn` → `ChipInput` splitting on `/[,\s]+/`, upper-cased; picker unavailable → text input (`Eq`/`Neq`) or `ChipInput` without splitting (`In`/`NotIn`), preserving an (Empty) that arrived in a link; `IsNull`/`IsNotNull` → no control; decimal → number; date → date; other text → text. Accessible names: `Field for condition N`, `Operator for condition N`, `Value for condition N`, `Values for condition N` (multi picker).

- [ ] **Step 1: Write the failing tests**

Append to `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.test.ts` (add `operatorLabel` to its import):

```ts
describe('operatorLabel', () => {
  it('names every operator in words, as the spec fixes them', () => {
    expect(['Eq', 'Neq', 'In', 'NotIn', 'IsNull', 'IsNotNull', 'Lt', 'Lte', 'Gt', 'Gte', 'Contains', 'NotContains', 'StartsWith']
      .map((op) => operatorLabel(op as Parameters<typeof operatorLabel>[0])))
      .toEqual(['is', 'is not', 'is any of', 'is none of', 'is empty', 'is not empty', '<', '≤', '>', '≥',
        'contains', 'does not contain', 'starts with']);
  });
});
```

Create `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`:

```tsx
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import type { FilterCondition } from '../../api/issues';
import { ConditionRow } from './ConditionRow';

function renderRow(initial: FilterCondition) {
  const seen: FilterCondition[] = [];
  function Harness() {
    const [condition, setCondition] = useState(initial);
    return (
      <ConditionRow
        projectId="p1"
        catalog={FILTER_CATALOG}
        condition={condition}
        label="1"
        onChange={(next) => { seen.push(next); setCondition(next); }}
      />
    );
  }
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Harness />
    </QueryClientProvider>,
  );
  return { last: () => seen.at(-1) };
}

function mockValues() {
  server.use(http.get('/api/projects/p1/filter-values', () => HttpResponse.json({
    field: 'status',
    values: [{ name: 'Backlog', count: 42 }, { name: 'In Progress', count: 5 }],
    emptyCount: 7,
  })));
}

function failValues() {
  server.use(http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })));
}

const waitForPicker = () =>
  waitFor(() => expect(screen.getByLabelText('Value for condition 1').tagName).toBe('SELECT'));

describe('ConditionRow', () => {
  it('names operators in words, in the catalog’s order', () => {
    renderRow({ field: 'jiraKey', op: 'Eq', value: '' });

    const ops = within(screen.getByLabelText('Operator for condition 1')).getAllByRole('option').map((o) => o.textContent);
    expect(ops).toEqual(['is', 'is any of', 'is none of', 'contains', 'starts with']);
  });

  it('offers (Empty) first, then each value with its count', async () => {
    mockValues();
    renderRow({ field: 'status', op: 'Eq', value: '' });
    await waitForPicker();

    const options = within(screen.getByLabelText('Value for condition 1')).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['— choose —', '(Empty) (7)', 'Backlog (42)', 'In Progress (5)']);
  });

  it('turns "is (Empty)" into "is empty" and "is not (Empty)" into "is not empty"', async () => {
    mockValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'Eq', value: '' });
    await waitForPicker();

    await user.selectOptions(screen.getByLabelText('Value for condition 1'), '(Empty) (7)');
    expect(row.last()).toEqual({ field: 'status', op: 'IsNull' });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Neq');
    await user.selectOptions(screen.getByLabelText('Value for condition 1'), '(Empty) (7)');
    expect(row.last()).toEqual({ field: 'status', op: 'IsNotNull' });
  });

  it('picks several values for "is any of", (Empty) included, and never one twice', async () => {
    mockValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'In', values: [] });

    await user.click(await screen.findByText('Choose values'));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));
    await user.click(screen.getByRole('checkbox', { name: /^\(empty\)/i }));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));

    expect(row.last()).toEqual({ field: 'status', op: 'In', values: [null, 'Backlog'] });
  });

  it('keeps the chosen list when flipping "is any of" to "is none of"', async () => {
    mockValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'In', values: ['Backlog'] });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'NotIn');

    expect(row.last()).toEqual({ field: 'status', op: 'NotIn', values: ['Backlog'] });
  });

  it('falls back to typing when the value list cannot be loaded, without splitting names', async () => {
    failValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'In', values: [] });

    await user.type(await screen.findByLabelText('Value for condition 1'), 'In Progress{Enter}');

    expect(row.last()).toEqual({ field: 'status', op: 'In', values: ['In Progress'] });
  });

  it('keeps an (Empty) that arrived in a link when the fallback is used', async () => {
    failValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'sprint', op: 'In', values: [null] });

    await user.type(await screen.findByLabelText('Value for condition 1'), 'S1{Enter}');

    expect(row.last()).toEqual({ field: 'sprint', op: 'In', values: [null, 'S1'] });
  });

  it('splits a pasted list of keys into upper-cased chips, once each', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'jiraKey', op: 'NotIn', values: [] });

    await user.click(screen.getByLabelText('Value for condition 1'));
    await user.paste('acme-1, ACME-2\nacme-1');

    expect(row.last()).toEqual({ field: 'jiraKey', op: 'NotIn', values: ['ACME-1', 'ACME-2'] });
  });

  it('asks for no value on "is not empty"', () => {
    renderRow({ field: 'summary', op: 'IsNotNull' });

    expect(screen.queryByLabelText('Value for condition 1')).toBeNull();
  });

  it('sends a typed value for "does not contain"', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'summary', op: 'NotContains', value: '' });

    await user.type(screen.getByLabelText('Value for condition 1'), 'spike');

    expect(row.last()).toEqual({ field: 'summary', op: 'NotContains', value: 'spike' });
  });

  it('marks a field the catalog no longer has, instead of silently showing another', () => {
    renderRow({ field: 'retired', op: 'Eq', value: 'x' });

    const select = screen.getByLabelText('Field for condition 1');
    expect(select).toHaveValue('retired');
    expect(select).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('alert')).toHaveTextContent(/can no longer be filtered on/i);
  });
});
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/ConditionRow.test.tsx src/features/issues/filterCatalogHelpers.test.ts`
Expected: FAIL — `operatorLabel` does not exist; options read `Eq`, no counts, no multi picker.

- [ ] **Step 3: Operator labels**

Append to `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.ts`:

```ts
/** What the editor calls each operator. The wire keeps the enum names. */
export const OPERATOR_LABELS: Record<FilterOperator, string> = {
  Eq: 'is',
  Neq: 'is not',
  In: 'is any of',
  NotIn: 'is none of',
  IsNull: 'is empty',
  IsNotNull: 'is not empty',
  Lt: '<',
  Lte: '≤',
  Gt: '>',
  Gte: '≥',
  Contains: 'contains',
  NotContains: 'does not contain',
  StartsWith: 'starts with',
};

export const operatorLabel = (op: FilterOperator): string => OPERATOR_LABELS[op] ?? op;
```

- [ ] **Step 4: Rewrite ConditionRow**

Replace `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` with:

```tsx
import type { ChangeEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { FilterCatalog, FilterCondition, FilterOperator } from '../../api/issues';
import { getFilterValues } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Field, Select } from '../../ui';
import { ChipInput } from './ChipInput';
import {
  fieldNames, isDictionaryField, operatorLabel, operatorsFor, resetOperatorIfDisallowed, typeFor,
} from './filterCatalogHelpers';
import { takesList, takesNoValue } from './filterTree';
import { INPUT_CLASS } from './styles';
import { MultiValuePicker, SingleValueSelect } from './ValuePicker';

interface ConditionRowProps {
  /** Only used to fetch this project's values for the pickers. */
  projectId: string;
  condition: FilterCondition;
  catalog: FilterCatalog;
  onChange: (next: FilterCondition) => void;
  /** Position in the tree, e.g. "1" or "2.1" — part of every control's accessible name. */
  label: string;
}

const JIRA_KEY_SEPARATORS = /[,\s]+/;
const toUpper = (s: string) => s.toUpperCase();

/**
 * One field / operator / value row. Every transition rebuilds the condition from scratch with
 * only the key its new operator takes, so a stale value/values key of the wrong shape never
 * rides along to a 400. The row's identity (id, on/off) belongs to GroupEditor, which puts it
 * back (withIdentityOf).
 */
export function ConditionRow({ projectId, condition, catalog, onChange, label }: ConditionRowProps) {
  const { field, op } = condition;
  const fieldType = typeFor(catalog, field);
  const known = fieldType !== undefined;
  const isDict = isDictionaryField(catalog, field);

  const handleFieldChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const nextField = event.target.value;
    const nextOp = resetOperatorIfDisallowed(catalog, nextField, op);
    // A value valid for the old field rarely is for the new one, so it is dropped, not carried.
    onChange(takesList(nextOp) ? { field: nextField, op: nextOp, values: [] } : { field: nextField, op: nextOp });
  };

  const handleOperatorChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const nextOp = event.target.value as FilterOperator;
    if (takesNoValue(nextOp)) {
      onChange({ field, op: nextOp });
    } else if (takesList(nextOp)) {
      // "is any of" <-> "is none of" keeps the list: flipping the sense is the common edit.
      onChange({ field, op: nextOp, values: takesList(op) ? [...(condition.values ?? [])] : [] });
    } else {
      const keep = !takesList(op) && !takesNoValue(op) && condition.value !== null;
      onChange({ field, op: nextOp, value: keep ? condition.value ?? '' : '' });
    }
  };

  // Only dictionary fields have a value list, and only operators that take a value need one.
  const wantsValueList = isDict && !takesNoValue(op);
  const valueList = useQuery({
    queryKey: queryKeys.filterValues(projectId, field),
    queryFn: () => getFilterValues(projectId, field),
    enabled: wantsValueList,
    // A project's set of values changes only when a sync runs.
    staleTime: 5 * 60 * 1000,
  });
  // The list is an affordance, not a gate: while it loads, or if it fails, every control falls
  // back to typing — a broken picker must not make a working filter unusable.
  const picked = wantsValueList ? valueList.data : undefined;

  const valueId = `condition-${label}-value`;
  const valueLabel = `Value for condition ${label}`;
  const textValue = typeof condition.value === 'string' ? condition.value : '';

  const renderValueControl = () => {
    if (takesNoValue(op)) return null;

    if (takesList(op)) {
      const values = condition.values ?? [];
      if (picked) {
        return (
          <MultiValuePicker
            label={`Values for condition ${label}`}
            options={picked.values}
            emptyCount={picked.emptyCount}
            selected={values}
            max={catalog.maxInValues}
            onChange={(next) => onChange({ field, op, values: next })}
          />
        );
      }
      const isKey = field === 'jiraKey';
      // Typing cannot produce (Empty), but one that arrived in a link is kept, not silently lost.
      const keepsEmpty = values.includes(null);
      return (
        <ChipInput
          key={field}
          id={valueId}
          label={valueLabel}
          values={values.filter((v): v is string | number => v !== null).map(String)}
          max={catalog.maxInValues - (keepsEmpty ? 1 : 0)}
          splitOn={isKey ? JIRA_KEY_SEPARATORS : undefined}
          normalize={isKey ? toUpper : undefined}
          onChange={(next) => onChange({ field, op, values: keepsEmpty ? [null, ...next] : next })}
        />
      );
    }

    if (isDict && picked) {
      return (
        <Field label={valueLabel} id={valueId}>
          <SingleValueSelect
            value={textValue}
            options={picked.values}
            emptyCount={picked.emptyCount}
            onPick={(name) => onChange({ field, op, value: name })}
            // "is (Empty)" is "is empty": the operator changes, so a null value never reaches the wire.
            onPickEmpty={() => onChange({ field, op: op === 'Neq' ? 'IsNotNull' : 'IsNull' })}
          />
        </Field>
      );
    }

    if (fieldType === 'Decimal') {
      return (
        <Field label={valueLabel} id={valueId}>
          <input
            type="number"
            className={INPUT_CLASS}
            value={condition.value ?? ''}
            onChange={(e) => onChange({ field, op, value: e.target.value === '' ? '' : Number(e.target.value) })}
          />
        </Field>
      );
    }

    return (
      <Field label={valueLabel} id={valueId}>
        <input
          type={fieldType === 'DateTime' ? 'date' : 'text'}
          className={INPUT_CLASS}
          value={textValue}
          onChange={(e) => onChange({ field, op, value: e.target.value })}
        />
      </Field>
    );
  };

  return (
    <div className="flex flex-wrap items-end gap-2">
      <Field
        label={`Field for condition ${label}`}
        id={`condition-${label}-field`}
        error={known ? undefined : `'${field}' can no longer be filtered on, so this condition is not applied.`}
      >
        <Select value={field} onChange={handleFieldChange}>
          {!known && <option value={field} disabled>{field}</option>}
          {fieldNames(catalog).map((f) => (
            <option key={f} value={f}>{f}</option>
          ))}
        </Select>
      </Field>

      <Field label={`Operator for condition ${label}`} id={`condition-${label}-operator`}>
        <Select value={op} onChange={handleOperatorChange}>
          {operatorsFor(catalog, field).map((o) => (
            <option key={o} value={o}>{operatorLabel(o)}</option>
          ))}
        </Select>
      </Field>

      {renderValueControl()}
    </div>
  );
}
```

- [ ] **Step 5: Update the FilterBuilder tests that read operator names**

In `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`, `offers only the operators the chosen field allows`:

```tsx
    expect(ops).toEqual(['is', 'is any of', 'is none of', 'contains', 'starts with']);
```

(Every `selectOptions(…, 'In' | 'IsNull' | 'Gte' | 'Neq' | 'Or')` call selects by option **value**, which is still the enum name, so those tests are unchanged. `sends values (not value) for In`, `blocks a 101st In value` and `does not bleed …` now exercise `ChipInput` as the fallback — no `filter-values` handler is registered in them.)

- [ ] **Step 6: Run the web suite, typecheck, lint and build**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test && npm run build`
Expected: PASS; the build writes to `src/JiraUnpacked.Api/wwwroot` (git-ignored).

- [ ] **Step 7: Confirm the dependency rule held**

Run: `cd C:/jw-filters && git diff origin/develop -- src/JiraUnpacked.Web/package.json src/JiraUnpacked.Web/package-lock.json`
Expected: no output.

- [ ] **Step 8: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): worded operators, (Empty) as is empty, pickers and key chips in each row"
```

---

## Final verification (after Task 9, before handing back)

- [ ] `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_filters dotnet test JiraUnpacked.sln -v q` → PASS.
- [ ] `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test && npm run build` → PASS.
- [ ] `git -C C:/jw-filters status` clean; `git -C C:/jw-filters log --oneline origin/develop..` shows the spec commit plus nine task commits. Nothing pushed.

---

## Self-Review

**Spec coverage.**
§1 empty rule — Task 1 (translator + endpoint tests), wire null verified by `A_json_null_inside_values_arrives_as_a_Null_element`; editor normalisation of Eq/Neq (Empty) — Task 5 `toWireCondition` + Task 9 `onPickEmpty`. §2 operators/fields/labels — Task 1 (server sets, display order), Task 9 (labels), jiraKey chips + paste split — Tasks 8–9, NotContains — Task 1. §3 disabled — Task 2 (translator, endpoint, saved filters), Task 5 (prune/count), Task 7 (toggles). §4 counts — Task 3 (server), Task 8 (pickers). §5 CSV — Task 4 (server), Task 6 (client sends tree). §6 state model/ids/prune/URL — Tasks 5–6; GroupEditor, depth, limits, duplicate, disabled opacity/`aria-pressed`, nested indent with accent border, Clear all — Task 7; value controls table + fallback — Tasks 8–9. §7 retired field — Task 6 (pruned) + Task 9 (invalid state). §8 testing — every listed case has a test in Tasks 1–9.

**Placeholder scan.** No TBD/TODO; every code step shows the code; edits to existing files name the exact block replaced.

**Type consistency.** `EditorGroup`/`EditorCondition`/`TreeCounts` (Task 5) are the types used by `useListState` (6), `FilterBuilder`/`GroupEditor` (6–7). `ConditionRow` takes/returns `FilterCondition` with `label: string` from Task 7 on; `GroupEditor` re-attaches identity with `withIdentityOf`. `getFilterValues` returns `FilterValues` from Task 8; Task 8 adapts the old `ConditionRow` in one line so every task ends green. `exportIssuesCsv(id, { filter, … })` (6) matches `IssueList`. Server: `FilterCondition(..., bool? Disabled = null)`/`FilterGroup(..., bool? Disabled = null)` keep all existing positional constructions compiling.

**Review Focus.** Five lines above, each with its pinning test named in the owning task (6, 5/6/4, 8/9, 8, 4).
