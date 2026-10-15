# History Conditions (Was / Changed / Number of Changes) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Filter issues by what happened to them — "status was Blocked", "Sprint changed during the last 30 days", "assignee changed by Alice", and "Sprint changed ≥ 3 times" — answered on the server from the ingested changelog, at query time, for the list, the export, metrics, gap checks and saved views alike.

**Architecture:** A pseudo-field `history` (catalog type `History`) with three operators appended to `FilterOperator` (`Was`, `Changed`, `ChangeCount`) and an object value. `IssueFilterCompiler` hands the translator its tenant-scoped `_db.IssueChangeHistory` through a new optional trailing `history` parameter on `IIssueFilterTranslator.BuildAsync`, and a new partial `IssueFilterTranslator.History.cs` builds **correlated `Any`/`Count` subqueries** over it (captured the way a C# closure captures a `DbSet`, so EF inlines the query root with its tenant filter). Values, field names, window bounds and counts are EF parameters. Two read-only endpoints feed the SPA's pickers. The SPA gains a `historyShapes` module (readers, defaults, completeness), shared date controls extracted from `ConditionRow`, a `DuringControl`, a `HistoryFieldSelect`, and `HistoryConditionControls` on a second line of the row.

**Tech Stack:** .NET 8, EF Core 8.0.11 + Npgsql 8.0.11, xUnit 2.5; React 19, react-router-dom 7, @tanstack/react-query 5, Vitest + Testing Library + user-event 14 + MSW 2, Tailwind 4.

**Spec:** `docs/superpowers/specs/2026-10-07-history-conditions-design.md`

## Global Constraints

- **Work only in the worktree `C:/jw-filters`** (branch `feat/history-conditions`, based on `origin/develop`, which already has filter builder v2, saved views and richer filter values). Never read or write `C:\Users\AliaksandrHubanau\Private\Claude\JiraUnpacked` — other sessions use that checkout. Do not push.
- **Integration tests use their own database:** every `dotnet test` run sets `JU_TEST_DATABASE=ju_test_history` (Postgres = the `jiraunpacked-db` container on `localhost:5432`, already running). Bash: `JU_TEST_DATABASE=ju_test_history dotnet test …`. PowerShell: `$env:JU_TEST_DATABASE='ju_test_history'; dotnet test …`. The suite never truncates; a shared database collides with parallel branches. Integration tests that read with `IgnoreQueryFilters` must scope by their own `JiraProjectId` (none in this plan do).
- **Web setup:** `node_modules` already exists in `src/JiraUnpacked.Web`; if it is missing, run `npm ci` there once.
- SPA keeps **exactly four runtime deps**: `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`. **No new dependencies of any kind** (runtime or dev, .NET or npm).
- `FilterOperator` gains `Was`, `Changed`, `ChangeCount` **appended** after `InPeriod`. Enums stay serialised by name.
- New pseudo-field **`history`**, catalog type **`History`**, operators `Was`, `Changed`, `ChangeCount`. Its value is a JSON object:
  - `Was`: `{ "historyField": "status", "values": ["Blocked", null] }` — some changelog row for that field has a `FromValue` or `ToValue` matching one of the values, **or** (for `status, assignee, priority, issuetype, Sprint`) the issue's current value matches.
  - `Changed`: `{ "historyField": "Sprint", "from"?: [..], "to"?: [..], "by"?: [..], "during"?: Window }` — some row for that field satisfies every part given. No parts = changed at all.
  - `ChangeCount`: `{ "historyField": "Sprint", "compare": "Gte", "count": 3, "during"?: Window }` — the number of rows for that field (in the window, if given) compares to `count`; no rows = 0.
- `historyField`: required, non-blank, ≤ **100** chars, matched **exactly** (case-sensitive). A name no row uses matches nothing (and `ChangeCount Eq 0` then matches everything).
- Value matching (`values`, `from`, `to`): **equal**, or one **element** of a `", "`-joined list (`v`, `v, …`, `…, v`, `…, v, …`); "PLAT 10" never matches "PLAT 101". `null` = (Empty) = a null or empty stored value. Exact (no case folding). Lists ≤ **100** entries, each ≤ **255** chars. `by`: author display names, exact; `null` not allowed.
- `compare` ∈ `Eq, Neq, Gt, Gte, Lt, Lte`; `count` integer **0…10000**.
- Window (`during`): `{ "kind": "inLast", "amount": 30, "unit": "days" }` (same rules as `InLast`) · `{ "kind": "inPeriod", "period": "lastMonth", "timeZone": "Europe/Warsaw" }` (same as `InPeriod`) · `{ "kind": "between", "from": "2026-09-01", "to": "2026-09-30" }` (same as date `Between`: either bound open, date-only `to` includes the whole day). Evaluated with the translator's `IClock`, reusing `RelativeDates`.
- Guards: history conditions count as conditions **and** at most **5 enabled history conditions per filter**; exposed in the catalog as **`maxHistoryConditions`**.
- Translation: **correlated subqueries**, never materialising history in memory. List-element matching from `==`, `StartsWith`, `EndsWith`, `Contains` on parameters — no regex, no user string in SQL text. Window bounds and the clock-derived cutoff are EF **parameters** (`Parameter` helper), not constants.
- **Performance gate:** the generated SQL for each operator runs with `EXPLAIN (ANALYZE, BUFFERS)` against the **local dev database `jiraunpacked`** (container `jiraunpacked-db`), its **largest project by issue count**, **read-only** (reader role `jiraunpacked_reader` + `default_transaction_read_only=on`; never write to the dev DB), and must finish under **2 s** for a whole-project list query (page 1, count included). Only if it misses: composite index `(JiraProjectId, JiraIssueId, Field, ChangedAt)` in a migration generated with `dotnet ef migrations add … -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Infrastructure` (**`-s src/JiraUnpacked.Api` does not work in this repo**).
- Endpoints, both tenant-scoped, 404 for an unknown/foreign project: `GET /api/projects/{id}/history-fields` → `{ fields: [{ name, count }] }` (distinct `Field`, row counts, most-changed first, capped at **200**); `GET /api/projects/{id}/history-values?field=<historyField>&kind=value|author` → `{ values: [{ name, count }], emptyCount }` (for `value`, distinct elements of From/To values, lists split on `", "` in memory after a grouped, capped read; for `author`, distinct authors; sorted by name, capped at **1000**; unknown field → empty list, not 400).
- SPA copy, verbatim: field entry **`History of a field…`** (last in the field select); operators `Was` **was** · `Changed` **changed** · `ChangeCount` **number of changes**; compare labels **`=` `≠` `>` `≥` `<` `≤`**; count default **`≥ 2`**; During: **`any time`** / **`in the last`** [N] [unit] / **`in period`** [period] / **`between`** [from] [to].
- Completeness: `historyField` required; `Was` needs ≥ 1 value; `ChangeCount` needs an integer count 0–10000 and a compare; `Changed` is complete once the field is chosen; an incomplete `during` makes the condition incomplete.
- URL / saved views: the object value round-trips canonically (fixed key order) through `readCondition`, `toSavable` and `isModified`.
- All reads through the injected tenant-scoped `AppDbContext`. No `IgnoreQueryFilters`, no `FromSql` in `src/`.
- `src/ui/` imports neither `src/api/` nor `src/auth/`; new modules live in `src/features/issues/`. Colours only through `--ju-*` CSS variables. Only `ju.username` and `ju.theme` may go to `localStorage`.
- Out of scope: comparing to the previous value, time-in-status filters, a "Changes" column, history of fields the changelog does not report.
- Every commit message ends with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (second `-m`).

## Review Focus

1. **Another tenant's changelog rows under the same project id and issue id.** The subquery's source must carry the EF tenant query filter, so such rows neither match nor count (Postgres RLS is the second layer, not the only one). Expected: invisible. Pinned by Task 1 (`Changed_is_a_correlated_exists_over_the_tenant_filtered_history` asserts the filter *inside* `EXISTS`; `Another_tenants_history_never_matches`) and Task 3 (`Another_tenants_history_never_counts`).
2. **LIKE wildcards and near-miss names in a value** — `100%_done`, "PLAT 10" vs "PLAT 101", a name that is the first, middle or last element of `"a, b, c"`. Expected: literal, element-exact matching in SQL, not only in memory. Pinned by Task 2 (`A_value_matches_the_whole_stored_value_or_one_element_of_its_list`, `Wildcards_in_a_value_are_literal_in_sql` against Postgres).
3. **Never-changed issues and `ChangeCount`** — `Eq 0`, `Lt 1`, a field name no row uses, a window that excludes every row. Expected: they count 0 and match. Pinned by Task 3 (`ChangeCount_compares_the_number_of_rows_and_never_changed_counts_as_zero`, `ChangeCount_of_a_field_no_row_uses_is_zero_for_everyone`, the end-to-end `ChangeCount_and_windows_end_to_end…`).
4. **EF's compiled-query cache.** Field names, values, window bounds (moving with the clock) and counts are parameters, so two requests with different values produce the same statement text. Pinned by Task 2 (`Values_travel_as_parameters…`) and Task 3 (`ChangeCount_is_a_correlated_count_and_its_window_and_count_are_parameters`).
5. **A link or stored view whose history value is not exactly what the editor writes** — keys in another order, explicit nulls, empty optional lists, an unreadable `during`, a key another operator takes. Expected: good values read canonically (the view is not "modified"); unreadable ones leave the row unfinished — never silently broadened to "any time", never a 400 loop. Pinned by Task 6 (`readHistoryValue` tests, `round-trips history values … a malformed one stays unfinished`, the `viewState` history test).
6. **The 2 s gate on the real data shape** (~2.1M changelog rows, ~93k issues). Pinned by Task 5's opt-in `HistoryQueryPlanTests` and its recorded timings.

## Decisions this plan makes where the spec is silent

- **How the translator gets the source without breaking callers.** Both `IIssueFilterTranslator.BuildAsync` overloads gain a *trailing optional* `IQueryable<IssueChangeHistory>? history = null` **after** `ct`, so every existing positional call (`BuildAsync(x, resolver, ct)`, the test fakes, `RicherFilterTestData`'s override) compiles unchanged. `IssueFilterCompiler` passes `_db.IssueChangeHistory`. Without a source, a history condition is a 400 (`History conditions are not available here.`) — refused, never silently dropped. Unit tests pass a `List<>.AsQueryable()` and run the predicate in memory.
- **The subquery's source is captured like a closure variable** (`Parameter(history)` — a field read off a captured object). EF evaluates it and inlines the `DbSet`'s query root (tenant filter included), exactly as it does for `_db.Statuses` in `IssueQueryService`'s projection. Task 1's spike proves it; its fallback is `history.Expression`.
- **Correlation** is `h.JiraProjectId == i.JiraProjectId && h.JiraIssueId == i.JiraIssueId` (Jira issue ids are per site; the project pins it).
- **`""` is (Empty)** in every list, like `null` (Jira stores "no value" as either). `by` refuses both.
- **Unknown keys in a history value are a 400** naming the key — except an explicit `null`, which means "absent". An *empty* optional list (`"from": []`) is a 400 too ("leave it out to accept any"); the SPA omits empty lists.
- **`between` window with both bounds null is a 400** (as date `Between`); the SPA treats it as unfinished. `from > to` is unfinished in the SPA, a 400 on the server.
- **Current-value `Was` resolves names tenant-wide** through the compiler's resolver (the dictionary filters' own path), and the issue query's project filter keeps the match to this project. Mapping: `status→status`, `assignee→assignee`, `priority→priority`, `issuetype→issueType`, `Sprint→sprint`. Resolved ids are sent as a parameter.
- **`history` is never a column**: `SavedFilterService` refuses it in `columns`; it is not sortable (`SortFields` is its own list).
- **Value-list counts:** for `kind=value`, a value's count is the number of changelog row *sides* (From and To separately) holding it as an element; `emptyCount` counts null/empty sides. For `kind=author`, rows per author; `emptyCount` = rows with no author. The raw read is grouped in SQL and capped at the **5000** most frequent raw strings per side before splitting. Sorted case-insensitively, then ordinally. `kind` defaults to `value`; a blank `field` or unknown `kind` is a 400. Fields: most-changed first, then by name.
- **SPA defaults:** choosing **History of a field…** starts at `was` with no field chosen; `number of changes` starts at `≥ 2`; During `in the last` starts at **30 days**, `in period` at **this week** in the browser's zone (captured, shown as a hint), `between` with both bounds empty. Switching between history operators keeps `historyField`, and keeps `during` between `changed` and `number of changes`.
- **The cap in the editor** also blocks duplicating, or switching back on, a history row at the cap (`Limit: 5 history conditions`); on non-history rows the field select's last option is disabled and reads `History of a field… (limit: 5)`.
- **SPA labels:** `History field for condition N`, `Values for condition N`, `From values for condition N`, `To values for condition N`, `Authors for condition N`, `Compare for condition N`, `Count for condition N`, `During for condition N`, and the window's parts `During: amount|unit|period|from|to for condition N`.
- **Reading a history value from a link is all-or-nothing**: a malformed part (unknown key, non-list, bad `during`, unknown compare, non-number count) leaves the whole value unreadable, so the row is unfinished — not "the readable part", which could mean more than the link said.
- **`copyValue` deep-copies** object values (history values hold arrays), via a JSON round trip.
- **The perf gate is a committed, opt-in test** (`JU_PERF_DB` connection string; a no-op without it) that re-runs every statement `IssueQueryService` sends under `EXPLAIN (ANALYZE, BUFFERS)`. If an index is needed, it is measured on a **copy** of the dev database (`ju_perf_history`, made with `pg_dump`, dropped afterwards), because the dev DB is never written.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs` | + `Was`, `Changed`, `ChangeCount` |
| `src/JiraUnpacked.Domain/Abstractions/IFilterCatalogService.cs` | `FilterCatalogView.MaxHistoryConditions` |
| `src/JiraUnpacked.Domain/Abstractions/IHistoryValueService.cs` | **new** — value-list contract |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs` | `FieldType.History`; the `history` entry |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` | `history` parameter, history budget, dispatch |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.History.cs` | **new** — Was / Changed / ChangeCount, value matching, windows |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.Dates.cs` | readers shared with windows (`ReadSince`, `ReadPeriod`, `ReadDateRange`, `DateRangeTest`) |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterCompiler.cs` | passes `_db.IssueChangeHistory` |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs` | display order; `MaxHistoryConditions` |
| `src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs` | `history` is not a column |
| `src/JiraUnpacked.Infrastructure/Reporting/HistoryValueService.cs` | **new** — history-fields / history-values |
| `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` | registers `IHistoryValueService` |
| `src/JiraUnpacked.Api/Controllers/IssuesController.cs` | the two endpoints |
| `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueChangeHistoryConfiguration.cs` + generated migration | **only if Task 5's gate misses** |
| `tests/JiraUnpacked.Tests/Reporting/HistorySqlShapeTests.cs` | **new** — the SQL the operators become (spike) |
| `tests/JiraUnpacked.Tests/Reporting/HistoryTranslationTests.cs` | **new** — operators in memory, validation, cap |
| `tests/JiraUnpacked.Tests/Integration/HistoryTestData.cs` | **new** — changelog fixtures |
| `tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs` | **new** — query + export end to end, tenant isolation, 400s |
| `tests/JiraUnpacked.Tests/Integration/HistoryValueListTests.cs` | **new** — the two endpoints |
| `tests/JiraUnpacked.Tests/Performance/HistoryQueryPlanTests.cs` | **new** — opt-in perf gate |
| `src/JiraUnpacked.Web/src/api/issues.ts` | wire types; `getHistoryFields`, `getHistoryValues` |
| `src/JiraUnpacked.Web/src/api/queryKeys.ts` | `historyFields`, `historyValues` |
| `src/JiraUnpacked.Web/src/features/issues/historyShapes.ts` | **new** — constants, readers, canonical form, completeness |
| `src/JiraUnpacked.Web/src/features/issues/filterTree.ts` | history in completeness, wire form, reading, fresh/switch, counts; deep copy |
| `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.ts` | operator labels |
| `src/JiraUnpacked.Web/src/features/issues/DateControls.tsx` | **new** — amount/unit, period, date range (extracted from `ConditionRow`) |
| `src/JiraUnpacked.Web/src/features/issues/DuringControl.tsx` | **new** — the window control |
| `src/JiraUnpacked.Web/src/features/issues/HistoryFieldSelect.tsx` | **new** — history field select with typed fallback |
| `src/JiraUnpacked.Web/src/features/issues/HistoryConditionControls.tsx` | **new** — the history row's second line |
| `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` | uses DateControls; history field entry and controls |
| `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx` | `MultiValuePicker.allowEmpty` |
| `src/JiraUnpacked.Web/src/features/issues/GroupEditor.tsx` | history cap |
| `src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts` | `history` field, `maxHistoryConditions` |

---

### Task 1: The `history` field, the history source, and a correlated `Changed` (server; spike first)

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/IFilterCatalogService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` (whole file shown)
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.History.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterCompiler.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs` (`DescribeBadColumns`)
- Create: `tests/JiraUnpacked.Tests/Reporting/HistorySqlShapeTests.cs`
- Create: `tests/JiraUnpacked.Tests/Reporting/HistoryTranslationTests.cs`
- Create: `tests/JiraUnpacked.Tests/Integration/HistoryTestData.cs`
- Create: `tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs`

**Interfaces:**
- Produces: `FilterOperator.Was`, `FilterOperator.Changed`, `FilterOperator.ChangeCount` (appended).
- Produces: `IIssueFilterTranslator.BuildAsync(IReadOnlyList<FilterCondition> conditions, DictNameResolver resolveDict, CancellationToken ct = default, IQueryable<IssueChangeHistory>? history = null)` and the same for `FilterGroup group`.
- Produces: `IssueFilterTranslator.MaxHistoryConditions = 5` (public const); `FilterCatalogView(..., int MaxDepth, int MaxHistoryConditions)` → JSON `maxHistoryConditions`.
- Produces: `internal enum FieldType { …, History }`; `FilterFields.All["history"]` (Task 1 ops: `Changed`; Task 2 adds `Was`; Task 3 adds `ChangeCount`).
- Produces (History partial, used by Tasks 2–3): `private sealed record HistoryScope(JsonElement Value, IQueryable<IssueChangeHistory> Source, ParameterExpression Issue, ParameterExpression Row, Expression Rows)`; `ReadHistoryScope(...)`; `AnyRow(HistoryScope s, Expression test)`; `Source(IQueryable<IssueChangeHistory>)`; `Member(Expression, string)`; `IsHistory(FilterCondition)`; `HistoryKeys`.
- Produces (tests): `HistoryTestData.SeedChangeAsync(SeedContext ctx, string jiraIssueId, string field, string? from, string? to, string? author = "Alice", DateTime? at = null, Guid? asTenant = null)` (default `at` = 2026-09-01 12:00 UTC); `HistorySqlShapeTests.SqlAsync(FilterCondition c, DateTime? now = null)` and `.Statement(string sql)` (internal static, reused by Tasks 2–3).

- [ ] **Step 1: Write the spike — the SQL shape, and one query that runs on Postgres**

Create `tests/JiraUnpacked.Tests/Reporting/HistorySqlShapeTests.cs`:

```csharp
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Infrastructure.Tenancy;
using JiraUnpacked.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// The SQL the history operators become, without a database: EF + Npgsql must turn the
/// expression trees into correlated subqueries over the tenant-filtered IssueChangeHistory —
/// never a client-side evaluation, never a user string in the statement.
/// </summary>
public sealed class HistorySqlShapeTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    internal static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    private static Task<IReadOnlyList<Guid>> NoDictionary(string field, IReadOnlyList<string> names, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    /// <summary>The issue query's SQL for one history condition, composed as IssueQueryService composes it.</summary>
    internal static async Task<string> SqlAsync(FilterCondition c, DateTime? now = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=unused").Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        var built = await new IssueFilterTranslator(new FakeClock(now ?? Now))
            .BuildAsync(new[] { c }, NoDictionary, CancellationToken.None, db.IssueChangeHistory);
        Assert.Null(built.Error);
        return db.Issues.Where(i => i.JiraProjectId == Guid.Empty).Where(built.Predicate!).ToQueryString();
    }

    /// <summary>The statement without EF's parameter header (the `-- @p='…'` lines).</summary>
    internal static string Statement(string sql) =>
        string.Join('\n', sql.Split('\n').Where(l => !l.StartsWith("--", StringComparison.Ordinal)));

    [Fact]
    public async Task Changed_is_a_correlated_exists_over_the_tenant_filtered_history()
    {
        var sql = Statement(await SqlAsync(new FilterCondition(
            "history", FilterOperator.Changed, J(new { historyField = "Sprint" }), null)));

        Assert.Contains("EXISTS (", sql);
        Assert.Contains("FROM \"IssueChangeHistory\"", sql);
        // Correlated on the issue: same project, same Jira issue id.
        Assert.Matches(@"\w+\.""JiraProjectId"" = \w+\.""JiraProjectId""", sql);
        Assert.Matches(@"\w+\.""JiraIssueId"" = \w+\.""JiraIssueId""", sql);
        // The tenant query filter applies INSIDE the subquery, not only to Issues.
        Assert.Matches(@"EXISTS \([\s\S]*""EndUserId"" = @", sql);
        // The field name is a parameter, never SQL text.
        Assert.DoesNotContain("Sprint", sql);
    }
}
```

Create `tests/JiraUnpacked.Tests/Integration/HistoryTestData.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/HistoryTestData.cs
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Changelog fixtures for the history-condition tests. Seeding uses the superuser connection
/// (bypasses RLS) purely to build fixtures; every assertion reads through the API as the
/// owning tenant, and every test works in a fresh project.
/// </summary>
internal static class HistoryTestData
{
    private static NpgsqlConnection Conn() => new(JiraUnpackedApiFactory.TestConnectionString);

    /// <summary>
    /// One changelog row. <paramref name="asTenant"/> writes it under ANOTHER tenant's id while
    /// keeping this project's id and the issue id — exactly the row a missing tenant filter
    /// inside the subquery would leak.
    /// </summary>
    internal static async Task SeedChangeAsync(
        SeedContext ctx, string jiraIssueId, string field, string? from, string? to,
        string? author = "Alice", DateTime? at = null, Guid? asTenant = null)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"")
              VALUES (@i,@t,@p,@jid,@h,@fld,@f,@to,@a,@at)",
            ("i", Guid.NewGuid()), ("t", asTenant ?? ctx.Tenant), ("p", ctx.ProjectId), ("jid", jiraIssueId),
            ("h", Guid.NewGuid().ToString("N")[..16]), ("fld", field), ("f", from), ("to", to), ("a", author),
            ("at", at ?? new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)));
    }
}
```

Create `tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// History conditions end to end: the issue query (and export) against seeded changelog rows,
/// through the API as the owning tenant, with the translator's clock pinned to a Wednesday noon.
/// </summary>
public sealed class HistoryConditionTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly JiraUnpackedApiFactory _factory;
    private WebApplicationFactory<Program> _app = null!;

    public HistoryConditionTests(JiraUnpackedApiFactory f) => _factory = f;

    public async Task InitializeAsync()
    {
        await _factory.EnsureDatabaseAsync();   // migrate BEFORE any host is built
        _app = RicherFilterTestData.WithFixedClock(_factory, Now);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static object History(string op, object value) => new { field = "history", op, value };

    private async Task<(HttpClient client, SeedContext ctx)> TenantAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_app);
        return (client, await MetricsTestData.SeedProjectAsync(tenant));
    }

    [Fact]
    public async Task Changed_matches_the_issues_whose_field_changed_and_runs_as_sql()
    {
        var (client, ctx) = await TenantAsync();
        await RicherFilterTestData.SeedIssueAsync(ctx, "H-1");
        await RicherFilterTestData.SeedIssueAsync(ctx, "H-2");
        await RicherFilterTestData.SeedIssueAsync(ctx, "H-3");
        await HistoryTestData.SeedChangeAsync(ctx, "H-1", "Sprint", null, "S1");
        await HistoryTestData.SeedChangeAsync(ctx, "H-2", "status", "To Do", "Done");
        await HistoryTestData.SeedChangeAsync(ctx, "H-3", "sprint", null, "S1");   // another field: names are exact

        Assert.Equal(new[] { "H-1" }, await RicherFilterTestData.KeysAsync(
            client, ctx.ProjectId, History("Changed", new { historyField = "Sprint" })));
    }
}
```

(`RicherFilterTestData.SeedIssueAsync(ctx, key)` uses the key as the Jira issue id too, which is what the changelog rows correlate on.)

- [ ] **Step 2: Run the spike to verify it fails**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~HistorySqlShapeTests|FullyQualifiedName~HistoryConditionTests"`
Expected: build FAILS — `'FilterOperator' does not contain a definition for 'Changed'` and `No overload for method 'BuildAsync' takes 4 arguments`.

- [ ] **Step 3: Implement the field, the source plumbing, the guard and `Changed`**

(a) `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs` — append the three operators and extend the `FilterCondition` doc:

```csharp
public enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull, NotIn, IsNotNull, NotContains, Between, InLast, NotInLast, InPeriod, Was, Changed, ChangeCount }
```

In the `<summary>` of `FilterCondition`, after the sentence about `InPeriod`, add:
`/// Value is an object for the history operators (Was, Changed, ChangeCount) on the 'history' field:
/// {"historyField": "Sprint", …} — see IssueFilterTranslator.History.`

(b) `src/JiraUnpacked.Domain/Abstractions/IFilterCatalogService.cs` — the record gains the history guard:

```csharp
public sealed record FilterCatalogView(
    IReadOnlyList<FilterFieldInfo> Fields,
    int MaxConditions, int MaxInValues, int MaxGroups, int MaxDepth, int MaxHistoryConditions);
```

(c) `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs` — the enum and the new entry (add the entry after `parent`):

```csharp
internal enum FieldType { DictName, Decimal, DateTime, Text, ParentRef, StatusCategory, History }
```

```csharp
            // Not a column of Issue: what happened to one of its fields, answered from
            // IssueChangeHistory (IssueFilterTranslator.History). The selector is the correlation key.
            ["history"]    = new("history", FieldType.History,
                                 Ops(FilterOperator.Changed),
                                 (Expression<Func<Issue, string>>)(i => i.JiraIssueId)),
```

(d) Replace `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` with:

```csharp
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
```

(e) Create `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.History.cs`:

```csharp
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// The `history` pseudo-field: what happened to an issue's fields, from IssueChangeHistory.
/// Every operator is a correlated subquery over the source the compiler passes in — the injected
/// AppDbContext's set, so the tenant query filter applies inside the subquery as well. History
/// is never read into memory, and nothing a caller sends reaches SQL text: the field name,
/// values, bounds and counts are all parameters.
/// </summary>
public sealed partial class IssueFilterTranslator
{
    /// <summary>Each history condition is a correlated subquery over the largest table, so they are capped on their own.</summary>
    public const int MaxHistoryConditions = 5;
    internal const int MaxHistoryFieldLength = 100;

    /// <summary>The keys each history operator takes, in their canonical order. Anything else is refused.</summary>
    private static readonly IReadOnlyDictionary<FilterOperator, string[]> HistoryKeys =
        new Dictionary<FilterOperator, string[]>
        {
            [FilterOperator.Changed] = new[] { "historyField" },
        };

    private static bool IsHistory(FilterCondition c) =>
        FilterFields.All.TryGetValue(c.Field, out var f) && f.Type == FieldType.History;

    /// <summary>
    /// What every history operator shares: the value, the source, both lambda parameters (the
    /// issue, a changelog row), and <see cref="Rows"/> — "this row is this issue's, for this field".
    /// </summary>
    private sealed record HistoryScope(
        JsonElement Value, IQueryable<IssueChangeHistory> Source,
        ParameterExpression Issue, ParameterExpression Row, Expression Rows);

    private (Expression<Func<Issue, bool>>?, string?) BuildHistory(
        FilterCondition c, IQueryable<IssueChangeHistory>? history)
    {
        if (ReadHistoryScope(c, history, out var scope) is { } error) return (null, error);
        return c.Op switch
        {
            FilterOperator.Changed => BuildChanged(scope!),
            _ => (null, $"Operator '{c.Op}' is not allowed for field 'history'."),
        };
    }

    /// <summary>The value's shape and its historyField, and the correlation every operator starts from.</summary>
    private static string? ReadHistoryScope(
        FilterCondition c, IQueryable<IssueChangeHistory>? history, out HistoryScope? scope)
    {
        scope = null;
        // The compiler always passes the source; only a caller that has none lands here.
        if (history is null) return "History conditions are not available here.";
        if (c.Value is not { ValueKind: JsonValueKind.Object } v)
            return $"'{c.Op}' on 'history' requires 'value': {{\"historyField\": \"status\", …}}.";

        var keys = HistoryKeys[c.Op];
        foreach (var prop in v.EnumerateObject())
        {
            // An explicit null is the same as leaving the key out.
            if (!keys.Contains(prop.Name) && prop.Value.ValueKind != JsonValueKind.Null)
                return $"'{prop.Name}' is not part of '{c.Op}'. It takes: {string.Join(", ", keys)}.";
        }

        if (!v.TryGetProperty("historyField", out var hf) || hf.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(hf.GetString()) || hf.GetString()!.Length > MaxHistoryFieldLength)
            return $"'{c.Op}' needs 'historyField': the field's name as Jira reports it (\"status\", \"Sprint\"), at most {MaxHistoryFieldLength} characters.";

        var issue = Expression.Parameter(typeof(Issue), "i");
        var row = Expression.Parameter(typeof(IssueChangeHistory), "h");
        // h.JiraProjectId == i.JiraProjectId && h.JiraIssueId == i.JiraIssueId && h.Field == @field.
        // The field is matched exactly, as Jira names it: "Sprint" and "sprint" are different fields.
        var rows = Expression.AndAlso(
            Expression.AndAlso(
                Expression.Equal(Member(row, nameof(IssueChangeHistory.JiraProjectId)), Member(issue, nameof(Issue.JiraProjectId))),
                Expression.Equal(Member(row, nameof(IssueChangeHistory.JiraIssueId)), Member(issue, nameof(Issue.JiraIssueId)))),
            Expression.Equal(Member(row, nameof(IssueChangeHistory.Field)), Parameter(hf.GetString()!)));
        scope = new HistoryScope(v, history, issue, row, rows);
        return null;
    }

    /// <summary>Changed: some row for the field exists.</summary>
    private static (Expression<Func<Issue, bool>>?, string?) BuildChanged(HistoryScope s) =>
        (Lambda(AnyRow(s, s.Rows), s.Issue), null);

    /// <summary>EXISTS (rows of the source satisfying <paramref name="test"/>).</summary>
    private static Expression AnyRow(HistoryScope s, Expression test) =>
        Expression.Call(QueryableAny, Source(s.Source),
            Expression.Quote(Expression.Lambda<Func<IssueChangeHistory, bool>>(test, s.Row)));

    /// <summary>
    /// The source as a captured value — the very expression a C# closure over
    /// `_db.IssueChangeHistory` produces. EF evaluates it and inlines the set's query root, tenant
    /// filter included, exactly as it does for `_db.Statuses` in IssueQueryService's projection;
    /// in memory it is just the sequence.
    /// </summary>
    private static Expression Source(IQueryable<IssueChangeHistory> history) => Parameter(history);

    private static MemberExpression Member(Expression on, string name) => Expression.Property(on, name);

    private static readonly MethodInfo QueryableAny = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(IssueChangeHistory));
}
```

(f) `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterCompiler.cs` — in `CompileAsync`, pass the tenant-scoped set:

```csharp
        // The history conditions query the changelog through the SAME injected context, so the
        // tenant filter that scopes every dictionary lookup also scopes their subqueries.
        var build = group is not null
            ? await _translator.BuildAsync(group, ResolveDictAsync, ct, _db.IssueChangeHistory)
            : await _translator.BuildAsync(conditions, ResolveDictAsync, ct, _db.IssueChangeHistory);
```

(g) `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs` — `DisplayOrder` ends with the history operators, and `Get()` serves the cap:

```csharp
    /// <summary>
    /// The order the editor offers operators in: equality, lists, comparisons, ranges and
    /// relative dates, text matching, the two that take no value, then the history operators.
    /// Explicit because FilterOperator is append-only, which makes its declaration order a
    /// history rather than a reading order. Every member must appear here — FilterCatalogServiceTests checks.
    /// </summary>
    internal static readonly FilterOperator[] DisplayOrder =
    {
        FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.NotIn,
        FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Gt, FilterOperator.Gte,
        FilterOperator.Between, FilterOperator.InLast, FilterOperator.NotInLast, FilterOperator.InPeriod,
        FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.StartsWith,
        FilterOperator.IsNull, FilterOperator.IsNotNull,
        FilterOperator.Was, FilterOperator.Changed, FilterOperator.ChangeCount,
    };
```

and the end of `Get()`:

```csharp
        IssueFilterTranslator.MaxConditions,
        IssueFilterTranslator.MaxInValues,
        IssueFilterTranslator.MaxGroups,
        IssueFilterTranslator.MaxDepth,
        IssueFilterTranslator.MaxHistoryConditions);
```

(h) `src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs` — in `DescribeBadColumns`, replace the field check:

```csharp
            // A JSON null inside the list arrives as a null string: a bad column, not a 500.
            // 'history' is a filter field but never a column: it has no single value to show.
            if (column is null || !FilterFields.All.TryGetValue(column, out var field) || field.Name != column
                || field.Type == FieldType.History)
                return $"'{column}' is not a known column.";
```

- [ ] **Step 4: Run the spike to verify it passes**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~HistorySqlShapeTests|FullyQualifiedName~HistoryConditionTests"`
Expected: PASS (2 tests). The SQL test proves EF + Npgsql emit `EXISTS (SELECT 1 FROM "IssueChangeHistory" … "EndUserId" = @… AND … = i."JiraProjectId" AND … = i."JiraIssueId" AND … "Field" = @…)`; the integration test proves it runs on Postgres.

If EF throws while translating (`could not be translated` / an exception mentioning a query root from another context), switch `Source` to inline the set's own expression and re-run — nothing else changes:

```csharp
    private static Expression Source(IQueryable<IssueChangeHistory> history) => history.Expression;
```

- [ ] **Step 5: Write the remaining tests — in memory, validation, cap, catalog, columns, tenant isolation**

Create `tests/JiraUnpacked.Tests/Reporting/HistoryTranslationTests.cs`:

```csharp
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// History conditions through the translator, run in memory: the history source is a list, the
/// predicate is compiled, and the correlated Any/Count run as LINQ to Objects. (The SQL they
/// become is pinned by HistorySqlShapeTests; the database path by HistoryConditionTests.)
/// </summary>
public sealed class HistoryTranslationTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Project = Guid.NewGuid();

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    private static Task<IReadOnlyList<Guid>> NoDictionary(string field, IReadOnlyList<string> names, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    private static IssueChangeHistory Row(
        string issueId, string field, string? from, string? to,
        string? author = "Alice", DateTime? at = null, Guid? project = null) => new()
    {
        Id = Guid.NewGuid(), JiraProjectId = project ?? Project, JiraIssueId = issueId,
        JiraHistoryId = Guid.NewGuid().ToString("N"), Field = field, FromValue = from, ToValue = to,
        Author = author, ChangedAt = at ?? Now.AddDays(-1),
    };

    /// <summary>An issue of the test project, by Jira issue id. (Not named Issue: that would hide the type in nameof/expressions.)</summary>
    private static Issue I(string id) => new()
    {
        Id = Guid.NewGuid(), JiraProjectId = Project, JiraIssueId = id, JiraKey = $"A-{id}",
        JiraCreatedAt = Now, JiraUpdatedAt = Now,
    };

    private static FilterCondition History(FilterOperator op, object value, bool? disabled = null) =>
        new("history", op, J(value), null, disabled);

    private static Task<FilterBuildResult> BuildAsync(
        IEnumerable<FilterCondition> conditions, IEnumerable<IssueChangeHistory> rows, DictNameResolver? resolve = null) =>
        new IssueFilterTranslator(new FakeClock(Now))
            .BuildAsync(conditions.ToList(), resolve ?? NoDictionary, CancellationToken.None, rows.ToList().AsQueryable());

    private static async Task<Func<Issue, bool>> Pred(
        FilterCondition c, IEnumerable<IssueChangeHistory> rows, DictNameResolver? resolve = null)
    {
        var r = await BuildAsync(new[] { c }, rows, resolve);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static async Task<string> Error(FilterCondition c)
    {
        var r = await BuildAsync(new[] { c }, Array.Empty<IssueChangeHistory>());
        Assert.Null(r.Predicate);
        return Assert.IsType<string>(r.Error);
    }

    // ---- Changed, the field, the correlation ----

    [Fact]
    public async Task Changed_matches_an_issue_with_a_row_for_exactly_that_field()
    {
        var changed = await Pred(History(FilterOperator.Changed, new { historyField = "Sprint" }), new[]
        {
            Row("1", "Sprint", null, "S1"),
            Row("2", "sprint", null, "S1"),                          // another field: names are exact
            Row("3", "Sprint", null, "S1", project: Guid.NewGuid()), // same issue id, another project
        });

        Assert.True(changed(I("1")));
        Assert.False(changed(I("2")));
        Assert.False(changed(I("3")));
        Assert.False(changed(I("4")));   // no rows at all
    }

    [Fact]
    public async Task A_field_no_row_uses_simply_matches_nothing()
    {
        var changed = await Pred(History(FilterOperator.Changed, new { historyField = "Nope" }),
            new[] { Row("1", "Sprint", null, "S1") });
        Assert.False(changed(I("1")));
    }

    [Fact]
    public async Task The_history_value_and_its_field_are_validated()
    {
        Assert.Contains("historyField", await Error(new FilterCondition("history", FilterOperator.Changed, null, null)));
        Assert.Contains("historyField", await Error(History(FilterOperator.Changed, new { })));
        Assert.Contains("historyField", await Error(History(FilterOperator.Changed, new { historyField = "  " })));
        Assert.Contains("historyField", await Error(History(FilterOperator.Changed, new { historyField = 7 })));
        Assert.Contains("at most 100 characters",
            await Error(History(FilterOperator.Changed, new { historyField = new string('x', 101) })));
        Assert.StartsWith("'colour' is not part of 'Changed'.",
            await Error(History(FilterOperator.Changed, new { historyField = "Sprint", colour = "red" })));
        Assert.Contains("not allowed", await Error(new FilterCondition("history", FilterOperator.Eq, J("x"), null)));
    }

    [Fact]
    public async Task An_explicit_null_key_is_the_same_as_leaving_it_out()
    {
        var r = await BuildAsync(
            new[] { History(FilterOperator.Changed, new { historyField = "Sprint", colour = (string?)null }) },
            Array.Empty<IssueChangeHistory>());
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Without_a_history_source_a_history_condition_is_refused_not_ignored()
    {
        var r = await new IssueFilterTranslator(new FakeClock(Now)).BuildAsync(
            new[] { History(FilterOperator.Changed, new { historyField = "Sprint" }) }, NoDictionary, CancellationToken.None);
        Assert.Equal("History conditions are not available here.", r.Error);
    }

    [Fact]
    public async Task At_most_five_enabled_history_conditions_counted_over_the_whole_tree()
    {
        var c = History(FilterOperator.Changed, new { historyField = "Sprint" });
        var five = Enumerable.Repeat(c, 5).ToList();
        Assert.Null((await BuildAsync(five, Array.Empty<IssueChangeHistory>())).Error);

        // A disabled sixth does not count.
        var withDisabled = five.Append(History(FilterOperator.Changed, new { historyField = "x" }, disabled: true)).ToList();
        Assert.Null((await BuildAsync(withDisabled, Array.Empty<IssueChangeHistory>())).Error);

        // A sixth in a nested group does.
        var tree = new FilterGroup(FilterLogic.And, five,
            new List<FilterGroup> { new(FilterLogic.Or, new List<FilterCondition> { c }, null) });
        var r = await new IssueFilterTranslator(new FakeClock(Now)).BuildAsync(
            tree, NoDictionary, CancellationToken.None, Array.Empty<IssueChangeHistory>().AsQueryable());
        Assert.Equal("A filter may have at most 5 history conditions.", r.Error);
    }
}
```

Append to `tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Another_tenants_history_never_matches()
    {
        var (client, ctx) = await TenantAsync();
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_app);
        await RicherFilterTestData.SeedIssueAsync(ctx, "T-1");
        // Same project id, same issue id — only the tenant differs.
        await HistoryTestData.SeedChangeAsync(ctx, "T-1", "Sprint", null, "S1", asTenant: other);

        Assert.Empty(await RicherFilterTestData.KeysAsync(
            client, ctx.ProjectId, History("Changed", new { historyField = "Sprint" })));
    }

    [Fact]
    public async Task A_bad_history_condition_is_a_400_naming_the_problem()
    {
        var (client, ctx) = await TenantAsync();
        Task<string> Title(params object[] c) => RicherFilterTestData.RejectionAsync(client, ctx.ProjectId, c);

        Assert.Contains("historyField", await Title(History("Changed", new { })));
        var six = Enumerable.Repeat(History("Changed", new { historyField = "Sprint" }), 6).ToArray();
        Assert.Equal("A filter may have at most 5 history conditions.", await Title(six));
    }
```

`tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs` — add to `Operators_are_listed_in_reading_order_not_declaration_order`:

```csharp
        Assert.Equal(new[] { "Changed" }, OpsOf("history"));
```

and a new test:

```csharp
    [Fact]
    public void The_history_cap_is_served()
    {
        Assert.Equal(5, new FilterCatalogService().Get().MaxHistoryConditions);
        Assert.Equal("History", new FilterCatalogService().Get().Fields.Single(f => f.Name == "history").Type);
    }
```

`tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs`:
- `CatalogDto` gains `int MaxHistoryConditions` (last parameter).
- `ExpectedFieldNames` gains `"history"` (after `"parent"`); `ExpectedFieldTypes` gains `["history"] = "History"`.
- `Assert.Equal(13, catalog!.Fields.Count);` → `Assert.Equal(14, catalog!.Fields.Count);` (and the comment "All thirteen fields" → "All fourteen fields").
- After `Assert.Equal(3, catalog.MaxDepth);` add `Assert.Equal(5, catalog.MaxHistoryConditions);`.
- After the `parent` assertion add:

```csharp
        var history = catalog.Fields.Single(f => f.Name == "history");
        Assert.True(new HashSet<string> { "Changed" }.SetEquals(history.Operators));
```

`tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs` — in `Unknown_sort_fields_and_bad_column_lists_are_rejected`, after the `'STATUS'` assertion:

```csharp
        // history is a filter field, never a column: it has no single value to show.
        Assert.Equal("'history' is not a known column.",
            await RejectedAsync(client, projectId, new { name = "h", columns = new[] { "history" } }));
```

- [ ] **Step 6: Run the affected tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~History|FullyQualifiedName~FilterCatalog|FullyQualifiedName~SavedFilterTests|FullyQualifiedName~IssueFilter|FullyQualifiedName~DateOperator|FullyQualifiedName~SmartSprint|FullyQualifiedName~StatusCategory"`
Expected: PASS — the new tests and every existing translator/catalog test (the new `history` parameter is optional, so their calls are untouched).

- [ ] **Step 7: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain src/JiraUnpacked.Infrastructure tests/JiraUnpacked.Tests
git commit -m "feat(filters): history field with a correlated Changed over the tenant-scoped changelog" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `Was`, value matching, and `Changed` from / to / by (server)

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.History.cs` (whole file shown)
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` (the `FieldType.History` dispatch line)
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs` (history ops)
- Modify: `tests/JiraUnpacked.Tests/Reporting/HistoryTranslationTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Reporting/HistorySqlShapeTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs`, `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs` (history ops)

**Interfaces:**
- Consumes: Task 1's `HistoryScope`, `ReadHistoryScope`, `AnyRow`, `Source`, `Member`, `HistoryKeys`; the shared helpers `Parameter<T>`, `ListContains<T>`, `Lambda`, `ReplaceParameter`, `NullGuid` (Dictionary partial), `NullString` (Scalars partial), `MaxInValues`.
- Produces: `internal const int MaxHistoryValueLength = 255`; `internal static readonly IReadOnlyDictionary<string, string> CurrentValueFields` (`status→status, assignee→assignee, priority→priority, issuetype→issueType, Sprint→sprint`); `private sealed record HistoryValues(List<string> Names, bool WantsEmpty)`; `ReadHistoryValues(JsonElement v, string key, bool allowEmpty, out HistoryValues? list)`; `MatchValue(Expression column, HistoryValues values)`; `BuildHistoryAsync(FilterCondition c, DictNameResolver resolveDict, IQueryable<IssueChangeHistory>? history, CancellationToken ct)` (replaces Task 1's `BuildHistory`). Task 3 extends these.
- Produces (HTTP): `Was`, and `Changed` with `from`/`to`/`by`, on `POST …/issues/query` and the export.

- [ ] **Step 1: Write the failing tests**

Append to `tests/JiraUnpacked.Tests/Reporting/HistoryTranslationTests.cs` (inside the class):

```csharp
    // ---- value matching ----

    [Theory]
    [InlineData("PLAT 101", true)]                       // equal
    [InlineData("PLAT 101, PLAT 102", true)]             // first element
    [InlineData("PLAT 100, PLAT 101", true)]             // last element
    [InlineData("PLAT 100, PLAT 101, PLAT 102", true)]   // middle element
    [InlineData("PLAT 1010", false)]                     // never a partial word
    [InlineData("PLAT 10, PLAT 1011", false)]
    [InlineData("XPLAT 101", false)]
    [InlineData(null, false)]
    public async Task A_value_matches_the_whole_stored_value_or_one_element_of_its_list(string? stored, bool matches)
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "Labels", values = new[] { "PLAT 101" } }),
            new[] { Row("1", "Labels", null, stored) });
        Assert.Equal(matches, was(I("1")));
    }

    [Fact]
    public async Task A_shorter_name_never_matches_a_longer_one()
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "Labels", values = new[] { "PLAT 10" } }),
            new[] { Row("1", "Labels", "PLAT 101", "PLAT 101, PLAT 102") });
        Assert.False(was(I("1")));
    }

    [Fact]
    public async Task Was_looks_at_both_sides_of_every_change()
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "Team", values = new[] { "Red" } }), new[]
        {
            Row("1", "Team", "Red", "Blue"),    // left Red
            Row("2", "Team", "Blue", "Red"),    // arrived at Red
            Row("3", "Team", "Blue", "Green"),
        });
        Assert.True(was(I("1")));
        Assert.True(was(I("2")));
        Assert.False(was(I("3")));
    }

    [Fact]
    public async Task Empty_matches_a_null_or_empty_stored_value()
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "Team", values = new string?[] { null } }), new[]
        {
            Row("1", "Team", null, "Red"),
            Row("2", "Team", "", "Red"),
            Row("3", "Team", "Blue", "Red"),
        });
        Assert.True(was(I("1")));
        Assert.True(was(I("2")));
        Assert.False(was(I("3")));
    }

    [Theory]
    [InlineData("status", "status", nameof(Issue.StatusId))]
    [InlineData("assignee", "assignee", nameof(Issue.AssigneeId))]
    [InlineData("priority", "priority", nameof(Issue.PriorityId))]
    [InlineData("issuetype", "issueType", nameof(Issue.IssueTypeId))]
    [InlineData("Sprint", "sprint", nameof(Issue.SprintId))]
    public async Task Was_also_matches_the_current_value_of_the_five_fields_the_issue_holds(
        string historyField, string dictField, string fk)
    {
        var id = Guid.NewGuid();
        string? asked = null;
        DictNameResolver resolve = (field, names, ct) =>
        {
            asked = field;
            return Task.FromResult<IReadOnlyList<Guid>>(names.Contains("Blocked") ? new[] { id } : Array.Empty<Guid>());
        };
        var was = await Pred(History(FilterOperator.Was, new { historyField, values = new[] { "Blocked" } }),
            Array.Empty<IssueChangeHistory>(), resolve);

        Assert.Equal(dictField, asked);                 // resolved through the dictionary filters' own path
        var now = I("1");
        typeof(Issue).GetProperty(fk)!.SetValue(now, (Guid?)id);
        Assert.True(was(now));                          // never changed, but is Blocked now
        Assert.False(was(I("2")));
    }

    [Fact]
    public async Task Was_empty_on_a_current_value_field_matches_an_issue_with_no_value_now()
    {
        var was = await Pred(History(FilterOperator.Was, new { historyField = "status", values = new string?[] { null } }),
            Array.Empty<IssueChangeHistory>());
        var withStatus = I("2");
        withStatus.StatusId = Guid.NewGuid();

        Assert.True(was(I("1")));     // no status now
        Assert.False(was(withStatus));
    }

    [Fact]
    public async Task Other_fields_rely_on_history_alone()
    {
        var asked = false;
        DictNameResolver resolve = (_, _, _) => { asked = true; return Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()); };
        await Pred(History(FilterOperator.Was, new { historyField = "Story Points", values = new[] { "3" } }),
            Array.Empty<IssueChangeHistory>(), resolve);
        Assert.False(asked);
    }

    // ---- Changed: from / to / by ----

    [Fact]
    public async Task Changed_requires_every_part_given_on_the_same_row()
    {
        var rows = new[]
        {
            Row("1", "status", "To Do", "Blocked", author: "Bob"),
            Row("2", "status", "To Do", "Done", author: "Bob"),
            Row("2", "status", "Done", "Blocked", author: "Alice"),   // to Blocked — but from Done, by Alice
            Row("3", "status", "To Do", "Blocked", author: null),
        };
        var changed = await Pred(History(FilterOperator.Changed, new
        {
            historyField = "status", from = new[] { "To Do" }, to = new[] { "Blocked" }, by = new[] { "Bob" },
        }), rows);

        Assert.True(changed(I("1")));
        Assert.False(changed(I("2")));   // the parts hold, but on different rows
        Assert.False(changed(I("3")));   // no author matches no name
    }

    [Fact]
    public async Task Changed_from_empty_finds_the_first_assignment()
    {
        var changed = await Pred(History(FilterOperator.Changed, new { historyField = "Team", from = new string?[] { null } }),
            new[] { Row("1", "Team", null, "Red"), Row("2", "Team", "", "Blue"), Row("3", "Team", "Red", "Blue") });
        Assert.True(changed(I("1")));
        Assert.True(changed(I("2")));
        Assert.False(changed(I("3")));
    }

    [Fact]
    public async Task Value_lists_are_validated()
    {
        FilterCondition Was(object value) => History(FilterOperator.Was, value);
        FilterCondition Changed(object value) => History(FilterOperator.Changed, value);

        Assert.Contains("'Was' needs 'values'", await Error(Was(new { historyField = "status" })));
        Assert.Contains("at least one value", await Error(Was(new { historyField = "status", values = Array.Empty<string>() })));
        Assert.Contains("at least one value", await Error(Changed(new { historyField = "status", from = Array.Empty<string>() })));
        Assert.Contains("at most 100 values", await Error(Was(new
        {
            historyField = "status", values = Enumerable.Range(0, 101).Select(i => $"v{i}").ToArray(),
        })));
        Assert.Contains("at most 255 characters", await Error(Was(new { historyField = "status", values = new[] { new string('x', 256) } })));
        Assert.Contains("(Empty) is not one", await Error(Changed(new { historyField = "status", by = new string?[] { null } })));
        Assert.Contains("must be a list", await Error(Changed(new { historyField = "status", to = "Done" })));
        Assert.Contains("must be a list", await Error(Was(new { historyField = "status", values = new object[] { 7 } })));
        Assert.StartsWith("'during' is not part of 'Was'.", await Error(Was(new
        {
            historyField = "status", values = new[] { "x" }, during = new { kind = "inLast", amount = 1, unit = "days" },
        })));
    }
```

Append to `tests/JiraUnpacked.Tests/Reporting/HistorySqlShapeTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Values_travel_as_parameters_so_the_statement_is_the_same_for_any_value()
    {
        static FilterCondition Was(string v) => new("history", FilterOperator.Was,
            J(new { historyField = "Labels", values = new[] { v } }), null);
        var first = Statement(await SqlAsync(Was("PLAT 101")));
        var second = Statement(await SqlAsync(Was("100%_done")));

        Assert.DoesNotContain("PLAT", first);
        Assert.DoesNotContain("100%", second);
        Assert.Equal(first, second);   // one shape, one compiled-query cache entry
    }
```

Append to `tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Was_matches_history_elements_and_the_current_value()
    {
        var (client, ctx) = await TenantAsync();
        var s101 = await RicherFilterTestData.SeedSprintAsync(ctx, "PLAT 101", "closed");
        await RicherFilterTestData.SeedIssueAsync(ctx, "W-1");                       // carried through PLAT 101
        await RicherFilterTestData.SeedIssueAsync(ctx, "W-2", sprintId: s101);       // in PLAT 101 now, never moved
        await RicherFilterTestData.SeedIssueAsync(ctx, "W-3");                       // only ever in PLAT 1010
        await RicherFilterTestData.SeedIssueAsync(ctx, "W-4", statusId: ctx.DoneStatusId);
        await HistoryTestData.SeedChangeAsync(ctx, "W-1", "Sprint", "PLAT 100, PLAT 101", "PLAT 102");
        await HistoryTestData.SeedChangeAsync(ctx, "W-3", "Sprint", null, "PLAT 1010");

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        Assert.Equal(new[] { "W-1", "W-2" }, await Keys(History("Was", new { historyField = "Sprint", values = new[] { "PLAT 101" } })));
        // Never changed status, but "was" Done because it is Done now.
        Assert.Equal(new[] { "W-4" }, await Keys(History("Was", new { historyField = "status", values = new[] { "Done" } })));
    }

    [Fact]
    public async Task Wildcards_in_a_value_are_literal_in_sql()
    {
        var (client, ctx) = await TenantAsync();
        var stored = new Dictionary<string, string>
        {
            ["L-1"] = "a, 100%_done, b",      // the real value, in the middle
            ["L-2"] = "100%_done, b",         // first
            ["L-3"] = "a, 100%_done",         // last
            ["L-4"] = "a, 100xyzdone, b",     // what an unescaped LIKE would also match
            ["L-5"] = "100xyzdone, b",
            ["L-6"] = "a, 100xyzdone",
        };
        foreach (var (key, value) in stored)
        {
            await RicherFilterTestData.SeedIssueAsync(ctx, key);
            await HistoryTestData.SeedChangeAsync(ctx, key, "labels", null, value);
        }

        Assert.Equal(new[] { "L-1", "L-2", "L-3" }, await RicherFilterTestData.KeysAsync(client, ctx.ProjectId,
            History("Was", new { historyField = "labels", values = new[] { "100%_done" } })));
    }

    [Fact]
    public async Task Changed_from_to_and_by_must_hold_on_one_change()
    {
        var (client, ctx) = await TenantAsync();
        foreach (var key in new[] { "P-1", "P-2", "P-3" }) await RicherFilterTestData.SeedIssueAsync(ctx, key);
        await HistoryTestData.SeedChangeAsync(ctx, "P-1", "status", "To Do", "Done", author: "Bob");
        await HistoryTestData.SeedChangeAsync(ctx, "P-2", "status", "To Do", "In Progress", author: "Bob");
        await HistoryTestData.SeedChangeAsync(ctx, "P-2", "status", "In Progress", "Done", author: "Alice");
        await HistoryTestData.SeedChangeAsync(ctx, "P-3", "assignee", null, "Bob");

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        Assert.Equal(new[] { "P-1", "P-2" }, await Keys(History("Changed", new { historyField = "status", to = new[] { "Done" } })));
        Assert.Equal(new[] { "P-1" }, await Keys(History("Changed", new { historyField = "status", to = new[] { "Done" }, by = new[] { "Bob" } })));
        Assert.Equal(new[] { "P-3" }, await Keys(History("Changed", new { historyField = "assignee", from = new string?[] { null } })));
    }

    [Fact]
    public async Task Bad_value_lists_are_400s()
    {
        var (client, ctx) = await TenantAsync();
        Task<string> Title(object c) => RicherFilterTestData.RejectionAsync(client, ctx.ProjectId, c);

        Assert.Contains("(Empty) is not one", await Title(History("Changed", new { historyField = "status", by = new string?[] { null } })));
        Assert.Contains("at most 100 values", await Title(History("Was", new
        {
            historyField = "status", values = Enumerable.Range(0, 101).Select(i => $"v{i}").ToArray(),
        })));
        Assert.Contains("'Was' needs 'values'", await Title(History("Was", new { historyField = "status" })));
    }
```

Catalog tests: in `FilterCatalogServiceTests` the history line becomes `Assert.Equal(new[] { "Was", "Changed" }, OpsOf("history"));`; in `FilterCatalogEndpointTests` the history set becomes `new HashSet<string> { "Was", "Changed" }`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~History|FullyQualifiedName~FilterCatalog"`
Expected: FAIL — the `Was` tests with `Operator 'Was' is not allowed for field 'history'`, the `from`/`to`/`by` tests with `'from' is not part of 'Changed'`, the catalog operator sets.

- [ ] **Step 3: Implement**

(a) `FilterField.cs` — the history entry's operators become `Ops(FilterOperator.Was, FilterOperator.Changed)`.

(b) `IssueFilterTranslator.cs` — the dispatch line becomes:

```csharp
            FieldType.History   => await BuildHistoryAsync(c, resolveDict, history, ct),
```

(c) Replace `IssueFilterTranslator.History.cs` with:

```csharp
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// The `history` pseudo-field: what happened to an issue's fields, from IssueChangeHistory.
/// Every operator is a correlated subquery over the source the compiler passes in — the injected
/// AppDbContext's set, so the tenant query filter applies inside the subquery as well. History
/// is never read into memory, and nothing a caller sends reaches SQL text: the field name,
/// values, bounds and counts are all parameters.
/// </summary>
public sealed partial class IssueFilterTranslator
{
    /// <summary>Each history condition is a correlated subquery over the largest table, so they are capped on their own.</summary>
    public const int MaxHistoryConditions = 5;
    internal const int MaxHistoryFieldLength = 100;
    internal const int MaxHistoryValueLength = 255;

    /// <summary>Jira joins a multi-value field's names with this: "PLAT 100, PLAT 101".</summary>
    private const string ListSeparator = ", ";

    /// <summary>The keys each history operator takes, in their canonical order. Anything else is refused.</summary>
    private static readonly IReadOnlyDictionary<FilterOperator, string[]> HistoryKeys =
        new Dictionary<FilterOperator, string[]>
        {
            [FilterOperator.Was] = new[] { "historyField", "values" },
            [FilterOperator.Changed] = new[] { "historyField", "from", "to", "by" },
        };

    /// <summary>
    /// The changelog names (as Jira reports them) of the fields the issue table also holds, and
    /// the dictionary field each resolves through. For these, Was also matches the CURRENT value:
    /// an issue that never changed its status still "was" that status.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CurrentValueFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = "status",
            ["assignee"] = "assignee",
            ["priority"] = "priority",
            ["issuetype"] = "issueType",
            ["Sprint"] = "sprint",
        };

    private static bool IsHistory(FilterCondition c) =>
        FilterFields.All.TryGetValue(c.Field, out var f) && f.Type == FieldType.History;

    /// <summary>
    /// What every history operator shares: the value, the source, both lambda parameters (the
    /// issue, a changelog row), and <see cref="Rows"/> — "this row is this issue's, for this field".
    /// </summary>
    private sealed record HistoryScope(
        JsonElement Value, IQueryable<IssueChangeHistory> Source,
        ParameterExpression Issue, ParameterExpression Row, Expression Rows);

    /// <summary>A value list: names (matched whole, or as one element of a ", "-joined list) and whether (Empty) was asked for.</summary>
    private sealed record HistoryValues(List<string> Names, bool WantsEmpty);

    private async Task<(Expression<Func<Issue, bool>>?, string?)> BuildHistoryAsync(
        FilterCondition c, DictNameResolver resolveDict, IQueryable<IssueChangeHistory>? history, CancellationToken ct)
    {
        if (ReadHistoryScope(c, history, out var scope) is { } error) return (null, error);
        return c.Op switch
        {
            FilterOperator.Was => await BuildWasAsync(scope!, resolveDict, ct),
            FilterOperator.Changed => BuildChanged(scope!),
            _ => (null, $"Operator '{c.Op}' is not allowed for field 'history'."),
        };
    }

    /// <summary>The value's shape and its historyField, and the correlation every operator starts from.</summary>
    private static string? ReadHistoryScope(
        FilterCondition c, IQueryable<IssueChangeHistory>? history, out HistoryScope? scope)
    {
        scope = null;
        // The compiler always passes the source; only a caller that has none lands here.
        if (history is null) return "History conditions are not available here.";
        if (c.Value is not { ValueKind: JsonValueKind.Object } v)
            return $"'{c.Op}' on 'history' requires 'value': {{\"historyField\": \"status\", …}}.";

        var keys = HistoryKeys[c.Op];
        foreach (var prop in v.EnumerateObject())
        {
            // An explicit null is the same as leaving the key out.
            if (!keys.Contains(prop.Name) && prop.Value.ValueKind != JsonValueKind.Null)
                return $"'{prop.Name}' is not part of '{c.Op}'. It takes: {string.Join(", ", keys)}.";
        }

        if (!v.TryGetProperty("historyField", out var hf) || hf.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(hf.GetString()) || hf.GetString()!.Length > MaxHistoryFieldLength)
            return $"'{c.Op}' needs 'historyField': the field's name as Jira reports it (\"status\", \"Sprint\"), at most {MaxHistoryFieldLength} characters.";

        var issue = Expression.Parameter(typeof(Issue), "i");
        var row = Expression.Parameter(typeof(IssueChangeHistory), "h");
        // h.JiraProjectId == i.JiraProjectId && h.JiraIssueId == i.JiraIssueId && h.Field == @field.
        // The field is matched exactly, as Jira names it: "Sprint" and "sprint" are different fields.
        var rows = Expression.AndAlso(
            Expression.AndAlso(
                Expression.Equal(Member(row, nameof(IssueChangeHistory.JiraProjectId)), Member(issue, nameof(Issue.JiraProjectId))),
                Expression.Equal(Member(row, nameof(IssueChangeHistory.JiraIssueId)), Member(issue, nameof(Issue.JiraIssueId)))),
            Expression.Equal(Member(row, nameof(IssueChangeHistory.Field)), Parameter(hf.GetString()!)));
        scope = new HistoryScope(v, history, issue, row, rows);
        return null;
    }

    /// <summary>
    /// Was: some row for the field has one of the values as its FromValue or ToValue — or, for the
    /// five fields the issue table holds, the issue has it now.
    /// </summary>
    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildWasAsync(
        HistoryScope s, DictNameResolver resolveDict, CancellationToken ct)
    {
        if (ReadHistoryValues(s.Value, "values", allowEmpty: true, out var values) is { } error) return (null, error);
        if (values is null) return (null, "'Was' needs 'values': at least one value, or null for (Empty).");

        var inHistory = AnyRow(s, Expression.AndAlso(s.Rows, Expression.OrElse(
            MatchValue(Member(s.Row, nameof(IssueChangeHistory.FromValue)), values),
            MatchValue(Member(s.Row, nameof(IssueChangeHistory.ToValue)), values))));

        var historyField = s.Value.GetProperty("historyField").GetString()!;
        if (!CurrentValueFields.TryGetValue(historyField, out var dictField))
            return (Lambda(inHistory, s.Issue), null);

        var now = await CurrentValueAsync(dictField, values, s.Issue, resolveDict, ct);
        return (Lambda(Expression.OrElse(now, inHistory), s.Issue), null);
    }

    /// <summary>
    /// The issue's current value, resolved the way the dictionary filters resolve it: names → ids
    /// through the same tenant-scoped resolver → FK in set (ids sent as one parameter); (Empty) →
    /// no value now.
    /// </summary>
    private static async Task<Expression> CurrentValueAsync(
        string dictField, HistoryValues values, ParameterExpression issue, DictNameResolver resolveDict, CancellationToken ct)
    {
        var selector = (Expression<Func<Issue, Guid?>>)FilterFields.All[dictField].Selector;
        var fk = ReplaceParameter.Replace(selector.Body, selector.Parameters[0], issue);   // i.StatusId …
        Expression? test = values.WantsEmpty ? Expression.Equal(fk, NullGuid) : null;
        if (values.Names.Count > 0)
        {
            var ids = (await resolveDict(dictField, values.Names, ct)).ToList();
            // i.Fk.HasValue && @ids.Contains(i.Fk.Value)
            var inSet = Expression.AndAlso(
                Expression.Property(fk, nameof(Nullable<Guid>.HasValue)),
                Expression.Call(Parameter(ids), ListContains<Guid>(), Expression.Property(fk, nameof(Nullable<Guid>.Value))));
            test = test is null ? inSet : Expression.OrElse(test, inSet);
        }
        return test!;   // ReadHistoryValues never yields an empty list
    }

    /// <summary>Changed: some row for the field satisfies every part given — from, to, by. No parts: changed at all.</summary>
    private static (Expression<Func<Issue, bool>>?, string?) BuildChanged(HistoryScope s)
    {
        if (ReadHistoryValues(s.Value, "from", allowEmpty: true, out var from) is { } e1) return (null, e1);
        if (ReadHistoryValues(s.Value, "to", allowEmpty: true, out var to) is { } e2) return (null, e2);
        if (ReadHistoryValues(s.Value, "by", allowEmpty: false, out var by) is { } e3) return (null, e3);

        var test = s.Rows;
        if (from is not null)
            test = Expression.AndAlso(test, MatchValue(Member(s.Row, nameof(IssueChangeHistory.FromValue)), from));
        if (to is not null)
            test = Expression.AndAlso(test, MatchValue(Member(s.Row, nameof(IssueChangeHistory.ToValue)), to));
        // Authors exactly, as Jira shows them. A row with no author matches no name.
        if (by is not null)
            test = Expression.AndAlso(test, Expression.Call(
                Parameter(by.Names), ListContains<string>(), Member(s.Row, nameof(IssueChangeHistory.Author))));
        return (Lambda(AnyRow(s, test), s.Issue), null);
    }

    /// <summary>
    /// An optional list under <paramref name="key"/>: absent or null → no list, no error. Strings of
    /// at most 255 characters; null or "" is (Empty) where allowed (Jira stores "no value" as
    /// either). At most 100 entries, and never empty — leaving the key out is how a caller says "any".
    /// </summary>
    private static string? ReadHistoryValues(JsonElement v, string key, bool allowEmpty, out HistoryValues? list)
    {
        list = null;
        if (!v.TryGetProperty(key, out var arr) || arr.ValueKind == JsonValueKind.Null) return null;
        var what = allowEmpty ? "strings, or null for (Empty)" : "author names";
        if (arr.ValueKind != JsonValueKind.Array) return $"'{key}' must be a list of {what}.";
        var count = arr.GetArrayLength();
        if (count == 0) return $"'{key}' needs at least one value; leave it out to accept any.";
        if (count > MaxInValues) return $"'{key}' accepts at most {MaxInValues} values.";

        var names = new List<string>();
        var wantsEmpty = false;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return $"'{key}' must be a list of {what}.";
            var s = e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
            if (s.Length == 0)
            {
                if (!allowEmpty) return $"'{key}' takes author names; (Empty) is not one.";
                wantsEmpty = true;
                continue;
            }
            if (s.Length > MaxHistoryValueLength)
                return $"Values in '{key}' may be at most {MaxHistoryValueLength} characters.";
            if (!names.Contains(s)) names.Add(s);
        }
        list = new HistoryValues(names, wantsEmpty);
        return null;
    }

    /// <summary>
    /// The column holds one of the names — equal to it, or one ELEMENT of Jira's ", "-joined list
    /// ("v, …", "…, v", "…, v, …"), so "PLAT 10" never matches "PLAT 101" — or, for (Empty), holds
    /// nothing. Built from ==, StartsWith, EndsWith and Contains on parameters, which EF/Npgsql
    /// translate with the pattern escaped: a "%" or "_" in a name is a literal.
    /// </summary>
    private static Expression MatchValue(Expression column, HistoryValues values)
    {
        Expression? match = values.WantsEmpty
            ? Expression.OrElse(Expression.Equal(column, NullString), Expression.Equal(column, Expression.Constant("")))
            : null;
        if (values.Names.Count > 0)
        {
            Expression element = Expression.Call(Parameter(values.Names), ListContains<string>(), column);
            foreach (var name in values.Names)
            {
                element = Expression.OrElse(element, Expression.Call(column, StringStartsWith, Parameter(name + ListSeparator)));
                element = Expression.OrElse(element, Expression.Call(column, StringEndsWith, Parameter(ListSeparator + name)));
                element = Expression.OrElse(element, Expression.Call(column, StringContains, Parameter(ListSeparator + name + ListSeparator)));
            }
            // NULL holds no name. The guard keeps .StartsWith off a null in memory; in SQL it costs nothing.
            var named = Expression.AndAlso(Expression.NotEqual(column, NullString), element);
            match = match is null ? named : Expression.OrElse(match, named);
        }
        return match!;   // ReadHistoryValues never yields an empty list
    }

    /// <summary>EXISTS (rows of the source satisfying <paramref name="test"/>).</summary>
    private static Expression AnyRow(HistoryScope s, Expression test) =>
        Expression.Call(QueryableAny, Source(s.Source),
            Expression.Quote(Expression.Lambda<Func<IssueChangeHistory, bool>>(test, s.Row)));

    /// <summary>
    /// The source as a captured value — the very expression a C# closure over
    /// `_db.IssueChangeHistory` produces. EF evaluates it and inlines the set's query root, tenant
    /// filter included, exactly as it does for `_db.Statuses` in IssueQueryService's projection;
    /// in memory it is just the sequence.
    /// </summary>
    private static Expression Source(IQueryable<IssueChangeHistory> history) => Parameter(history);

    private static MemberExpression Member(Expression on, string name) => Expression.Property(on, name);

    private static readonly MethodInfo QueryableAny = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Any) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(IssueChangeHistory));

    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) })!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), new[] { typeof(string) })!;
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!;
}
```

(If Task 1 Step 4 needed the `history.Expression` fallback, keep that body for `Source` here.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~History|FullyQualifiedName~FilterCatalog"`
Expected: PASS. `Wildcards_in_a_value_are_literal_in_sql` is the test that proves Npgsql escapes the `StartsWith`/`EndsWith` parameters on Postgres. If it also returns `L-4`…`L-6`, the provider is not escaping: stop and report rather than hand-rolling escaping (the spec relies on the provider's escaped `LIKE`).

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Infrastructure tests/JiraUnpacked.Tests
git commit -m "feat(filters): history Was with list-element matching and current values; Changed from/to/by" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Windows (`during`) and `ChangeCount` (server)

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.Dates.cs` (whole file shown — readers extracted for reuse, behaviour unchanged)
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.History.cs` (additions and replacements shown)
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs` (history ops)
- Modify: `tests/JiraUnpacked.Tests/Reporting/HistoryTranslationTests.cs`, `tests/JiraUnpacked.Tests/Reporting/HistorySqlShapeTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs`, `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs`

**Interfaces:**
- Consumes: Task 2's History partial; `RelativeDates.Subtract/Period/TryResolveZone/Units/Periods/MaxAmount`; `_clock`; `And` (Expressions partial).
- Produces (Dates partial, shared): `private string? ReadSince(JsonElement v, string shapeError, out DateTime since)`; `private string? ReadPeriod(JsonElement v, string shapeError, out DateTime start, out DateTime end)`; `private static string? ReadDateRange(JsonElement? fromEl, JsonElement? toEl, string what, string where, out DateTime? from, out DateTime? to, out bool toDateOnly)`; `private static Expression? DateRangeTest(Expression body, DateTime? from, DateTime? to, bool toDateOnly, Func<DateTime, Expression> value)`.
- Produces (History partial): `internal const int MaxChangeCount = 10_000`; `private sealed record Window(DateTime? From, DateTime? To, bool ToExclusive)`; `ReadDuring`, `ReadWindow`, `WindowTest`, `BuildChangeCount`.
- Produces (HTTP): `ChangeCount`, and `during` on `Changed` and `ChangeCount`, on the query and the export.

- [ ] **Step 1: Write the failing tests**

Append to `tests/JiraUnpacked.Tests/Reporting/HistoryTranslationTests.cs` (inside the class):

```csharp
    // ---- windows ----

    [Fact]
    public async Task During_in_the_last_counts_from_now_minus_the_amount()
    {
        var changed = await Pred(History(FilterOperator.Changed, new
        {
            historyField = "Sprint", during = new { kind = "inLast", amount = 30, unit = "days" },
        }), new[]
        {
            Row("1", "Sprint", null, "S1", at: Now.AddDays(-30)),                  // the boundary itself is inside
            Row("2", "Sprint", null, "S1", at: Now.AddDays(-30).AddSeconds(-1)),
        });
        Assert.True(changed(I("1")));
        Assert.False(changed(I("2")));
    }

    [Fact]
    public async Task During_in_period_uses_the_zones_calendar()
    {
        // Now is Wed 2026-10-07 12:00 UTC. Last month in Warsaw (CEST) = [Aug 31 22:00Z, Sep 30 22:00Z).
        var changed = await Pred(History(FilterOperator.Changed, new
        {
            historyField = "Sprint", during = new { kind = "inPeriod", period = "lastMonth", timeZone = "Europe/Warsaw" },
        }), new[]
        {
            Row("1", "Sprint", null, "S1", at: new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc)),
            Row("2", "Sprint", null, "S1", at: new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc)),
            Row("3", "Sprint", null, "S1", at: new DateTime(2026, 9, 30, 21, 59, 59, DateTimeKind.Utc)),
        });
        Assert.True(changed(I("1")));
        Assert.False(changed(I("2")));
        Assert.True(changed(I("3")));
    }

    [Fact]
    public async Task During_between_includes_the_whole_last_day_and_either_bound_may_be_open()
    {
        var rows = new[]
        {
            Row("1", "Sprint", null, "S1", at: new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc)),
            Row("2", "Sprint", null, "S1", at: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
            Row("3", "Sprint", null, "S1", at: new DateTime(2026, 8, 31, 23, 0, 0, DateTimeKind.Utc)),
        };
        FilterCondition Between(object during) => History(FilterOperator.Changed, new { historyField = "Sprint", during });

        var closed = await Pred(Between(new { kind = "between", from = "2026-09-01", to = "2026-09-30" }), rows);
        Assert.True(closed(I("1")));
        Assert.False(closed(I("2")));
        Assert.False(closed(I("3")));

        var openStart = await Pred(Between(new { kind = "between", from = (string?)null, to = "2026-09-30" }), rows);
        Assert.True(openStart(I("3")));
        Assert.False(openStart(I("2")));

        var openEnd = await Pred(Between(new { kind = "between", from = "2026-09-01" }), rows);
        Assert.True(openEnd(I("2")));
        Assert.False(openEnd(I("3")));
    }

    // ---- ChangeCount ----

    [Theory]
    [InlineData("Eq", 2, new[] { false, false, true, false })]
    [InlineData("Neq", 2, new[] { true, true, false, true })]
    [InlineData("Gt", 1, new[] { false, false, true, true })]
    [InlineData("Gte", 3, new[] { false, false, false, true })]
    [InlineData("Lt", 1, new[] { true, false, false, false })]
    [InlineData("Lte", 1, new[] { true, true, false, false })]
    [InlineData("Eq", 0, new[] { true, false, false, false })]
    public async Task ChangeCount_compares_the_number_of_rows_and_never_changed_counts_as_zero(
        string compare, int count, bool[] expected)
    {
        // Issue 0 never changed the field (only another one); issues 1, 2 and 3 changed it that many times.
        var rows = new[] { "1", "2", "2", "3", "3", "3" }.Select(id => Row(id, "Sprint", null, "S"))
            .Append(Row("0", "status", null, "Done"));
        var counted = await Pred(History(FilterOperator.ChangeCount, new { historyField = "Sprint", compare, count }), rows);
        Assert.Equal(expected, new[] { "0", "1", "2", "3" }.Select(id => counted(Issue(id))).ToArray());
    }

    [Fact]
    public async Task ChangeCount_counts_only_the_rows_inside_the_window()
    {
        var rows = new[]
        {
            Row("1", "Sprint", null, "S", at: Now.AddDays(-3)),
            Row("1", "Sprint", null, "S", at: Now.AddDays(-40)),
            Row("1", "Sprint", null, "S", at: Now.AddDays(-41)),
        };
        var recent = await Pred(History(FilterOperator.ChangeCount, new
        {
            historyField = "Sprint", compare = "Eq", count = 1, during = new { kind = "inLast", amount = 30, unit = "days" },
        }), rows);
        Assert.True(recent(I("1")));
        Assert.False(recent(I("2")));   // no rows: 0, not 1
    }

    [Fact]
    public async Task ChangeCount_of_a_field_no_row_uses_is_zero_for_everyone()
    {
        var zero = await Pred(History(FilterOperator.ChangeCount, new { historyField = "Nope", compare = "Eq", count = 0 }),
            new[] { Row("1", "Sprint", null, "S") });
        Assert.True(zero(I("1")));
        Assert.True(zero(I("2")));
    }

    [Fact]
    public async Task Counts_and_windows_are_validated()
    {
        FilterCondition Count(object value) => History(FilterOperator.ChangeCount, value);
        Assert.Contains("'compare' must be one of: Eq, Neq, Gt, Gte, Lt, Lte", await Error(Count(new { historyField = "S", count = 2 })));
        Assert.Contains("'compare' must be one of", await Error(Count(new { historyField = "S", compare = "gte", count = 2 })));
        foreach (var bad in new object[] { -1, 10001, 2.5, "3" })
            Assert.Contains("from 0 to 10000", await Error(Count(new { historyField = "S", compare = "Gte", count = bad })));
        Assert.Contains("from 0 to 10000", await Error(Count(new { historyField = "S", compare = "Gte" })));

        FilterCondition During(object during) => History(FilterOperator.Changed, new { historyField = "S", during });
        Assert.StartsWith("'during' must look like", await Error(During("last week")));
        Assert.Contains("Unknown 'during' kind 'fortnight'", await Error(During(new { kind = "fortnight" })));
        Assert.Contains("'amount' must be a whole number from 1 to 3650", await Error(During(new { kind = "inLast", amount = 0, unit = "days" })));
        Assert.Contains("Unknown unit 'decades'", await Error(During(new { kind = "inLast", amount = 1, unit = "decades" })));
        Assert.Contains("Unknown period 'fortnight'", await Error(During(new { kind = "inPeriod", period = "fortnight" })));
        Assert.Contains("Unknown time zone '../../etc/passwd'", await Error(During(new { kind = "inPeriod", period = "today", timeZone = "../../etc/passwd" })));
        Assert.Contains("at least one bound", await Error(During(new { kind = "between" })));
        Assert.Contains("requires ISO-8601 dates", await Error(During(new { kind = "between", from = "soon" })));
        Assert.Contains("'from' is after 'to'", await Error(During(new { kind = "between", from = "2026-09-30", to = "2026-09-01" })));
    }
```

Append to `tests/JiraUnpacked.Tests/Reporting/HistorySqlShapeTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task ChangeCount_is_a_correlated_count_and_its_window_and_count_are_parameters()
    {
        static FilterCondition Count(int n) => new("history", FilterOperator.ChangeCount, J(new
        {
            historyField = "Sprint", compare = "Gte", count = n, during = new { kind = "inLast", amount = 30, unit = "days" },
        }), null);
        var first = await SqlAsync(Count(3));
        var second = await SqlAsync(Count(4), Now.AddMinutes(1));

        Assert.Contains("count(*)", Statement(first));
        Assert.Contains("2026-09-07T12:00:00", first);                 // the header: @p = now − 30 days
        Assert.DoesNotContain("2026-09-07", Statement(first));
        Assert.Equal(Statement(first), Statement(second));            // one shape, one cache entry
    }
```

Append to `tests/JiraUnpacked.Tests/Integration/HistoryConditionTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task ChangeCount_and_windows_end_to_end_with_never_changed_issues_counting_zero()
    {
        var (client, ctx) = await TenantAsync();
        foreach (var key in new[] { "C-0", "C-1", "C-3" }) await RicherFilterTestData.SeedIssueAsync(ctx, key);
        await HistoryTestData.SeedChangeAsync(ctx, "C-1", "Sprint", null, "S1", at: Now.AddDays(-100));
        for (var n = 0; n < 3; n++)
            await HistoryTestData.SeedChangeAsync(ctx, "C-3", "Sprint", $"S{n}", $"S{n + 1}", at: Now.AddDays(-5 - n));

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        Assert.Equal(new[] { "C-3" }, await Keys(History("ChangeCount", new { historyField = "Sprint", compare = "Gte", count = 3 })));
        Assert.Equal(new[] { "C-0" }, await Keys(History("ChangeCount", new { historyField = "Sprint", compare = "Eq", count = 0 })));
        Assert.Equal(new[] { "C-0", "C-1" }, await Keys(History("ChangeCount", new
        {
            historyField = "Sprint", compare = "Eq", count = 0, during = new { kind = "inLast", amount = 30, unit = "days" },
        })));
        // This month in Warsaw starts Sep 30 22:00Z: C-3's Oct 1 and Oct 2 changes are in it, C-1's is not.
        Assert.Equal(new[] { "C-3" }, await Keys(History("Changed", new
        {
            historyField = "Sprint", during = new { kind = "inPeriod", period = "thisMonth", timeZone = "Europe/Warsaw" },
        })));
    }

    [Fact]
    public async Task Another_tenants_history_never_counts()
    {
        var (client, ctx) = await TenantAsync();
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_app);
        await RicherFilterTestData.SeedIssueAsync(ctx, "N-1");
        await HistoryTestData.SeedChangeAsync(ctx, "N-1", "Sprint", null, "S1");
        for (var i = 0; i < 4; i++)
            await HistoryTestData.SeedChangeAsync(ctx, "N-1", "Sprint", "S1", "S2", asTenant: other);

        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);
        Assert.Equal(new[] { "N-1" }, await Keys(History("ChangeCount", new { historyField = "Sprint", compare = "Eq", count = 1 })));
        Assert.Empty(await Keys(History("ChangeCount", new { historyField = "Sprint", compare = "Gte", count = 2 })));
    }

    [Fact]
    public async Task Export_applies_each_history_operator_like_the_list()
    {
        var (client, ctx) = await TenantAsync();
        foreach (var key in new[] { "E-1", "E-2" }) await RicherFilterTestData.SeedIssueAsync(ctx, key);
        await HistoryTestData.SeedChangeAsync(ctx, "E-1", "status", "To Do", "Blocked");
        await HistoryTestData.SeedChangeAsync(ctx, "E-1", "status", "Blocked", "To Do");

        foreach (var condition in new[]
        {
            History("Was", new { historyField = "status", values = new[] { "Blocked" } }),
            History("Changed", new { historyField = "status", during = new { kind = "inLast", amount = 90, unit = "days" } }),
            History("ChangeCount", new { historyField = "status", compare = "Gte", count = 2 }),
        })
        {
            var filter = JsonSerializer.Serialize(new { logic = "And", conditions = new[] { condition } });
            var csv = await client.GetStringAsync(
                $"/api/projects/{ctx.ProjectId}/issues/export.csv?cols=jiraKey&filter={Uri.EscapeDataString(filter)}");
            Assert.Equal(new[] { "Key", "E-1" }, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        }
    }

    [Fact]
    public async Task Bad_counts_and_windows_are_400s()
    {
        var (client, ctx) = await TenantAsync();
        Task<string> Title(object c) => RicherFilterTestData.RejectionAsync(client, ctx.ProjectId, c);

        Assert.Contains("'compare' must be one of", await Title(History("ChangeCount", new { historyField = "Sprint", compare = "About", count = 2 })));
        Assert.Contains("from 0 to 10000", await Title(History("ChangeCount", new { historyField = "Sprint", compare = "Gte", count = 10001 })));
        Assert.Contains("Unknown time zone", await Title(History("Changed", new
        {
            historyField = "Sprint", during = new { kind = "inPeriod", period = "today", timeZone = "Nope/Nowhere" },
        })));
        Assert.Contains("'from' is after 'to'", await Title(History("Changed", new
        {
            historyField = "Sprint", during = new { kind = "between", from = "2026-09-30", to = "2026-09-01" },
        })));
    }
```

(`History(...)` returns `object`, so `new[] { History(…), … }` is an `object[]`; `JsonSerializer` writes each element by its runtime type.)

Catalog tests: `OpsOf("history")` → `new[] { "Was", "Changed", "ChangeCount" }`; the endpoint test's history set → `{ "Was", "Changed", "ChangeCount" }`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~History|FullyQualifiedName~FilterCatalog"`
Expected: FAIL — `'during' is not part of 'Changed'`, `Operator 'ChangeCount' is not allowed for field 'history'`, the catalog operator sets.

- [ ] **Step 3: Extract the date readers so windows reuse them (behaviour unchanged)**

Replace `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.Dates.cs` with:

```csharp
using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// Date fields: comparisons, Between, and the relative operators resolved against the clock. The
/// readers (ReadSince, ReadPeriod, ReadDateRange) are shared with history windows ('during'), so
/// "in the last", "in period" and "between" follow one set of rules wherever they appear.
/// </summary>
public sealed partial class IssueFilterTranslator
{
    private readonly IClock _clock;

    /// <summary>
    /// The clock is what makes a relative condition relative: "in the last 14 days" is resolved
    /// against it on every build, so a saved view means the same thing whenever it is opened.
    /// </summary>
    public IssueFilterTranslator(IClock clock) => _clock = clock;

    private (Expression<Func<Issue, bool>>?, string?) BuildDate(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, DateTime>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];

        switch (c.Op)
        {
            case FilterOperator.Between: return BuildDateBetween(c, body, p);
            case FilterOperator.InLast or FilterOperator.NotInLast: return BuildDateInLast(c, body, p);
            case FilterOperator.InPeriod: return BuildDateInPeriod(c, body, p);
        }

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

    private static readonly Regex DateOnlyShape = new(@"^\d{4}-\d{2}-\d{2}\z", RegexOptions.CultureInvariant);

    /// <summary>An ISO-8601 date or timestamp, read as UTC like every other date here.</summary>
    private static bool TryReadDate(JsonElement v, out DateTime value, out bool dateOnly)
    {
        value = default; dateOnly = false;
        if (v.ValueKind != JsonValueKind.String) return false;
        var s = v.GetString()!;
        dateOnly = DateOnlyShape.IsMatch(s);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
    }

    /// <summary>`from ≤ x ≤ to`, either bound open; see ReadDateRange and DateRangeTest.</summary>
    private static (Expression<Func<Issue, bool>>?, string?) BuildDateBetween(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (ReadBounds(c, out var fromEl, out var toEl) is { } boundsError) return (null, boundsError);
        if (ReadDateRange(fromEl, toEl, $"'Between' on '{c.Field}'", "'values'",
                out var from, out var to, out var toDateOnly) is { } error)
            return (null, error);
        // Only an open-ended `to` with no `from` leaves nothing to compare: every issue matches.
        return (Lambda(DateRangeTest(body, from, to, toDateOnly, d => Expression.Constant(d))
                       ?? Expression.Constant(true), p), null);
    }

    /// <summary>
    /// [from, to] as ISO-8601 dates or timestamps (UTC), either absent. A date-only `to` means
    /// "through that day" (DateRangeTest adds the day); `from` after the effective end is refused.
    /// </summary>
    private static string? ReadDateRange(
        JsonElement? fromEl, JsonElement? toEl, string what, string where,
        out DateTime? from, out DateTime? to, out bool toDateOnly)
    {
        from = null; to = null; toDateOnly = false;
        var notDates = $"{what} requires ISO-8601 dates (or null) in {where}.";
        if (fromEl is { } f)
        {
            if (!TryReadDate(f, out var d, out _)) return notDates;
            from = d;
        }
        if (toEl is { } t)
        {
            if (!TryReadDate(t, out var d, out toDateOnly)) return notDates;
            to = d;
        }
        // "Through 9999-12-31" is through the last day a DateTime can hold: it has no next midnight
        // (AddDays would overflow), and it excludes no instant at all — so it is an open end,
        // which is exactly what it means, rather than a 400 for a date the parser accepted.
        if (toDateOnly && to!.Value.Date == DateTime.MaxValue.Date) { to = null; toDateOnly = false; }

        // Judged against the effective end, so "from 10:00 on the 30th to the 30th" is not reversed.
        if (from is { } a && to is { } b && (toDateOnly ? a >= b.AddDays(1) : a > b))
            return $"{what}: 'from' is after 'to'.";
        return null;
    }

    /// <summary>
    /// `from ≤ x`, and `x &lt; to + 1 day` for a date-only `to` or `x ≤ to` for a timestamp.
    /// <paramref name="value"/> decides how a bound enters the tree (constant or parameter).
    /// Null when both ends are open.
    /// </summary>
    private static Expression? DateRangeTest(
        Expression body, DateTime? from, DateTime? to, bool toDateOnly, Func<DateTime, Expression> value)
    {
        Expression? cmp = null;
        if (from is { } lo) cmp = Expression.GreaterThanOrEqual(body, value(lo));
        if (to is { } hi)
            cmp = And(cmp, toDateOnly
                ? Expression.LessThan(body, value(hi.AddDays(1)))
                : Expression.LessThanOrEqual(body, value(hi)));
        return cmp;
    }

    /// <summary>InLast: `x ≥ now − amount·unit`. NotInLast: `x < now − amount·unit` ("more than … ago").</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildDateInLast(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        var shape = $"'{c.Op}' on '{c.Field}' requires 'value': {{\"amount\": 14, \"unit\": \"days\"}}.";
        if (ReadSince(c.Value ?? default, shape, out var since) is { } error) return (null, error);

        // A parameter, not a constant: the boundary moves on every request, and a constant would
        // make each request a new query to EF — a compiled-query cache entry per tick, never reused.
        var at = Parameter(since);
        return (Lambda(c.Op == FilterOperator.InLast
            ? Expression.GreaterThanOrEqual(body, at)
            : Expression.LessThan(body, at), p), null);
    }

    /// <summary>{"amount", "unit"} → now − amount·unit. Null = read; otherwise the 400's message.</summary>
    private string? ReadSince(JsonElement v, string shapeError, out DateTime since)
    {
        since = default;
        if (v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number
            || !v.TryGetProperty("unit", out var u) || u.ValueKind != JsonValueKind.String)
            return shapeError;
        if (!a.TryGetInt32(out var amount) || amount < 1 || amount > RelativeDates.MaxAmount)
            return $"'amount' must be a whole number from 1 to {RelativeDates.MaxAmount}.";
        var unit = u.GetString()!;
        if (!RelativeDates.Units.Contains(unit))
            return $"Unknown unit '{unit}'. Use one of: {string.Join(", ", RelativeDates.Units)}.";
        since = RelativeDates.Subtract(_clock.UtcNow, amount, unit);
        return null;
    }

    /// <summary>`start ≤ x < end` of the calendar period containing now, in the condition's zone (missing = UTC).</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildDateInPeriod(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        var shape = $"'InPeriod' on '{c.Field}' requires 'value': {{\"period\": \"thisWeek\", \"timeZone\": \"Europe/Warsaw\"}}.";
        if (ReadPeriod(c.Value ?? default, shape, out var start, out var end) is { } error) return (null, error);
        return (Lambda(Expression.AndAlso(
            Expression.GreaterThanOrEqual(body, Expression.Constant(start)),
            Expression.LessThan(body, Expression.Constant(end))), p), null);
    }

    /// <summary>{"period", "timeZone"?} → the period's [start, end) in UTC. Null = read; otherwise the 400's message.</summary>
    private string? ReadPeriod(JsonElement v, string shapeError, out DateTime start, out DateTime end)
    {
        start = default; end = default;
        if (v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty("period", out var pe) || pe.ValueKind != JsonValueKind.String)
            return shapeError;
        var period = pe.GetString()!;
        if (!RelativeDates.Periods.Contains(period))
            return $"Unknown period '{period}'. Use one of: {string.Join(", ", RelativeDates.Periods)}.";

        string? zoneId = null;
        if (v.TryGetProperty("timeZone", out var tz) && tz.ValueKind != JsonValueKind.Null)
        {
            if (tz.ValueKind != JsonValueKind.String)
                return "'timeZone' must be an IANA time zone id, such as \"Europe/Warsaw\".";
            zoneId = tz.GetString();
        }
        if (!RelativeDates.TryResolveZone(zoneId, out var zone))
            return $"Unknown time zone '{zoneId}'. Use an IANA id, such as \"Europe/Warsaw\".";

        (start, end) = RelativeDates.Period(_clock.UtcNow, period, zone);
        return null;
    }
}
```

Run: `cd C:/jw-filters && dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~DateOperatorTranslationTests"`
Expected: PASS — every existing date test (messages, boundaries, the parameter test) is unchanged by the extraction.

- [ ] **Step 4: Add windows and `ChangeCount` to the history partial**

(a) `FilterField.cs` — the history entry's ops become `Ops(FilterOperator.Was, FilterOperator.Changed, FilterOperator.ChangeCount)`.

(b) In `IssueFilterTranslator.History.cs`:

Add after `MaxHistoryValueLength`:

```csharp
    internal const int MaxChangeCount = 10_000;

    private static readonly string[] Compares = { "Eq", "Neq", "Gt", "Gte", "Lt", "Lte" };
```

Replace `HistoryKeys` with:

```csharp
    /// <summary>The keys each history operator takes, in their canonical order. Anything else is refused.</summary>
    private static readonly IReadOnlyDictionary<FilterOperator, string[]> HistoryKeys =
        new Dictionary<FilterOperator, string[]>
        {
            [FilterOperator.Was] = new[] { "historyField", "values" },
            [FilterOperator.Changed] = new[] { "historyField", "from", "to", "by", "during" },
            [FilterOperator.ChangeCount] = new[] { "historyField", "compare", "count", "during" },
        };
```

Replace the `switch` in `BuildHistoryAsync` with:

```csharp
        return c.Op switch
        {
            FilterOperator.Was => await BuildWasAsync(scope!, resolveDict, ct),
            FilterOperator.Changed => BuildChanged(scope!),
            FilterOperator.ChangeCount => BuildChangeCount(scope!),
            _ => (null, $"Operator '{c.Op}' is not allowed for field 'history'."),
        };
```

Replace `BuildChanged` (it now reads the window, which needs the clock, so it is an instance method):

```csharp
    /// <summary>Changed: some row for the field satisfies every part given — from, to, by, during. No parts: changed at all.</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildChanged(HistoryScope s)
    {
        if (ReadHistoryValues(s.Value, "from", allowEmpty: true, out var from) is { } e1) return (null, e1);
        if (ReadHistoryValues(s.Value, "to", allowEmpty: true, out var to) is { } e2) return (null, e2);
        if (ReadHistoryValues(s.Value, "by", allowEmpty: false, out var by) is { } e3) return (null, e3);
        if (ReadDuring(s.Value, out var window) is { } e4) return (null, e4);

        var test = s.Rows;
        if (from is not null)
            test = Expression.AndAlso(test, MatchValue(Member(s.Row, nameof(IssueChangeHistory.FromValue)), from));
        if (to is not null)
            test = Expression.AndAlso(test, MatchValue(Member(s.Row, nameof(IssueChangeHistory.ToValue)), to));
        // Authors exactly, as Jira shows them. A row with no author matches no name.
        if (by is not null)
            test = Expression.AndAlso(test, Expression.Call(
                Parameter(by.Names), ListContains<string>(), Member(s.Row, nameof(IssueChangeHistory.Author))));
        if (window is not null && WindowTest(Member(s.Row, nameof(IssueChangeHistory.ChangedAt)), window) is { } inWindow)
            test = Expression.AndAlso(test, inWindow);
        return (Lambda(AnyRow(s, test), s.Issue), null);
    }
```

Add after `BuildChanged`:

```csharp
    /// <summary>
    /// ChangeCount: how many rows the field has (inside the window, if given), compared to
    /// `count`. An issue with no rows counts 0, so "= 0" finds the never-changed ones.
    /// </summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildChangeCount(HistoryScope s)
    {
        if (!s.Value.TryGetProperty("compare", out var cmp) || cmp.ValueKind != JsonValueKind.String
            || !Compares.Contains(cmp.GetString()))
            return (null, $"'compare' must be one of: {string.Join(", ", Compares)}.");
        if (!s.Value.TryGetProperty("count", out var n) || n.ValueKind != JsonValueKind.Number
            || !n.TryGetInt32(out var count) || count < 0 || count > MaxChangeCount)
            return (null, $"'count' must be a whole number from 0 to {MaxChangeCount}.");
        if (ReadDuring(s.Value, out var window) is { } error) return (null, error);

        var test = s.Rows;
        if (window is not null && WindowTest(Member(s.Row, nameof(IssueChangeHistory.ChangedAt)), window) is { } inWindow)
            test = Expression.AndAlso(test, inWindow);
        var changes = Expression.Call(QueryableCount, Source(s.Source),
            Expression.Quote(Expression.Lambda<Func<IssueChangeHistory, bool>>(test, s.Row)));

        var k = Parameter(count);
        Expression body = cmp.GetString() switch
        {
            "Eq" => Expression.Equal(changes, k),
            "Neq" => Expression.NotEqual(changes, k),
            "Gt" => Expression.GreaterThan(changes, k),
            "Gte" => Expression.GreaterThanOrEqual(changes, k),
            "Lt" => Expression.LessThan(changes, k),
            _ => Expression.LessThanOrEqual(changes, k),   // Lte
        };
        return (Lambda(body, s.Issue), null);
    }

    /// <summary>A resolved `during`: ChangedAt ≥ From (if any), and &lt; To (exclusive) or ≤ To (if any).</summary>
    private sealed record Window(DateTime? From, DateTime? To, bool ToExclusive);

    private const string WindowShape =
        "'during' must look like {\"kind\": \"inLast\", \"amount\": 30, \"unit\": \"days\"}, "
        + "{\"kind\": \"inPeriod\", \"period\": \"lastMonth\", \"timeZone\": \"Europe/Warsaw\"} "
        + "or {\"kind\": \"between\", \"from\": \"2026-09-01\", \"to\": \"2026-09-30\"}.";

    /// <summary>`during`, if given: absent or null means no window.</summary>
    private string? ReadDuring(JsonElement v, out Window? window)
    {
        window = null;
        return v.TryGetProperty("during", out var d) && d.ValueKind != JsonValueKind.Null
            ? ReadWindow(d, out window)
            : null;
    }

    /// <summary>The three window kinds, read by the same rules as InLast, InPeriod and date Between.</summary>
    private string? ReadWindow(JsonElement v, out Window? window)
    {
        window = null;
        if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String)
            return WindowShape;
        switch (k.GetString())
        {
            case "inLast":
                if (ReadSince(v, WindowShape, out var since) is { } e1) return e1;
                window = new Window(since, null, false);
                return null;
            case "inPeriod":
                if (ReadPeriod(v, WindowShape, out var start, out var end) is { } e2) return e2;
                window = new Window(start, end, true);
                return null;
            case "between":
                JsonElement? fromEl = v.TryGetProperty("from", out var f) && f.ValueKind != JsonValueKind.Null ? f : null;
                JsonElement? toEl = v.TryGetProperty("to", out var t) && t.ValueKind != JsonValueKind.Null ? t : null;
                if (fromEl is null && toEl is null)
                    return "'during' between needs at least one bound; both are empty.";
                if (ReadDateRange(fromEl, toEl, "'during' between", "'from' and 'to'",
                        out var lo, out var hi, out var dateOnly) is { } e3)
                    return e3;
                // A date-only end is "through that day": exclusive at the next midnight.
                window = new Window(lo, dateOnly && hi is { } day ? day.AddDays(1) : hi, dateOnly);
                return null;
            default:
                return $"Unknown 'during' kind '{k.GetString()}'. Use one of: inLast, inPeriod, between.";
        }
    }

    /// <summary>
    /// ChangedAt inside the window. Bounds are parameters, so a moving "last 30 days" never churns
    /// EF's query cache. Null when the window bounds nothing.
    /// </summary>
    private static Expression? WindowTest(Expression changedAt, Window w)
    {
        Expression? test = null;
        if (w.From is { } lo) test = Expression.GreaterThanOrEqual(changedAt, Parameter(lo));
        if (w.To is { } hi)
            test = And(test, w.ToExclusive
                ? Expression.LessThan(changedAt, Parameter(hi))
                : Expression.LessThanOrEqual(changedAt, Parameter(hi)));
        return test;
    }

    private static readonly MethodInfo QueryableCount = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Count) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(IssueChangeHistory));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~History|FullyQualifiedName~FilterCatalog|FullyQualifiedName~DateOperator|FullyQualifiedName~RelativeDate"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Infrastructure tests/JiraUnpacked.Tests
git commit -m "feat(filters): history ChangeCount and during windows, sharing the date readers" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The value lists — `history-fields` and `history-values` (server)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IHistoryValueService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/HistoryValueService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` (register next to `IFilterValueService`)
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/HistoryTestData.cs` (bulk seeders)
- Create: `tests/JiraUnpacked.Tests/Integration/HistoryValueListTests.cs`

**Interfaces:**
- Produces: `public sealed record HistoryFieldCount(string Name, int Count)`; `public sealed record HistoryFieldsResult(IReadOnlyList<HistoryFieldCount>? Fields)` (null = 404); `public sealed record HistoryValuesResult(IReadOnlyList<FilterValueCount>? Values, int EmptyCount, string? Error)` (Error = 400, Values null + Error null = 404); `public interface IHistoryValueService { Task<HistoryFieldsResult> GetFieldsAsync(Guid projectId, CancellationToken ct = default); Task<HistoryValuesResult> GetValuesAsync(Guid projectId, string? field, string? kind, CancellationToken ct = default); }`.
- Produces (HTTP): `GET /api/projects/{id}/history-fields` → `{ "fields": [{ "name", "count" }] }`; `GET /api/projects/{id}/history-values?field=…&kind=value|author` → `{ "values": [{ "name", "count" }], "emptyCount" }`. Task 7 (`getHistoryFields`) and Task 8 (`getHistoryValues`) call these.
- Produces (tests): `HistoryTestData.SeedManyFieldsAsync(SeedContext ctx, int count)`, `HistoryTestData.SeedManyValuesAsync(SeedContext ctx, string field, int count)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/JiraUnpacked.Tests/Integration/HistoryTestData.cs` (inside the class):

```csharp
    /// <summary><paramref name="count"/> distinct fields ("F0001"…), one row each, in one statement.</summary>
    internal static Task SeedManyFieldsAsync(SeedContext ctx, int count) => BulkAsync(ctx,
        @"SELECT gen_random_uuid(), @t, @p, 'BULK', 'f-' || g, 'F' || lpad(g::text, 4, '0'), NULL, 'x', 'Alice', now()
          FROM generate_series(1, @n) AS g",
        ("n", count));

    /// <summary><paramref name="count"/> rows of one field with distinct ToValues ("V0001"…) and no FromValue.</summary>
    internal static Task SeedManyValuesAsync(SeedContext ctx, string field, int count) => BulkAsync(ctx,
        @"SELECT gen_random_uuid(), @t, @p, 'BULK', 'v-' || g, @field, NULL, 'V' || lpad(g::text, 4, '0'), 'Alice', now()
          FROM generate_series(1, @n) AS g",
        ("n", count), ("field", field));

    private static async Task BulkAsync(SeedContext ctx, string select, params (string, object?)[] ps)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") "
            + select,
            ps.Append(("t", (object?)ctx.Tenant)).Append(("p", (object?)ctx.ProjectId)).ToArray());
    }
```

Create `tests/JiraUnpacked.Tests/Integration/HistoryValueListTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/HistoryValueListTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The two value lists behind the history row: the fields this project has history for, and
/// one field's values (list elements split out) or authors. Tenant-scoped, 404 for a project
/// that is not the caller's — like filter-values.
/// </summary>
public sealed class HistoryValueListTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public HistoryValueListTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record NameCount(string Name, int Count);
    private sealed record FieldsDto(List<NameCount> Fields);
    private sealed record ValuesDto(List<NameCount> Values, int EmptyCount);

    private async Task<(HttpClient client, SeedContext ctx)> TenantAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        return (client, await MetricsTestData.SeedProjectAsync(tenant));
    }

    private static async Task<T> OkAsync<T>(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Fields_are_listed_most_changed_first_with_their_row_counts()
    {
        var (client, ctx) = await TenantAsync();
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_factory);
        foreach (var (field, n) in new[] { ("status", 2), ("Sprint", 3), ("labels", 2) })
            for (var i = 0; i < n; i++) await HistoryTestData.SeedChangeAsync(ctx, "1", field, null, "x");
        // Another tenant's rows under this project id are never counted.
        for (var i = 0; i < 5; i++) await HistoryTestData.SeedChangeAsync(ctx, "1", "status", null, "x", asTenant: other);

        var dto = await OkAsync<FieldsDto>(client, $"/api/projects/{ctx.ProjectId}/history-fields");
        Assert.Equal(new[] { new NameCount("Sprint", 3), new NameCount("labels", 2), new NameCount("status", 2) }, dto.Fields);
    }

    [Fact]
    public async Task At_most_200_fields_are_listed()
    {
        var (client, ctx) = await TenantAsync();
        await HistoryTestData.SeedManyFieldsAsync(ctx, 201);

        var dto = await OkAsync<FieldsDto>(client, $"/api/projects/{ctx.ProjectId}/history-fields");
        Assert.Equal(200, dto.Fields.Count);
    }

    [Fact]
    public async Task Values_split_jiras_lists_and_count_from_and_to_alike()
    {
        var (client, ctx) = await TenantAsync();
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_factory);
        await HistoryTestData.SeedChangeAsync(ctx, "1", "Sprint", null, "PLAT 100");
        await HistoryTestData.SeedChangeAsync(ctx, "1", "Sprint", "PLAT 100", "PLAT 100, PLAT 101", author: "Bob");
        await HistoryTestData.SeedChangeAsync(ctx, "2", "Sprint", "", "plat 99", author: null);
        await HistoryTestData.SeedChangeAsync(ctx, "2", "Sprint", null, "Secret", author: "Mallory", asTenant: other);

        var values = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=Sprint&kind=value");
        Assert.Equal(new[] { new NameCount("PLAT 100", 3), new NameCount("PLAT 101", 1), new NameCount("plat 99", 1) }, values.Values);
        Assert.Equal(2, values.EmptyCount);   // the null and the "" FromValue

        var authors = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=Sprint&kind=author");
        Assert.Equal(new[] { new NameCount("Alice", 1), new NameCount("Bob", 1) }, authors.Values);
        Assert.Equal(1, authors.EmptyCount);

        // kind defaults to value.
        var byDefault = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=Sprint");
        Assert.Equal(values.Values, byDefault.Values);
    }

    [Fact]
    public async Task At_most_1000_values_are_listed()
    {
        var (client, ctx) = await TenantAsync();
        await HistoryTestData.SeedManyValuesAsync(ctx, "labels", 1001);

        var dto = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=labels");
        Assert.Equal(1000, dto.Values.Count);
        Assert.Equal(1001, dto.EmptyCount);   // every FromValue is null
    }

    [Fact]
    public async Task An_unknown_field_is_an_empty_list_and_bad_requests_are_400()
    {
        var (client, ctx) = await TenantAsync();

        var none = await OkAsync<ValuesDto>(client, $"/api/projects/{ctx.ProjectId}/history-values?field=Nope");
        Assert.Empty(none.Values);
        Assert.Equal(0, none.EmptyCount);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/projects/{ctx.ProjectId}/history-values?field=%20")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/projects/{ctx.ProjectId}/history-values?field=Sprint&kind=colour")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_or_foreign_project_is_404_before_anything_else()
    {
        var (client, _) = await TenantAsync();
        var (_, otherTenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var foreign = await MetricsTestData.SeedProjectAsync(otherTenant);

        foreach (var id in new[] { Guid.NewGuid(), foreign.ProjectId })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{id}/history-fields")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{id}/history-values?field=Sprint")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{id}/history-values?field=%20")).StatusCode);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~HistoryValueListTests"`
Expected: FAIL — every request answers 404 (no such route).

- [ ] **Step 3: Implement**

Create `src/JiraUnpacked.Domain/Abstractions/IHistoryValueService.cs`:

```csharp
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>A changelog field this project has history for, and how many changelog rows it has.</summary>
public sealed record HistoryFieldCount(string Name, int Count);

/// <summary>Null Fields => the project is unknown or another tenant's => 404.</summary>
public sealed record HistoryFieldsResult(IReadOnlyList<HistoryFieldCount>? Fields);

/// <summary>
/// Values + null Error => 200. Error => 400. Both null => the project is unknown or another
/// tenant's => 404. EmptyCount is the number behind the picker's (Empty) entry.
/// </summary>
public sealed record HistoryValuesResult(IReadOnlyList<FilterValueCount>? Values, int EmptyCount, string? Error);

/// <summary>
/// What the history row's pickers offer: the fields this project's changelog has, and one field's
/// values or authors. Counts are over the whole project's changelog, like the filter-values
/// counts are over the whole project.
/// </summary>
public interface IHistoryValueService
{
    Task<HistoryFieldsResult> GetFieldsAsync(Guid projectId, CancellationToken ct = default);

    /// <param name="kind">"value" (From and To, lists split on ", ") or "author".</param>
    Task<HistoryValuesResult> GetValuesAsync(Guid projectId, string? field, string? kind, CancellationToken ct = default);
}
```

Create `src/JiraUnpacked.Infrastructure/Reporting/HistoryValueService.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// The value lists behind history conditions, from this project's changelog. Every read goes
/// through the injected tenant-scoped AppDbContext, so another tenant's rows are unreachable.
/// Grouped in the database and capped there; only the split of Jira's ", "-joined lists happens
/// in memory, over at most <see cref="MaxRawValues"/> distinct strings per side.
/// </summary>
public sealed class HistoryValueService : IHistoryValueService
{
    internal const int MaxFields = 200;
    internal const int MaxValues = 1000;

    /// <summary>The most frequent distinct raw strings read per side before splitting — bounds a field whose every value is unique.</summary>
    internal const int MaxRawValues = 5000;

    private const string ListSeparator = ", ";

    private readonly AppDbContext _db;
    public HistoryValueService(AppDbContext db) => _db = db;

    public async Task<HistoryFieldsResult> GetFieldsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return new HistoryFieldsResult(null);

        var fields = await _db.IssueChangeHistory
            .Where(h => h.JiraProjectId == projectId)
            .GroupBy(h => h.Field)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name)
            .Take(MaxFields)
            .ToListAsync(ct);
        return new HistoryFieldsResult(fields.Select(f => new HistoryFieldCount(f.Name, f.Count)).ToList());
    }

    public async Task<HistoryValuesResult> GetValuesAsync(
        Guid projectId, string? field, string? kind, CancellationToken ct = default)
    {
        // Project first: an unknown or foreign project is a 404 whatever else the request says.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return new HistoryValuesResult(null, 0, null);
        if (string.IsNullOrWhiteSpace(field))
            return new HistoryValuesResult(null, 0, "'field' is required: the history field to list values for.");
        if (kind is not ("value" or "author"))
            return new HistoryValuesResult(null, 0, "'kind' must be 'value' or 'author'.");

        // The field is data, not a closed catalog: one no row uses simply lists nothing.
        var rows = _db.IssueChangeHistory.Where(h => h.JiraProjectId == projectId && h.Field == field);
        var groups = kind == "author"
            ? await GroupedAsync(rows.Select(h => h.Author), ct)
            : (await GroupedAsync(rows.Select(h => h.FromValue), ct))
                .Concat(await GroupedAsync(rows.Select(h => h.ToValue), ct)).ToList();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var empty = 0;
        foreach (var (value, count) in groups)
        {
            if (string.IsNullOrEmpty(value)) { empty += count; continue; }
            // Authors are names; values may be Jira's ", "-joined lists, offered element by element —
            // exactly what the filter matches.
            var elements = kind == "author" ? new[] { value } : value.Split(ListSeparator);
            foreach (var element in elements.Where(e => e.Length > 0).Distinct(StringComparer.Ordinal))
                counts[element] = counts.GetValueOrDefault(element) + count;
        }

        var values = counts
            .Select(kv => new FilterValueCount(kv.Key, kv.Value))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.Ordinal)
            .Take(MaxValues)
            .ToList();
        return new HistoryValuesResult(values, empty, null);
    }

    /// <summary>One side's distinct strings with their row counts, most frequent first, capped.</summary>
    private static async Task<List<(string? Value, int Count)>> GroupedAsync(IQueryable<string?> side, CancellationToken ct) =>
        (await side
            .GroupBy(v => v)
            .Select(g => new { Value = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(MaxRawValues)
            .ToListAsync(ct))
        .Select(x => (x.Value, x.Count))
        .ToList();
}
```

`src/JiraUnpacked.Infrastructure/DependencyInjection.cs` — after `services.AddScoped<IFilterValueService, FilterValueService>();`:

```csharp
        services.AddScoped<IHistoryValueService, HistoryValueService>();
```

`src/JiraUnpacked.Api/Controllers/IssuesController.cs` — inject the service and add the two actions after `FilterValues`:

```csharp
    private readonly IIssueQueryService _issues;
    private readonly IFilterValueService _filterValues;
    private readonly IHistoryValueService _history;

    public IssuesController(IIssueQueryService issues, IFilterValueService filterValues, IHistoryValueService history)
    {
        _issues = issues;
        _filterValues = filterValues;
        _history = history;
    }
```

```csharp
    /// <summary>The changelog fields this project has history for, most-changed first — the history row's field list.</summary>
    [HttpGet("api/projects/{projectId:guid}/history-fields")]
    public async Task<IActionResult> HistoryFields(Guid projectId, CancellationToken ct = default)
    {
        var result = await _history.GetFieldsAsync(projectId, ct);
        return result.Fields is null ? NotFound() : Ok(new { fields = result.Fields });
    }

    /// <summary>
    /// One history field's values (kind=value, the default: From and To, Jira's lists split into
    /// elements) or authors (kind=author), by name. An unknown field is an empty list, not a 400:
    /// fields are data here, not a closed catalog.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/history-values")]
    public async Task<IActionResult> HistoryValues(
        Guid projectId, [FromQuery] string? field, [FromQuery] string? kind, CancellationToken ct = default)
    {
        var result = await _history.GetValuesAsync(projectId, field, kind ?? "value", ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        if (result.Values is null) return NotFound();
        return Ok(new { values = result.Values, emptyCount = result.EmptyCount });
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~HistoryValueListTests|FullyQualifiedName~FilterValuesTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain src/JiraUnpacked.Infrastructure src/JiraUnpacked.Api tests/JiraUnpacked.Tests
git commit -m "feat(filters): history-fields and history-values for the history pickers" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The performance gate (read-only, local dev database)

**Files:**
- Create: `tests/JiraUnpacked.Tests/Performance/HistoryQueryPlanTests.cs`
- Only if the gate misses: Modify `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueChangeHistoryConfiguration.cs`; generated `src/JiraUnpacked.Infrastructure/Persistence/Migrations/*_AddChangeHistoryIssueFieldIndex.cs` (+ `.Designer.cs`, model snapshot)

**Interfaces:**
- Consumes: `IssueQueryService(AppDbContext, IIssueFilterCompiler)`, `IssueFilterCompiler(AppDbContext, IIssueFilterTranslator)`, `IssueFilterTranslator(IClock)`, `FakeClock`, `IssueFilterRequest`, `FilterGroup`, `FilterCondition`.
- Produces: an opt-in test (`JU_PERF_DB`); without the variable it returns at once, so the normal suite never touches a real database. Its output — six operators' total execution times and their plans — is the gate's record.

- [ ] **Step 1: Write the gate**

Create `tests/JiraUnpacked.Tests/Performance/HistoryQueryPlanTests.cs`:

```csharp
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit.Abstractions;

namespace JiraUnpacked.Tests.Performance;

/// <summary>
/// The history-conditions performance gate (spec §2): the whole-project list query — page 1,
/// count included — for each operator must finish under 2 s on the largest project.
///
/// Opt-in. Runs only when JU_PERF_DB holds a connection string, and refuses one whose session is
/// not read-only; without it the test returns at once, so the normal suite never touches a real
/// database. Every statement IssueQueryService sends is first run under
/// EXPLAIN (ANALYZE, BUFFERS) on the same connection, and the plans go to the test output.
/// </summary>
public sealed class HistoryQueryPlanTests
{
    private const double BudgetMs = 2000;
    private readonly ITestOutputHelper _out;

    public HistoryQueryPlanTests(ITestOutputHelper output) => _out = output;

    private sealed class FixedTenant : ICurrentTenant
    {
        public FixedTenant(Guid id) => EndUserId = id;
        public Guid? EndUserId { get; }
    }

    /// <summary>EXPLAIN ANALYZE of each command just before EF runs it; plans and execution times kept.</summary>
    private sealed class ExplainInterceptor : DbCommandInterceptor
    {
        public readonly List<(string Sql, IReadOnlyList<string> Plan, double Ms)> Statements = new();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await ExplainAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            await ExplainAsync(command, cancellationToken);
            return result;
        }

        private async Task ExplainAsync(DbCommand command, CancellationToken ct)
        {
            await using var explain = command.Connection!.CreateCommand();
            explain.Transaction = command.Transaction;
            explain.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + command.CommandText;
            foreach (NpgsqlParameter p in command.Parameters) explain.Parameters.Add(p.Clone());
            var plan = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) plan.Add(reader.GetString(0));
            var ms = plan
                .Select(l => Regex.Match(l, @"Execution Time: ([\d.]+) ms"))
                .Where(m => m.Success)
                .Sum(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            Statements.Add((command.CommandText, plan, ms));
        }
    }

    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    [Fact]
    public async Task Every_history_operator_lists_the_largest_project_under_two_seconds()
    {
        var connectionString = Environment.GetEnvironmentVariable("JU_PERF_DB");
        if (string.IsNullOrEmpty(connectionString))
        {
            _out.WriteLine("JU_PERF_DB is not set: the performance gate did not run.");
            return;
        }

        // ---- the read-only guard, and what to measure, on a plain connection ----
        Guid tenant, project;
        string status, sprint, author;
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            async Task<object?> Scalar(string sql, Guid? p = null)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                if (p is { } id) cmd.Parameters.AddWithValue("p", id);
                return await cmd.ExecuteScalarAsync();
            }

            // Never a write, by construction: refuse a session that could make one.
            Assert.Equal("on", (string?)await Scalar("SHOW default_transaction_read_only"));
            _out.WriteLine($"Connected as {await Scalar("SELECT current_user")} (read-only).");

            await using (var cmd = new NpgsqlCommand(
                @"SELECT ""EndUserId"", ""JiraProjectId"", count(*) FROM ""Issues"" GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 1", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                Assert.True(await r.ReadAsync(), "The database has no issues.");
                tenant = r.GetGuid(0);
                project = r.GetGuid(1);
                _out.WriteLine($"Largest project {project}: {r.GetInt64(2)} issues.");
            }
            _out.WriteLine($"History rows: {await Scalar(@"SELECT count(*) FROM ""IssueChangeHistory"" WHERE ""JiraProjectId"" = @p", project)}.");

            status = (string?)await Scalar(@"SELECT ""ToValue"" FROM ""IssueChangeHistory"" WHERE ""JiraProjectId"" = @p
                AND ""Field"" = 'status' AND ""ToValue"" IS NOT NULL GROUP BY 1 ORDER BY count(*) DESC LIMIT 1", project) ?? "Done";
            var multi = (string?)await Scalar(@"SELECT ""ToValue"" FROM ""IssueChangeHistory"" WHERE ""JiraProjectId"" = @p
                AND ""Field"" = 'Sprint' AND ""ToValue"" LIKE '%, %' GROUP BY 1 ORDER BY count(*) DESC LIMIT 1", project);
            sprint = multi?.Split(", ").Last() ?? "Sprint 1";
            author = (string?)await Scalar(@"SELECT ""Author"" FROM ""IssueChangeHistory"" WHERE ""JiraProjectId"" = @p
                AND ""Field"" = 'Sprint' AND ""Author"" IS NOT NULL GROUP BY 1 ORDER BY count(*) DESC LIMIT 1", project) ?? "nobody";
            _out.WriteLine($"Measuring with status '{status}', sprint '{sprint}', author '{author}'.");
        }

        var cases = new (string Name, FilterOperator Op, object Value)[]
        {
            ("Was status (current value too)", FilterOperator.Was,
                new { historyField = "status", values = new[] { status } }),
            ("Was Sprint (list elements)", FilterOperator.Was,
                new { historyField = "Sprint", values = new[] { sprint } }),
            ("Changed Sprint", FilterOperator.Changed,
                new { historyField = "Sprint" }),
            ("Changed status to X by Y in the last 90 days", FilterOperator.Changed,
                new { historyField = "status", to = new[] { status }, by = new[] { author }, during = new { kind = "inLast", amount = 90, unit = "days" } }),
            ("ChangeCount Sprint >= 3", FilterOperator.ChangeCount,
                new { historyField = "Sprint", compare = "Gte", count = 3 }),
            ("ChangeCount Sprint = 0 in the last 90 days", FilterOperator.ChangeCount,
                new { historyField = "Sprint", compare = "Eq", count = 0, during = new { kind = "inLast", amount = 90, unit = "days" } }),
        };

        var failures = new List<string>();
        foreach (var (name, op, value) in cases)
        {
            var explain = new ExplainInterceptor();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString).AddInterceptors(explain).Options;
            await using var db = new AppDbContext(options, new FixedTenant(tenant));
            var service = new IssueQueryService(db,
                new IssueFilterCompiler(db, new IssueFilterTranslator(new FakeClock(DateTime.UtcNow))));

            var filter = new FilterGroup(FilterLogic.And, new List<FilterCondition> { new("history", op, J(value), null) }, null);
            var result = await service.QueryAsync(project, new IssueFilterRequest(null, filter, 1, 50));
            Assert.Null(result.Error);

            // The whole request: the project check, any name resolution, the count and page 1.
            var total = explain.Statements.Sum(s => s.Ms);
            _out.WriteLine($"== {name}: {result.Result!.TotalCount} matches, {total:F0} ms over {explain.Statements.Count} statements");
            foreach (var s in explain.Statements.Where(s => s.Sql.Contains("IssueChangeHistory")))
                foreach (var line in s.Plan) _out.WriteLine(line);
            if (total >= BudgetMs) failures.Add($"{name}: {total:F0} ms");
        }

        Assert.True(failures.Count == 0, "Over the 2 s budget: " + string.Join("; ", failures));
    }
}
```

- [ ] **Step 2: Check it is inert without the variable**

Run: `cd C:/jw-filters && dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~HistoryQueryPlanTests"`
Expected: PASS (it printed "did not run" and returned).

- [ ] **Step 3: Run the gate against the dev database, read-only**

First check the container is up and no sync is running (a running sync skews timings; the gate itself is read-only and safe either way): `docker ps --filter name=jiraunpacked-db --format "{{.Names}} {{.Status}}"` → `jiraunpacked-db Up …`.

Run (Bash):

```bash
cd C:/jw-filters && JU_PERF_DB='Host=localhost;Port=5432;Database=jiraunpacked;Username=jiraunpacked_reader;Password=localreader;Options=-c default_transaction_read_only=on;Command Timeout=120' \
  dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~HistoryQueryPlanTests" --logger "console;verbosity=detailed"
```

Expected: PASS, with six `== <operator>: N matches, T ms` lines, each `T` < 2000, followed by their plans. The reader role (`jiraunpacked_reader`) only has SELECT and the session option makes every transaction read-only; the test also refuses to run unless `default_transaction_read_only` is `on`.

If Postgres refuses the parameterised `EXPLAIN` (`42P02: there is no parameter $1`), that is an Npgsql/Postgres version quirk, not a gate failure: change `ExplainAsync` to `PREPARE` the statement and `EXPLAIN (ANALYZE, BUFFERS) EXECUTE` it — still read-only — and re-run.

Copy the six lines (operator, matches, ms) into this task's commit message body (Step 5) — that is the gate's record for the PR.

- [ ] **Step 4: Only if any operator is at or over 2 s — the composite index, measured on a copy**

Skip this step entirely when Step 3 passed.

(a) `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueChangeHistoryConfiguration.cs` — after the `(EndUserId, JiraProjectId, Field, ChangedAt)` index:

```csharp
        // Serves the history conditions' correlated subqueries (one issue, one field, a window):
        // added because the spec §2 gate measured the existing indexes over 2 s.
        b.HasIndex(x => new { x.JiraProjectId, x.JiraIssueId, x.Field, x.ChangedAt });
```

(b) Generate the migration (never hand-write it): `cd C:/jw-filters && dotnet ef migrations add AddChangeHistoryIssueFieldIndex -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Infrastructure -o Persistence/Migrations`. Then `dotnet ef migrations has-pending-model-changes -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Infrastructure` → `No changes have been made to the model since the last migration.`

(c) Measure on a COPY — the dev database is never written:

```bash
docker exec jiraunpacked-db createdb -U jiraunpacked ju_perf_history
docker exec jiraunpacked-db sh -c "pg_dump -U jiraunpacked jiraunpacked | psql -q -U jiraunpacked ju_perf_history"
cd C:/jw-filters && dotnet ef database update -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Infrastructure \
  --connection "Host=localhost;Port=5432;Database=ju_perf_history;Username=jiraunpacked;Password=localdev"
docker exec jiraunpacked-db psql -U jiraunpacked -d ju_perf_history -c 'ANALYZE "IssueChangeHistory";'
JU_PERF_DB='Host=localhost;Port=5432;Database=ju_perf_history;Username=jiraunpacked_reader;Password=localreader;Options=-c default_transaction_read_only=on;Command Timeout=120' \
  dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~HistoryQueryPlanTests" --logger "console;verbosity=detailed"
docker exec jiraunpacked-db dropdb -U jiraunpacked ju_perf_history
```

Expected: PASS on the copy. Record both runs (without and with the index) in the commit body. If it still misses with the index, stop and report the plans — do not widen the scope (no denormalised counters; the spec rules them out).

(d) Run the integration suite so `ju_test_history` gets the migration and nothing regresses: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~History"` → PASS.

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add tests/JiraUnpacked.Tests/Performance
# only if Step 4 ran:
git add src/JiraUnpacked.Infrastructure/Persistence
git commit -m "test(filters): opt-in EXPLAIN ANALYZE gate for history conditions" \
  -m "Gate (largest dev project, read-only): <paste the six '== …: N matches, T ms' lines; and, if Step 4 ran, the with-index run>" \
  -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

(Replace the angle-bracketed text with the actual lines from Step 3 — it is the record, not a placeholder to leave in.)

---

### Task 6: History values in the SPA model (wire types, reader, completeness, URL and saved views)

**Files:**
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts` (types only)
- Create: `src/JiraUnpacked.Web/src/features/issues/historyShapes.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/historyShapes.test.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterTree.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterTree.test.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.ts` (labels)
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.test.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/viewState.test.ts`
- Modify: `src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx` (catalog literal)

**Interfaces:**
- Produces (`api/issues.ts`): `FilterOperator` + `'Was' | 'Changed' | 'ChangeCount'`; `FieldType` + `'History'`; `type HistoryCompare = 'Eq' | 'Neq' | 'Gt' | 'Gte' | 'Lt' | 'Lte'`; `type HistoryWindow = { kind: 'inLast'; amount: number; unit: RelativeUnit } | { kind: 'inPeriod'; period: PeriodKey; timeZone?: string } | { kind: 'between'; from: string | null; to: string | null }`; `interface HistoryValue { historyField: string; values?: (string | null)[]; from?: (string | null)[]; to?: (string | null)[]; by?: string[]; compare?: HistoryCompare; count?: number; during?: HistoryWindow }`; `ConditionValue` includes `HistoryValue`; `FilterCatalog.maxHistoryConditions: number`.
- Produces (`historyShapes.ts`): `HISTORY_FIELD = 'history'`; `takesHistory(op): op is 'Was' | 'Changed' | 'ChangeCount'`; `MAX_HISTORY_FIELD_LENGTH = 100`, `MAX_HISTORY_VALUE_LENGTH = 255`, `MAX_CHANGE_COUNT = 10000`; `HISTORY_COMPARES: readonly { key: HistoryCompare; label: string }[]`; `DEFAULT_CHANGE_COUNT = { compare: 'Gte', count: 2 }`; `DEFAULT_WINDOW_AMOUNT: RelativeAmount = { amount: 30, unit: 'days' }`; `readWindow(v: unknown): HistoryWindow | undefined`; `isCompleteWindow(w: HistoryWindow): boolean`; `canonicalHistoryValue(op: FilterOperator, v: HistoryValue): HistoryValue`; `freshHistoryValue(op: FilterOperator, historyField: string): HistoryValue`; `readHistoryValue(op: FilterOperator, raw: unknown): HistoryValue | undefined`; `isCompleteHistoryValue(op: FilterOperator, v: HistoryValue | undefined): boolean`.
- Produces (`filterTree.ts`): `TreeCounts.history: number`; history handled by `isCompleteCondition`, `toWireCondition`, `freshCondition`, `switchOperator`, `countEnabled`, the URL reader.
- Produces (labels): `Was` → `was`, `Changed` → `changed`, `ChangeCount` → `number of changes`.

- [ ] **Step 1: Write the failing tests**

Create `src/JiraUnpacked.Web/src/features/issues/historyShapes.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import type { FilterOperator, HistoryValue } from '../../api/issues';
import { freshHistoryValue, isCompleteHistoryValue, readHistoryValue, readWindow } from './historyShapes';

describe('readHistoryValue', () => {
  it('reads each operator’s keys in canonical order, dropping explicit nulls and empty optional lists', () => {
    const counted = readHistoryValue('ChangeCount', {
      during: { unit: 'days', amount: 30, kind: 'inLast' }, count: 3, compare: 'Gte', historyField: 'Sprint',
    });
    expect(counted).toEqual({ historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 30, unit: 'days' } });
    expect(Object.keys(counted!)).toEqual(['historyField', 'compare', 'count', 'during']);
    expect(Object.keys(counted!.during!)).toEqual(['kind', 'amount', 'unit']);

    expect(readHistoryValue('Changed', { historyField: 'status', by: null, from: [], to: ['Done', 'Done', ''], during: null }))
      .toEqual({ historyField: 'status', to: ['Done', null] });
    expect(readHistoryValue('Was', { historyField: 'Sprint' })).toEqual({ historyField: 'Sprint', values: [] });
    expect(readHistoryValue('Changed', {
      historyField: 'Sprint', during: { kind: 'inPeriod', timeZone: 'Europe/Warsaw', period: 'lastMonth' },
    })?.during).toEqual({ kind: 'inPeriod', period: 'lastMonth', timeZone: 'Europe/Warsaw' });
  });

  it('refuses a value it cannot read whole, rather than reading less than it says', () => {
    const unreadable: [FilterOperator, unknown][] = [
      ['Was', { historyField: 'status', values: 'Done' }],
      ['Was', { historyField: 'status', values: ['Done'], during: { kind: 'inLast', amount: 1, unit: 'days' } }],
      ['Was', { values: ['x'] }],
      ['Changed', { historyField: 'status', by: [null] }],
      ['Changed', { historyField: 'status', to: [7] }],
      ['Changed', { historyField: 'status', during: { kind: 'fortnight' } }],
      ['Changed', { historyField: 'status', during: { kind: 'between', from: 5 } }],
      ['Changed', { historyField: 'status', colour: 'red' }],
      ['ChangeCount', { historyField: 'Sprint', compare: 'About', count: 3 }],
      ['ChangeCount', { historyField: 'Sprint', compare: 'Gte', count: '3' }],
      ['Eq', { historyField: 'status', values: ['x'] }],
      ['Was', 'status'],
    ];
    for (const [op, raw] of unreadable) expect(readHistoryValue(op, raw)).toBeUndefined();
  });

  it('reads the three window shapes and nothing else', () => {
    expect(readWindow({ kind: 'between', to: '2026-09-30' })).toEqual({ kind: 'between', from: null, to: '2026-09-30' });
    expect(readWindow({ kind: 'inLast', amount: '30', unit: 'days' })).toBeUndefined();
    expect(readWindow({ kind: 'inPeriod', period: 'fortnight' })).toBeUndefined();
    expect(readWindow(null)).toBeUndefined();
  });
});

describe('isCompleteHistoryValue', () => {
  it('needs a field; "was" a value; "number of changes" a compare and a whole count from 0 to 10000; and a finished window', () => {
    const done = (op: FilterOperator, v: HistoryValue) => isCompleteHistoryValue(op, v);
    expect(done('Changed', { historyField: '' })).toBe(false);
    expect(done('Changed', { historyField: '  ' })).toBe(false);
    expect(done('Changed', { historyField: 'x'.repeat(101) })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint' })).toBe(true);
    expect(done('Was', { historyField: 'Sprint', values: [] })).toBe(false);
    expect(done('Was', { historyField: 'Sprint', values: [null] })).toBe(true);
    expect(done('Was', { historyField: 'Sprint', values: ['x'.repeat(256)] })).toBe(false);
    expect(done('ChangeCount', { historyField: 'Sprint', compare: 'Eq', count: 0 })).toBe(true);
    expect(done('ChangeCount', { historyField: 'Sprint', compare: 'Eq' })).toBe(false);
    expect(done('ChangeCount', { historyField: 'Sprint', count: 2 })).toBe(false);
    expect(done('ChangeCount', { historyField: 'Sprint', compare: 'Eq', count: 2.5 })).toBe(false);
    expect(done('ChangeCount', { historyField: 'Sprint', compare: 'Eq', count: 10001 })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint', during: { kind: 'inLast', amount: 0, unit: 'days' } })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint', during: { kind: 'between', from: null, to: null } })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint', during: { kind: 'between', from: '2026-09-30', to: '2026-09-01' } })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint', during: { kind: 'between', from: null, to: '2026-09-30' } })).toBe(true);
    expect(isCompleteHistoryValue('Changed', undefined)).toBe(false);
  });

  it('starts each operator where the spec says', () => {
    expect(freshHistoryValue('Was', 'Sprint')).toEqual({ historyField: 'Sprint', values: [] });
    expect(freshHistoryValue('Changed', '')).toEqual({ historyField: '' });
    expect(freshHistoryValue('ChangeCount', '')).toEqual({ historyField: '', compare: 'Gte', count: 2 });
  });
});
```

Append to `src/JiraUnpacked.Web/src/features/issues/filterTree.test.ts` (and add `import type { HistoryValue } from '../../api/issues';` to its imports):

```ts
describe('history conditions', () => {
  it('starts each history operator fresh, and keeps the field (and the window) when switching between them', () => {
    expect(freshCondition('history', 'Was', 'UTC')).toEqual({ field: 'history', op: 'Was', value: { historyField: '', values: [] } });
    expect(freshCondition('history', 'Changed', 'UTC')).toEqual({ field: 'history', op: 'Changed', value: { historyField: '' } });
    expect(freshCondition('history', 'ChangeCount', 'UTC'))
      .toEqual({ field: 'history', op: 'ChangeCount', value: { historyField: '', compare: 'Gte', count: 2 } });

    const counted = {
      field: 'history', op: 'ChangeCount' as const,
      value: { historyField: 'Sprint', compare: 'Gt' as const, count: 4, during: { kind: 'inLast' as const, amount: 7, unit: 'days' as const } },
    };
    expect(switchOperator(counted, 'Changed', 'UTC'))
      .toEqual({ field: 'history', op: 'Changed', value: { historyField: 'Sprint', during: { kind: 'inLast', amount: 7, unit: 'days' } } });
    expect(switchOperator(counted, 'Was', 'UTC')).toEqual({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: [] } });
  });

  it('sends only finished history conditions, in canonical form', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'history', op: 'Was', value: { historyField: 'Sprint', values: [] } }),            // no value yet
        cond('b', { field: 'history', op: 'Changed', value: { historyField: '' } }),                         // no field yet
        cond('c', { field: 'history', op: 'Changed', value: { to: ['Done'], from: [], historyField: 'status' } }),
        cond('d', { field: 'history', op: 'ChangeCount', value: { historyField: 'Sprint', compare: 'Gte' } }), // no count
      ],
    });

    const sent = pruneTree(tree);
    expect(sent).toEqual({ logic: 'And', conditions: [{ field: 'history', op: 'Changed', value: { historyField: 'status', to: ['Done'] } }] });
    expect(Object.keys(sent!.conditions![0].value as object)).toEqual(['historyField', 'to']);
  });

  it('round-trips history values through the URL form, and a malformed one stays unfinished', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101', null] } }),
        cond('b', {
          field: 'history', op: 'Changed',
          value: { historyField: 'status', from: ['To Do'], by: ['Alice'], during: { kind: 'between', from: '2026-09-01', to: null } },
        }),
        cond('c', {
          field: 'history', op: 'ChangeCount',
          value: { historyField: 'Sprint', compare: 'Eq', count: 0, during: { kind: 'inPeriod', period: 'lastMonth', timeZone: 'Europe/Warsaw' } },
        }),
      ],
    });
    expect(pruneTree(parseFilterParam(toUrlJson(tree), null))).toEqual(pruneTree(tree));

    const bad = parseFilterParam(JSON.stringify({
      logic: 'And',
      conditions: [{ field: 'history', op: 'Changed', value: { historyField: 'status', during: { kind: 'fortnight' } } }],
    }), null);
    expect(bad.conditions[0].value).toBeUndefined();
    expect(isCompleteCondition(bad.conditions[0])).toBe(false);   // kept on screen, never broadened to "any time"
  });

  it('copies history values deeply when a condition is duplicated', () => {
    const original = cond('a', { field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101'] } });
    const copy = cloneCondition(original);
    (copy.value as HistoryValue).values!.push('PLAT 102');
    expect((original.value as HistoryValue).values).toEqual(['PLAT 101']);
  });

  it('counts enabled history conditions on their own, over the whole tree', () => {
    const h = (id: string, disabled = false): EditorCondition => ({
      id, field: 'history', op: 'Changed', value: { historyField: 'Sprint' }, ...(disabled ? { disabled: true } : {}),
    });
    expect(countEnabled(group('r', { conditions: [h('a'), h('b', true)], groups: [group('g', { conditions: [h('c')] })] })))
      .toEqual({ conditions: 2, groups: 2, history: 2 });
  });
});
```

In the existing `countEnabled` test, change the expectation to `{ conditions: 2, groups: 2, history: 0 }`.

Append to `src/JiraUnpacked.Web/src/features/issues/viewState.test.ts`, inside `describe('isModified', …)` (the block holding the "relative and smart values" test):

```ts
  it('does not call a view with history conditions modified when the server returns them in its own key order', () => {
    const stored = view({
      filter: {
        logic: 'And',
        conditions: [
          {
            field: 'history', op: 'ChangeCount',
            value: { during: { unit: 'days', amount: 30, kind: 'inLast' }, count: 3, compare: 'Gte', historyField: 'Sprint' },
            values: null, disabled: null,
          },
          { field: 'history', op: 'Changed', value: { to: ['Done'], historyField: 'status', from: null, during: null }, values: null, disabled: null },
        ],
        groups: null,
        disabled: null,
      } as unknown as SavedView['filter'],
    });
    const built: ListSnapshot = {
      tree: {
        id: 'r', logic: 'And', groups: [],
        conditions: [
          { id: 'a', field: 'history', op: 'ChangeCount', value: { historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 30, unit: 'days' } } },
          { id: 'b', field: 'history', op: 'Changed', value: { historyField: 'status', to: ['Done'] } },
        ],
      },
      sortBy: 'updatedAt',
      sortDescending: true,
      columns: DEFAULT_COLUMNS,
    };

    expect(isModified(stored, built)).toBe(false);
    // Opened and saved again, the view writes the same canonical filter it was built from.
    expect(saveRequest('x', opened(stored)).filter).toEqual(saveRequest('x', built).filter);
  });
```

Append to `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.test.ts` (inside the existing `describe` that holds `names the range and relative operators in words`):

```ts
  it('names the history operators in words', () => {
    expect((['Was', 'Changed', 'ChangeCount'] as const).map(operatorLabel)).toEqual(['was', 'changed', 'number of changes']);
  });
```

and add `maxHistoryConditions: 5,` to both inline `FilterCatalog` literals in that file (after `maxDepth: 3`), and to the `CATALOG` literal in `ColumnChooser.test.tsx`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/historyShapes.test.ts src/features/issues/filterTree.test.ts src/features/issues/viewState.test.ts src/features/issues/filterCatalogHelpers.test.ts`
Expected: FAIL — `historyShapes` does not exist; `operatorLabel('Was')` returns `Was`; history conditions are treated as plain values.

- [ ] **Step 3: Implement**

(a) `src/JiraUnpacked.Web/src/api/issues.ts`:

```ts
export type FilterOperator =
  | 'Eq' | 'Neq' | 'In' | 'NotIn' | 'Lt' | 'Lte' | 'Gt' | 'Gte'
  | 'Contains' | 'NotContains' | 'StartsWith' | 'IsNull' | 'IsNotNull'
  | 'Between' | 'InLast' | 'NotInLast' | 'InPeriod'
  | 'Was' | 'Changed' | 'ChangeCount';

export type FieldType = 'DictName' | 'Decimal' | 'DateTime' | 'Text' | 'ParentRef' | 'StatusCategory' | 'History';
```

After `SmartValue`:

```ts
/** "number of changes" compares the count with one of these. */
export type HistoryCompare = 'Eq' | 'Neq' | 'Gt' | 'Gte' | 'Lt' | 'Lte';

/** A history condition's `during`: the same three shapes the date operators take. */
export type HistoryWindow =
  | { kind: 'inLast'; amount: number; unit: RelativeUnit }
  | { kind: 'inPeriod'; period: PeriodKey; timeZone?: string }
  | { kind: 'between'; from: string | null; to: string | null };

/**
 * The `history` field's value. "was": `values`; "changed": optional `from`, `to`, `by` and
 * `during`; "number of changes": `compare`, `count` and an optional `during`. null in a list is
 * (Empty). `historyField` is the name Jira reports ("status", "Sprint"), matched exactly.
 */
export interface HistoryValue {
  historyField: string;
  values?: (string | null)[];
  from?: (string | null)[];
  to?: (string | null)[];
  by?: string[];
  compare?: HistoryCompare;
  count?: number;
  during?: HistoryWindow;
}
```

```ts
export type ConditionValue = string | number | null | SmartValue | RelativeAmount | PeriodValue | HistoryValue;
```

and in `FilterCatalog`, after `maxDepth: number;`:

```ts
  /** Enabled history conditions a filter may hold — each is a subquery over the changelog. */
  maxHistoryConditions: number;
```

(b) Create `src/JiraUnpacked.Web/src/features/issues/historyShapes.ts`:

```ts
import type { FilterOperator, HistoryCompare, HistoryValue, HistoryWindow, RelativeAmount } from '../../api/issues';
import { MAX_RELATIVE_AMOUNT, readPeriod, readRelativeAmount } from './valueShapes';

/**
 * The `history` pseudo-field's values — what "was", "changed" and "number of changes" take —
 * their limits and defaults, and the reader that turns untrusted JSON (a link, a stored view)
 * into exactly that shape: a fresh object in one key order per operator, or nothing at all.
 */

/** The catalog name of the pseudo-field. */
export const HISTORY_FIELD = 'history';

type HistoryOperator = 'Was' | 'Changed' | 'ChangeCount';

export const takesHistory = (op: FilterOperator): op is HistoryOperator =>
  op === 'Was' || op === 'Changed' || op === 'ChangeCount';

/** The server's limits (IssueFilterTranslator.History). */
export const MAX_HISTORY_FIELD_LENGTH = 100;
export const MAX_HISTORY_VALUE_LENGTH = 255;
export const MAX_CHANGE_COUNT = 10000;

/** In the order the compare select offers them. */
export const HISTORY_COMPARES: readonly { key: HistoryCompare; label: string }[] = [
  { key: 'Eq', label: '=' },
  { key: 'Neq', label: '≠' },
  { key: 'Gt', label: '>' },
  { key: 'Gte', label: '≥' },
  { key: 'Lt', label: '<' },
  { key: 'Lte', label: '≤' },
];

/** "number of changes" starts at "≥ 2", so choosing it already finds the churners. */
export const DEFAULT_CHANGE_COUNT: { compare: HistoryCompare; count: number } = { compare: 'Gte', count: 2 };

/** "during: in the last" starts at 30 days. */
export const DEFAULT_WINDOW_AMOUNT: RelativeAmount = { amount: 30, unit: 'days' };

/** The keys each operator takes; any other key with a value makes a link's value unreadable. */
const HISTORY_KEYS: Record<HistoryOperator, readonly string[]> = {
  Was: ['historyField', 'values'],
  Changed: ['historyField', 'from', 'to', 'by', 'during'],
  ChangeCount: ['historyField', 'compare', 'count', 'during'],
};

const isRecord = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

const BAD = Symbol('unreadable');

/**
 * A value list from untrusted JSON. Absent or null → undefined. Names, and (Empty) as null — ""
 * too, as the server reads it — where allowed; de-duplicated. Anything else → BAD.
 */
function readList(x: unknown, allowEmpty: boolean): (string | null)[] | undefined | typeof BAD {
  if (x === undefined || x === null) return undefined;
  if (!Array.isArray(x)) return BAD;
  const out: (string | null)[] = [];
  for (const raw of x) {
    const v: unknown = raw === '' ? null : raw;
    if (v === null) {
      if (!allowEmpty) return BAD;
    } else if (typeof v !== 'string') {
      return BAD;
    }
    const item = v as string | null;
    if (!out.includes(item)) out.push(item);
  }
  return out;
}

/** A `during` from untrusted JSON, in canonical key order — or undefined when it is none of the three shapes. */
export function readWindow(v: unknown): HistoryWindow | undefined {
  if (!isRecord(v)) return undefined;
  if (v.kind === 'inLast') {
    const rel = readRelativeAmount(v);
    return rel ? { kind: 'inLast', amount: rel.amount, unit: rel.unit } : undefined;
  }
  if (v.kind === 'inPeriod') {
    const period = readPeriod(v);
    return period ? { kind: 'inPeriod', ...period } : undefined;
  }
  if (v.kind === 'between') {
    const bound = (x: unknown): string | null | typeof BAD =>
      x === undefined || x === null || x === '' ? null : typeof x === 'string' ? x : BAD;
    const from = bound(v.from);
    const to = bound(v.to);
    return from === BAD || to === BAD ? undefined : { kind: 'between', from, to };
  }
  return undefined;
}

/** In the last: a whole amount 1–3650. Between: a bound, and not reversed. In period: always (the reader admits only known periods). */
export function isCompleteWindow(w: HistoryWindow): boolean {
  if (w.kind === 'inLast') return Number.isInteger(w.amount) && w.amount >= 1 && w.amount <= MAX_RELATIVE_AMOUNT;
  if (w.kind === 'between') {
    if (w.from === null && w.to === null) return false;
    return w.from === null || w.to === null || w.from <= w.to;
  }
  return true;
}

/**
 * The value as the server takes it and a saved view stores it: only the operator's keys, in one
 * order, optional lists left out when empty, every array and the window copied.
 */
export function canonicalHistoryValue(op: FilterOperator, v: HistoryValue): HistoryValue {
  const out: HistoryValue = { historyField: v.historyField };
  if (op === 'Was') {
    out.values = [...(v.values ?? [])];
    return out;
  }
  if (op === 'Changed') {
    if (v.from?.length) out.from = [...v.from];
    if (v.to?.length) out.to = [...v.to];
    if (v.by?.length) out.by = [...v.by];
  } else {
    if (v.compare !== undefined) out.compare = v.compare;
    if (v.count !== undefined) out.count = v.count;
  }
  if (v.during) out.during = { ...v.during };
  return out;
}

/** What each operator starts with: "was" no values yet, "changed" nothing, "number of changes" ≥ 2. */
export function freshHistoryValue(op: FilterOperator, historyField: string): HistoryValue {
  if (op === 'Was') return { historyField, values: [] };
  if (op === 'ChangeCount') return { historyField, ...DEFAULT_CHANGE_COUNT };
  return { historyField };
}

/**
 * A history value from untrusted JSON, read whole or not at all: a part it cannot read (a key
 * this operator does not take, a list that is not one, a window of no known kind, an unknown
 * compare, a count that is not a number) leaves it unreadable — the row stays on screen,
 * unfinished, rather than quietly meaning more than the link said.
 */
export function readHistoryValue(op: FilterOperator, raw: unknown): HistoryValue | undefined {
  if (!takesHistory(op) || !isRecord(raw) || typeof raw.historyField !== 'string') return undefined;
  const keys = HISTORY_KEYS[op];
  if (Object.entries(raw).some(([k, x]) => !keys.includes(k) && x !== null && x !== undefined)) return undefined;

  const v: HistoryValue = { historyField: raw.historyField };
  if (op === 'Was') {
    const values = readList(raw.values, true);
    if (values === BAD) return undefined;
    v.values = values ?? [];
    return canonicalHistoryValue(op, v);
  }

  if (raw.during !== undefined && raw.during !== null) {
    const during = readWindow(raw.during);
    if (!during) return undefined;
    v.during = during;
  }

  if (op === 'Changed') {
    const from = readList(raw.from, true);
    const to = readList(raw.to, true);
    const by = readList(raw.by, false);
    if (from === BAD || to === BAD || by === BAD) return undefined;
    if (from) v.from = from;
    if (to) v.to = to;
    if (by) v.by = by.filter((x): x is string => x !== null);
    return canonicalHistoryValue(op, v);
  }

  if (raw.compare !== undefined && raw.compare !== null) {
    const compare = HISTORY_COMPARES.find((c) => c.key === raw.compare);
    if (!compare) return undefined;
    v.compare = compare.key;
  }
  if (raw.count !== undefined && raw.count !== null) {
    if (typeof raw.count !== 'number' || !Number.isFinite(raw.count)) return undefined;
    v.count = raw.count;
  }
  return canonicalHistoryValue(op, v);
}

const fits = (list: readonly (string | null)[] | undefined): boolean =>
  (list ?? []).every((x) => x === null || x.length <= MAX_HISTORY_VALUE_LENGTH);

/**
 * Finished enough to send: a field name (non-blank, ≤ 100); "was" at least one value; "number of
 * changes" a compare and a whole count 0–10000; any window finished; no value over 255 characters.
 */
export function isCompleteHistoryValue(op: FilterOperator, v: HistoryValue | undefined): boolean {
  if (!v || v.historyField.trim() === '' || v.historyField.length > MAX_HISTORY_FIELD_LENGTH) return false;
  if (v.during && !isCompleteWindow(v.during)) return false;
  if (op === 'Was') return (v.values?.length ?? 0) > 0 && fits(v.values);
  if (op === 'Changed') return fits(v.from) && fits(v.to) && fits(v.by);
  if (op === 'ChangeCount') {
    return v.compare !== undefined && v.count !== undefined && Number.isInteger(v.count)
      && v.count >= 0 && v.count <= MAX_CHANGE_COUNT;
  }
  return false;
}
```

(c) `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.ts` — append to `OPERATOR_LABELS`:

```ts
  Was: 'was',
  Changed: 'changed',
  ChangeCount: 'number of changes',
```

(d) `src/JiraUnpacked.Web/src/features/issues/filterTree.ts`:

- Imports — add:

```ts
import {
  HISTORY_FIELD, freshHistoryValue, isCompleteHistoryValue, readHistoryValue, takesHistory,
} from './historyShapes';
```

- `TreeCounts` becomes:

```ts
export interface TreeCounts {
  conditions: number;
  groups: number;
  /** Enabled conditions on the history pseudo-field: capped on their own (catalog.maxHistoryConditions). */
  history: number;
}
```

- `copyValue` becomes a deep copy (history values hold arrays and a window):

```ts
/**
 * Object values are copied, never shared: two rows — or the editor and the wire — must not alias
 * one object, nor the arrays inside a history value. Values are plain JSON, so a JSON round trip
 * is a faithful deep copy.
 */
function copyValue<T>(v: T): T {
  return (typeof v === 'object' && v !== null ? JSON.parse(JSON.stringify(v)) : v) as T;
}
```

- `isCompleteCondition` — first line of the body:

```ts
  if (takesHistory(c.op)) return isCompleteHistoryValue(c.op, readHistoryValue(c.op, c.value));
```

- `toWireCondition` — before its final `return`:

```ts
  if (takesHistory(c.op)) {
    // Canonical and freshly built: the key order a saved view compares on, no arrays shared.
    const value = readHistoryValue(c.op, c.value);
    return value ? { field: c.field, op: c.op, value } : { field: c.field, op: c.op };
  }
```

- `countEnabled` becomes:

```ts
export function countEnabled(g: EditorGroup): TreeCounts {
  if (g.disabled) return { conditions: 0, groups: 0, history: 0 };
  const on = g.conditions.filter((c) => !c.disabled);
  let conditions = on.length;
  let history = on.filter((c) => c.field === HISTORY_FIELD).length;
  let groups = 1;
  for (const child of g.groups) {
    const sub = countEnabled(child);
    conditions += sub.conditions;
    groups += sub.groups;
    history += sub.history;
  }
  return { conditions, groups, history };
}
```

- `freshCondition` — first line of the body:

```ts
  if (takesHistory(op)) return { field, op, value: freshHistoryValue(op, '') };
```

- `switchOperator` — right after `const { field, op } = c;`:

```ts
  if (takesHistory(nextOp)) {
    // The field carries over between history operators; a window means the same under
    // "changed" and "number of changes".
    const prev = takesHistory(op) ? readHistoryValue(op, c.value) : undefined;
    const next = freshHistoryValue(nextOp, prev?.historyField ?? '');
    if (prev?.during && nextOp !== 'Was') next.during = { ...prev.during };
    return { field, op: nextOp, value: next };
  }
```

- `readValue` — first line of the body:

```ts
  if (takesHistory(op)) return readHistoryValue(op, raw);
```

(e) `src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts` — add the history field last in `fields`, and the cap:

```ts
    { name: 'history', type: 'History', operators: ['Was', 'Changed', 'ChangeCount'] },
```

```ts
  maxDepth: 3,
  maxHistoryConditions: 5,
```

- [ ] **Step 4: Run the tests and the typecheck**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npx vitest run src/features/issues src/api`
Expected: PASS — the new tests, and every existing editor/view test unchanged (the fixture's new field is last, so nothing that reads the first fields moves).

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): history values in the filter model — reader, completeness, URL and saved views" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Shared date controls, the During control, and the history field select (SPA)

**Files:**
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts` (fetchers)
- Modify: `src/JiraUnpacked.Web/src/api/queryKeys.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/DateControls.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` (whole file shown — the date branches use DateControls; behaviour unchanged)
- Create: `src/JiraUnpacked.Web/src/features/issues/DuringControl.tsx`, `DuringControl.test.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/HistoryFieldSelect.tsx`, `HistoryFieldSelect.test.tsx`

**Interfaces:**
- Consumes: Task 4's endpoints; Task 6's `HistoryWindow`, `DEFAULT_WINDOW_AMOUNT`, `MAX_HISTORY_FIELD_LENGTH`.
- Produces (`api/issues.ts`): `interface HistoryFieldOption { name: string; count: number }`; `getHistoryFields(projectId): Promise<{ fields: HistoryFieldOption[] }>`; `type HistoryValueKind = 'value' | 'author'`; `interface HistoryValueList { values: FilterValueOption[]; emptyCount: number }`; `getHistoryValues(projectId, field, kind): Promise<HistoryValueList>`.
- Produces (`queryKeys`): `historyFields(projectId)`, `historyValues(projectId, field, kind)`.
- Produces (`DateControls.tsx`): `type DatePart = 'Amount' | 'Unit' | 'Period' | 'From' | 'To'`; `type PartNamer = (part: DatePart) => { label: string; id: string }`; `conditionParts(label): PartNamer`; `AmountInputs({ parts, value: RelativeAmount | undefined, onChange })`; `PeriodSelect({ parts, value: PeriodValue | undefined, onChange })`; `RangeInputs({ parts, from: ListValue, to: ListValue, numeric: boolean, onChange: (from, to) => void })`.
- Produces: `DuringControl({ label: string; value: HistoryWindow | undefined; onChange: (next: HistoryWindow | undefined) => void })` — names `During for condition N` and `During: amount|unit|period|from|to for condition N`; `HistoryFieldSelect({ projectId, label, value: string, onChange: (next: string) => void })` — name `History field for condition N`. Task 8 composes both.

- [ ] **Step 1: Write the failing tests**

Create `src/JiraUnpacked.Web/src/features/issues/DuringControl.test.tsx`:

```tsx
import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { HistoryWindow } from '../../api/issues';
import { DuringControl } from './DuringControl';

function renderDuring(initial?: HistoryWindow) {
  const seen: (HistoryWindow | undefined)[] = [];
  function Harness() {
    const [value, setValue] = useState(initial);
    return <DuringControl label="1" value={value} onChange={(next) => { seen.push(next); setValue(next); }} />;
  }
  render(<Harness />);
  return { last: () => seen.at(-1) };
}

const inZone = (timeZone: string) => {
  const real = Intl.DateTimeFormat().resolvedOptions();
  vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ ...real, timeZone });
};

describe('DuringControl', () => {
  afterEach(() => vi.restoreAllMocks());

  it('offers any time, in the last, in period and between — starting at any time', () => {
    renderDuring();
    const select = screen.getByLabelText('During for condition 1');
    expect(select).toHaveValue('');
    expect(within(select).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['any time', 'in the last', 'in period', 'between']);
  });

  it('starts "in the last" at 30 days, and edits the amount and the unit', async () => {
    const user = userEvent.setup();
    const during = renderDuring();
    await user.selectOptions(screen.getByLabelText('During for condition 1'), 'inLast');
    expect(during.last()).toEqual({ kind: 'inLast', amount: 30, unit: 'days' });

    await user.selectOptions(screen.getByLabelText('During: unit for condition 1'), 'weeks');
    expect(during.last()).toEqual({ kind: 'inLast', amount: 30, unit: 'weeks' });

    await user.clear(screen.getByLabelText('During: amount for condition 1'));
    expect(during.last()).toEqual({ kind: 'inLast', amount: 0, unit: 'weeks' });   // unfinished: never sent
  });

  it('captures the browser’s zone with "in period" and names it', async () => {
    inZone('Europe/Warsaw');
    const user = userEvent.setup();
    const during = renderDuring();
    await user.selectOptions(screen.getByLabelText('During for condition 1'), 'inPeriod');
    expect(during.last()).toEqual({ kind: 'inPeriod', period: 'thisWeek', timeZone: 'Europe/Warsaw' });

    await user.selectOptions(screen.getByLabelText('During: period for condition 1'), 'lastMonth');
    expect(during.last()).toEqual({ kind: 'inPeriod', period: 'lastMonth', timeZone: 'Europe/Warsaw' });
    expect(screen.getByText('Europe/Warsaw')).toBeInTheDocument();
  });

  it('takes two dates with either end open, and goes back to any time', async () => {
    const user = userEvent.setup();
    const during = renderDuring();
    await user.selectOptions(screen.getByLabelText('During for condition 1'), 'between');
    expect(during.last()).toEqual({ kind: 'between', from: null, to: null });

    fireEvent.change(screen.getByLabelText('During: to for condition 1'), { target: { value: '2026-09-30' } });
    expect(during.last()).toEqual({ kind: 'between', from: null, to: '2026-09-30' });

    await user.selectOptions(screen.getByLabelText('During for condition 1'), '');
    expect(during.last()).toBeUndefined();
  });
});
```

Create `src/JiraUnpacked.Web/src/features/issues/HistoryFieldSelect.test.tsx`:

```tsx
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { HistoryFieldSelect } from './HistoryFieldSelect';

function renderSelect(initial = '') {
  const seen: string[] = [];
  function Harness() {
    const [value, setValue] = useState(initial);
    return <HistoryFieldSelect projectId="p1" label="1" value={value} onChange={(next) => { seen.push(next); setValue(next); }} />;
  }
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Harness />
    </QueryClientProvider>,
  );
  return { last: () => seen.at(-1) };
}

const fieldList = () => server.use(http.get('/api/projects/p1/history-fields', () =>
  HttpResponse.json({ fields: [{ name: 'Sprint', count: 42 }, { name: 'status', count: 7 }] })));

const waitForSelect = () =>
  waitFor(() => expect(screen.getByLabelText('History field for condition 1').tagName).toBe('SELECT'));

describe('HistoryFieldSelect', () => {
  it('lists this project’s history fields with their counts, most-changed first', async () => {
    fieldList();
    const user = userEvent.setup();
    const select = renderSelect();
    await waitForSelect();

    expect(within(screen.getByLabelText('History field for condition 1')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['— choose —', 'Sprint (42)', 'status (7)']);
    await user.selectOptions(screen.getByLabelText('History field for condition 1'), 'Sprint');
    expect(select.last()).toBe('Sprint');
  });

  it('keeps a field from a link that this project’s changelog does not have', async () => {
    fieldList();
    renderSelect('Story Points');
    await waitForSelect();

    expect(screen.getByLabelText('History field for condition 1')).toHaveValue('Story Points');
    expect(screen.getByRole('option', { name: 'Story Points (0)' })).toBeInTheDocument();
  });

  it('falls back to typing the name when the list fails', async () => {
    server.use(http.get('/api/projects/p1/history-fields', () => new HttpResponse(null, { status: 500 })));
    const user = userEvent.setup();
    const select = renderSelect();

    expect(await screen.findByText(/type the name as Jira reports it/i)).toBeInTheDocument();
    await user.type(screen.getByLabelText('History field for condition 1'), 'Sprint');
    expect(select.last()).toBe('Sprint');
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/DuringControl.test.tsx src/features/issues/HistoryFieldSelect.test.tsx`
Expected: FAIL — `DuringControl` and `HistoryFieldSelect` do not exist.

- [ ] **Step 3: Implement**

(a) `src/JiraUnpacked.Web/src/api/issues.ts` — after `getFilterValues`:

```ts
export interface HistoryFieldOption {
  name: string;
  /** Changelog rows this project has for the field. */
  count: number;
}

/** The changelog fields this project has history for, most-changed first (at most 200). */
export const getHistoryFields = (projectId: string) =>
  apiFetch<{ fields: HistoryFieldOption[] }>(`/api/projects/${projectId}/history-fields`);

/** `value`: the From/To values, Jira's ", "-joined lists split into elements. `author`: who made the changes. */
export type HistoryValueKind = 'value' | 'author';

export interface HistoryValueList {
  values: FilterValueOption[];
  /** Changelog sides (value) or rows (author) with nothing in them: the count behind (Empty). */
  emptyCount: number;
}

/** One history field's values or authors in this project, by name (at most 1000). An unknown field lists nothing. */
export const getHistoryValues = (projectId: string, field: string, kind: HistoryValueKind) =>
  apiFetch<HistoryValueList>(
    `/api/projects/${projectId}/history-values?field=${encodeURIComponent(field)}&kind=${kind}`);
```

(b) `src/JiraUnpacked.Web/src/api/queryKeys.ts` — after `filterValues`:

```ts
  historyFields: (projectId: string) => ['history-fields', projectId] as const,
  historyValues: (projectId: string, field: string, kind: string) =>
    ['history-values', projectId, field, kind] as const,
```

(c) Create `src/JiraUnpacked.Web/src/features/issues/DateControls.tsx`:

```tsx
import type { ListValue, PeriodKey, PeriodValue, RelativeAmount, RelativeUnit } from '../../api/issues';
import { Field, Select } from '../../ui';
import { INPUT_CLASS } from './styles';
import {
  DEFAULT_RELATIVE, MAX_RELATIVE_AMOUNT, PERIODS, RELATIVE_UNITS, browserTimeZone, isScalar,
} from './valueShapes';

/**
 * The controls the date operators and history windows share: an amount and a unit ("in the
 * last"), a calendar period with its zone ("in period"), and two bounds ("between"). Each part's
 * accessible name and id come from a PartNamer, so the same control reads "Amount for condition 1"
 * in a date row and "During: amount for condition 1" in a history window.
 */
export type DatePart = 'Amount' | 'Unit' | 'Period' | 'From' | 'To';
export type PartNamer = (part: DatePart) => { label: string; id: string };

/** A condition's own parts: "Amount for condition 1", id "condition-1-amount". */
export const conditionParts = (label: string): PartNamer => (part) => ({
  label: `${part} for condition ${label}`,
  id: `condition-${label}-${part.toLowerCase()}`,
});

interface AmountInputsProps {
  parts: PartNamer;
  /** Undefined = nothing entered yet. */
  value: RelativeAmount | undefined;
  onChange: (next: RelativeAmount) => void;
}

/** "[14] [days]". An amount of 0 is "not entered yet": the input shows blank and the row is unfinished. */
export function AmountInputs({ parts, value, onChange }: AmountInputsProps) {
  const rel: RelativeAmount = value ?? { amount: 0, unit: DEFAULT_RELATIVE.unit };
  const amount = parts('Amount');
  const unit = parts('Unit');
  return (
    <>
      <Field label={amount.label} id={amount.id}>
        <input
          type="number"
          min={1}
          max={MAX_RELATIVE_AMOUNT}
          step={1}
          className={INPUT_CLASS}
          value={rel.amount >= 1 ? rel.amount : ''}
          onChange={(e) => onChange({
            amount: e.target.value === '' ? 0 : Math.trunc(Number(e.target.value)),
            unit: rel.unit,
          })}
        />
      </Field>
      <Field label={unit.label} id={unit.id}>
        <Select value={rel.unit} onChange={(e) => onChange({ amount: rel.amount, unit: e.target.value as RelativeUnit })}>
          {RELATIVE_UNITS.map((u) => (
            <option key={u} value={u}>{u}</option>
          ))}
        </Select>
      </Field>
    </>
  );
}

interface PeriodSelectProps {
  parts: PartNamer;
  /** Undefined = no period yet (a link that had none, or an unknown one). */
  value: PeriodValue | undefined;
  onChange: (next: PeriodValue) => void;
}

/**
 * The ten periods in reading order. The zone captured with the value stays with it; one that
 * never had a zone (a link from elsewhere) means UTC on the server, and keeps meaning it. With no
 * period yet, the browser's zone is the one a pick will capture, so that is the one named.
 */
export function PeriodSelect({ parts, value, onChange }: PeriodSelectProps) {
  const zone = value ? value.timeZone : browserTimeZone();
  const period = parts('Period');
  const pick = (key: PeriodKey) => {
    const next: PeriodValue = { period: key };
    if (zone) next.timeZone = zone;
    onChange(next);
  };
  return (
    <Field label={period.label} id={period.id} hint={zone ?? 'UTC'}>
      <Select value={value?.period ?? ''} onChange={(e) => pick(e.target.value as PeriodKey)}>
        {!value && <option value="">— choose —</option>}
        {PERIODS.map((p) => (
          <option key={p.key} value={p.key}>{p.label}</option>
        ))}
      </Select>
    </Field>
  );
}

interface RangeInputsProps {
  parts: PartNamer;
  from: ListValue;
  to: ListValue;
  /** Number inputs (story points) rather than date inputs. */
  numeric: boolean;
  onChange: (from: ListValue, to: ListValue) => void;
}

/** Two bounds, either left blank for an open end (null). */
export function RangeInputs({ parts, from, to, numeric, onChange }: RangeInputsProps) {
  const shown = (v: ListValue) => (isScalar(v) ? String(v) : '');
  const read = (raw: string): ListValue => (raw === '' ? null : numeric ? Number(raw) : raw);
  const bound = (part: 'From' | 'To', v: ListValue, set: (raw: string) => void) => {
    const p = parts(part);
    return (
      <Field label={p.label} id={p.id}>
        <input
          type={numeric ? 'number' : 'date'}
          step={numeric ? 'any' : undefined}
          className={INPUT_CLASS}
          value={shown(v)}
          onChange={(e) => set(e.target.value)}
        />
      </Field>
    );
  };
  return (
    <>
      {bound('From', from, (raw) => onChange(read(raw), to))}
      {bound('To', to, (raw) => onChange(from, read(raw)))}
    </>
  );
}
```

(d) Replace `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` with (only the imports and the three date branches differ from today's file; everything else is carried over verbatim):

```tsx
import type { ChangeEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { FilterCatalog, FilterCondition, FilterOperator } from '../../api/issues';
import { getFilterValues } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Field, Select } from '../../ui';
import { ChipInput } from './ChipInput';
import { AmountInputs, PeriodSelect, RangeInputs, conditionParts } from './DateControls';
import {
  fieldNames, isDictionaryField, operatorLabel, operatorsFor, resetOperatorIfDisallowed, typeFor,
} from './filterCatalogHelpers';
import {
  freshCondition, switchOperator, takesAmount, takesList, takesNoValue, takesPeriod, takesRange,
} from './filterTree';
import { INPUT_CLASS } from './styles';
import { browserTimeZone, isRelativeAmount, isScalar, isSmartValue, readPeriod } from './valueShapes';
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
    onChange(freshCondition(nextField, nextOp, browserTimeZone()));
  };

  // What carries over between operators, and what each one starts with, lives in switchOperator.
  const handleOperatorChange = (event: ChangeEvent<HTMLSelectElement>) =>
    onChange(switchOperator(condition, event.target.value as FilterOperator, browserTimeZone()));

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

    if (takesRange(op)) {
      // Two bounds, either left blank for an open end. Dates for the date fields, numbers for points.
      const [from = null, to = null] = condition.values ?? [];
      return (
        <RangeInputs
          parts={conditionParts(label)}
          from={from}
          to={to}
          numeric={fieldType === 'Decimal'}
          onChange={(nextFrom, nextTo) => onChange({ field, op, values: [nextFrom, nextTo] })}
        />
      );
    }

    if (takesAmount(op)) {
      return (
        <>
          <AmountInputs
            parts={conditionParts(label)}
            value={isRelativeAmount(condition.value) ? condition.value : undefined}
            onChange={(next) => onChange({ field, op, value: next })}
          />
          {/* "more than [14] [days] ago": the operator reads "more than … ago", the row ends the sentence. */}
          {op === 'NotInLast' && <span className="pb-2 text-sm text-[var(--ju-text)]">ago</span>}
        </>
      );
    }

    if (takesPeriod(op)) {
      return (
        <PeriodSelect
          parts={conditionParts(label)}
          value={readPeriod(condition.value)}
          onChange={(next) => onChange({ field, op, value: next })}
        />
      );
    }

    if (takesList(op)) {
      const values = condition.values ?? [];
      if (picked) {
        return (
          <MultiValuePicker
            label={`Values for condition ${label}`}
            options={picked.values}
            emptyCount={picked.emptyCount}
            smart={picked.smart}
            selected={values}
            max={catalog.maxInValues}
            onChange={(next) => onChange({ field, op, values: next })}
          />
        );
      }
      const isKey = field === 'jiraKey';
      // Typing cannot produce (Empty) or a smart value, but one that arrived in a link is kept,
      // not silently lost.
      const kept = values.filter((v) => v === null || isSmartValue(v));
      return (
        <ChipInput
          key={field}
          id={valueId}
          label={valueLabel}
          values={values.filter(isScalar).map(String)}
          max={catalog.maxInValues - kept.length}
          splitOn={isKey ? JIRA_KEY_SEPARATORS : undefined}
          normalize={isKey ? toUpper : undefined}
          onChange={(next) => onChange({ field, op, values: [...kept, ...next] })}
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
            smart={picked.smart}
            smartValue={isSmartValue(condition.value) ? condition.value.smart : undefined}
            onPick={(name) => onChange({ field, op, value: name })}
            onPickSmart={(key) => onChange({ field, op, value: { smart: key } })}
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
            value={isScalar(condition.value) ? condition.value : ''}
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

(e) Create `src/JiraUnpacked.Web/src/features/issues/DuringControl.tsx`:

```tsx
import type { HistoryWindow } from '../../api/issues';
import { Field, Select } from '../../ui';
import { AmountInputs, PeriodSelect, RangeInputs, type PartNamer } from './DateControls';
import { DEFAULT_WINDOW_AMOUNT } from './historyShapes';
import { DEFAULT_PERIOD, browserTimeZone } from './valueShapes';

/** In reading order; '' is "any time" — no window at all. */
const KINDS: readonly { key: '' | HistoryWindow['kind']; label: string }[] = [
  { key: '', label: 'any time' },
  { key: 'inLast', label: 'in the last' },
  { key: 'inPeriod', label: 'in period' },
  { key: 'between', label: 'between' },
];

/** The window's own parts: "During: amount for condition 1", id "condition-1-during-amount". */
const duringParts = (label: string): PartNamer => (part) => ({
  label: `During: ${part.toLowerCase()} for condition ${label}`,
  id: `condition-${label}-during-${part.toLowerCase()}`,
});

interface DuringControlProps {
  /** Position in the tree, e.g. "1" or "2.1" — part of every control's accessible name. */
  label: string;
  /** Undefined = any time. */
  value: HistoryWindow | undefined;
  onChange: (next: HistoryWindow | undefined) => void;
}

/**
 * When the change happened: any time, in the last N units, in a calendar period (in the zone
 * captured when it was chosen) or between two dates — the date operators' own controls, so the
 * two read and behave alike.
 */
export function DuringControl({ label, value, onChange }: DuringControlProps) {
  const parts = duringParts(label);
  const choose = (kind: string) => {
    if (kind === 'inLast') onChange({ kind: 'inLast', ...DEFAULT_WINDOW_AMOUNT });
    else if (kind === 'inPeriod') onChange({ kind: 'inPeriod', period: DEFAULT_PERIOD, timeZone: browserTimeZone() });
    else if (kind === 'between') onChange({ kind: 'between', from: null, to: null });
    else onChange(undefined);
  };
  return (
    <>
      <Field label={`During for condition ${label}`} id={`condition-${label}-during`}>
        <Select value={value?.kind ?? ''} onChange={(e) => choose(e.target.value)}>
          {KINDS.map((k) => (
            <option key={k.key} value={k.key}>{k.label}</option>
          ))}
        </Select>
      </Field>
      {value?.kind === 'inLast' && (
        <AmountInputs
          parts={parts}
          value={{ amount: value.amount, unit: value.unit }}
          onChange={(next) => onChange({ kind: 'inLast', ...next })}
        />
      )}
      {value?.kind === 'inPeriod' && (
        <PeriodSelect
          parts={parts}
          value={value.timeZone ? { period: value.period, timeZone: value.timeZone } : { period: value.period }}
          onChange={(next) => onChange({ kind: 'inPeriod', ...next })}
        />
      )}
      {value?.kind === 'between' && (
        <RangeInputs
          parts={parts}
          from={value.from}
          to={value.to}
          numeric={false}
          onChange={(from, to) => onChange({
            kind: 'between',
            from: typeof from === 'string' ? from : null,
            to: typeof to === 'string' ? to : null,
          })}
        />
      )}
    </>
  );
}
```

(f) Create `src/JiraUnpacked.Web/src/features/issues/HistoryFieldSelect.tsx`:

```tsx
import { useQuery } from '@tanstack/react-query';
import { getHistoryFields } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Field, Select } from '../../ui';
import { MAX_HISTORY_FIELD_LENGTH } from './historyShapes';
import { INPUT_CLASS } from './styles';

interface HistoryFieldSelectProps {
  projectId: string;
  /** Position in the tree, e.g. "1" or "2.1" — part of the accessible name. */
  label: string;
  /** The field's name as Jira reports it; '' = none chosen yet. */
  value: string;
  onChange: (next: string) => void;
}

/**
 * Which field's history: the fields this project's changelog has, most-changed first, each with
 * its count. The list is an affordance, not a gate — while it loads, or if it fails, the name is
 * typed, exactly as Jira reports it.
 */
export function HistoryFieldSelect({ projectId, label, value, onChange }: HistoryFieldSelectProps) {
  const fields = useQuery({
    queryKey: queryKeys.historyFields(projectId),
    queryFn: () => getHistoryFields(projectId),
    // A project's changelog changes only when a sync runs.
    staleTime: 5 * 60 * 1000,
  });
  const id = `condition-${label}-history-field`;
  const name = `History field for condition ${label}`;

  if (!fields.data) {
    return (
      <Field
        label={name}
        id={id}
        hint={fields.isError ? 'Field list unavailable — type the name as Jira reports it' : undefined}
      >
        <input
          type="text"
          className={INPUT_CLASS}
          maxLength={MAX_HISTORY_FIELD_LENGTH}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
      </Field>
    );
  }

  const options = fields.data.fields;
  // A field from a link that this project's changelog does not have is shown as what it is.
  const stale = value !== '' && !options.some((o) => o.name === value);
  return (
    <Field label={name} id={id}>
      <Select value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">— choose —</option>
        {stale && <option value={value}>{`${value} (0)`}</option>}
        {options.map((o) => (
          <option key={o.name} value={o.name}>{`${o.name} (${o.count})`}</option>
        ))}
      </Select>
    </Field>
  );
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npx vitest run src/features/issues`
Expected: PASS — the two new test files, and `ConditionRow.test.tsx`'s existing range / relative-date / period tests unchanged (they pin the labels and ids `conditionParts` reproduces).

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): shared date controls, the During window control and the history field select" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: The history row — controls, the field entry, and the cap (SPA)

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/HistoryConditionControls.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx` (`MultiValuePicker.allowEmpty`)
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/GroupEditor.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`, `GroupEditor.test.tsx`, `ValuePicker.test.tsx`

**Interfaces:**
- Consumes: Task 6 (`HISTORY_FIELD`, `takesHistory`, `readHistoryValue`, `canonicalHistoryValue`, `freshHistoryValue`, `HISTORY_COMPARES`, `MAX_CHANGE_COUNT`, `TreeCounts.history`); Task 7 (`DuringControl`, `HistoryFieldSelect`, `getHistoryValues`, `queryKeys.historyValues`).
- Produces: `HistoryConditionControls({ projectId, label, op, value: HistoryValue | undefined, maxInValues, onChange: (next: HistoryValue) => void })`; `MultiValuePicker` prop `allowEmpty?: boolean` (default `true`); `ConditionRow` prop `historyAtCap?: boolean`.

- [ ] **Step 1: Write the failing tests**

`src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx` — give `renderRow` a second parameter and pass it through:

```tsx
function renderRow(initial: FilterCondition, extra: { historyAtCap?: boolean } = {}) {
```

```tsx
      <ConditionRow
        projectId="p1"
        catalog={FILTER_CATALOG}
        condition={condition}
        label="1"
        historyAtCap={extra.historyAtCap}
        onChange={(next) => { seen.push(next); setCondition(next); }}
      />
```

and append:

```tsx
describe('ConditionRow — history', () => {
  function mockHistory() {
    server.use(
      http.get('/api/projects/p1/history-fields', () =>
        HttpResponse.json({ fields: [{ name: 'Sprint', count: 9 }, { name: 'status', count: 4 }] })),
      http.get('/api/projects/p1/history-values', ({ request }) =>
        HttpResponse.json(new URL(request.url).searchParams.get('kind') === 'author'
          ? { values: [{ name: 'Alice', count: 3 }], emptyCount: 1 }
          : { values: [{ name: 'PLAT 101', count: 5 }, { name: 'PLAT 102', count: 2 }], emptyCount: 4 })),
    );
  }

  it('lists "History of a field…" last, and starts it at "was" with nothing chosen', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'Eq', value: '' });

    const fields = within(screen.getByLabelText('Field for condition 1')).getAllByRole('option');
    expect(fields.at(-1)).toHaveTextContent('History of a field…');
    await user.selectOptions(screen.getByLabelText('Field for condition 1'), 'history');

    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: '', values: [] } });
    expect(within(screen.getByLabelText('Operator for condition 1')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['was', 'changed', 'number of changes']);
  });

  it('"was": picks the field, then values — (Empty) included', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({ field: 'history', op: 'Was', value: { historyField: '', values: [] } });

    await waitFor(() => expect(screen.getByLabelText('History field for condition 1').tagName).toBe('SELECT'));
    await user.selectOptions(screen.getByLabelText('History field for condition 1'), 'Sprint');
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: [] } });

    const values = await screen.findByRole('group', { name: 'Values for condition 1' });
    await user.click(within(values).getByRole('checkbox', { name: /^PLAT 101/ }));
    await user.click(within(values).getByRole('checkbox', { name: /^\(Empty\)/ }));
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101', null] } });
  });

  it('"changed": from, to and by are optional; authors offer no (Empty); unticking drops the key', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({ field: 'history', op: 'Changed', value: { historyField: 'status' } });

    expect(await screen.findByRole('group', { name: 'From values for condition 1' })).toBeInTheDocument();
    expect(await screen.findByRole('group', { name: 'To values for condition 1' })).toBeInTheDocument();
    const by = await screen.findByRole('group', { name: 'Authors for condition 1' });
    expect(within(by).queryByRole('checkbox', { name: /^\(Empty\)/ })).toBeNull();

    await user.click(within(by).getByRole('checkbox', { name: /^Alice/ }));
    expect(row.last()).toEqual({ field: 'history', op: 'Changed', value: { historyField: 'status', by: ['Alice'] } });
    await user.click(within(by).getByRole('checkbox', { name: /^Alice/ }));
    expect(row.last()).toEqual({ field: 'history', op: 'Changed', value: { historyField: 'status' } });
  });

  it('"number of changes": starts at ≥ 2, takes a count and a window', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101'] } });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'ChangeCount');
    expect(row.last()).toEqual({ field: 'history', op: 'ChangeCount', value: { historyField: 'Sprint', compare: 'Gte', count: 2 } });
    expect(within(screen.getByLabelText('Compare for condition 1')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['=', '≠', '>', '≥', '<', '≤']);

    await user.clear(screen.getByLabelText('Count for condition 1'));
    expect(row.last()).toEqual({ field: 'history', op: 'ChangeCount', value: { historyField: 'Sprint', compare: 'Gte' } });
    await user.type(screen.getByLabelText('Count for condition 1'), '3');
    await user.selectOptions(screen.getByLabelText('During for condition 1'), 'inLast');
    expect(row.last()).toEqual({
      field: 'history', op: 'ChangeCount',
      value: { historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 30, unit: 'days' } },
    });
  });

  it('keeps the field, and the window, when switching between history operators', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({
      field: 'history', op: 'ChangeCount',
      value: { historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 7, unit: 'days' } },
    });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Changed');
    expect(row.last()).toEqual({
      field: 'history', op: 'Changed', value: { historyField: 'Sprint', during: { kind: 'inLast', amount: 7, unit: 'days' } },
    });
    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Was');
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: [] } });
  });

  it('disables "History of a field…" at the cap, saying so', () => {
    renderRow({ field: 'status', op: 'Eq', value: '' }, { historyAtCap: true });

    const option = within(screen.getByLabelText('Field for condition 1')).getByRole('option', { name: /History of a field/ });
    expect(option).toBeDisabled();
    expect(option).toHaveTextContent('History of a field… (limit: 5)');
  });
});
```

Append to `src/JiraUnpacked.Web/src/features/issues/GroupEditor.test.tsx` (inside `describe('GroupEditor', …)`):

```tsx
  it('counts history conditions against their own cap', () => {
    server.use(
      http.get('/api/projects/p1/history-fields', () => HttpResponse.json({ fields: [] })),
      http.get('/api/projects/p1/history-values', () => HttpResponse.json({ values: [], emptyCount: 0 })),
    );
    const history = (id: string): EditorCondition =>
      ({ id, field: 'history', op: 'Changed', value: { historyField: 'Sprint' } });
    renderEditor(root({ conditions: [...['a', 'b', 'c', 'd', 'e'].map(history), summary('f')] }));

    // A sixth history condition does not fit; another summary row does.
    expect(screen.getByRole('button', { name: 'Duplicate condition 1' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Duplicate condition 1' })).toHaveAttribute('title', 'Limit: 5 history conditions');
    expect(screen.getByRole('button', { name: 'Duplicate condition 6' })).toBeEnabled();
    expect(within(screen.getByLabelText('Field for condition 6')).getByRole('option', { name: /History of a field/ }))
      .toBeDisabled();
  });
```

Append to `src/JiraUnpacked.Web/src/features/issues/ValuePicker.test.tsx` (inside the `MultiValuePicker` describe; add `MultiValuePicker` to its import from `./ValuePicker` if it is not there):

```tsx
  it('leaves (Empty) out when it is not allowed', () => {
    render(
      <MultiValuePicker
        label="Authors"
        options={[{ name: 'Alice', count: 3 }]}
        emptyCount={2}
        selected={[]}
        max={100}
        allowEmpty={false}
        onChange={() => {}}
      />,
    );
    expect(screen.queryByRole('checkbox', { name: /^\(empty\)/i })).toBeNull();
    expect(screen.getByRole('checkbox', { name: /^alice/i })).toBeInTheDocument();
  });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/ConditionRow.test.tsx src/features/issues/GroupEditor.test.tsx src/features/issues/ValuePicker.test.tsx`
Expected: FAIL — no "History of a field…" entry (the field shows as `history`), no history controls, `allowEmpty` ignored, the duplicate button enabled at the cap.

- [ ] **Step 3: Implement**

(a) `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx` — `MultiValuePickerProps` gains:

```ts
  /** Offer (Empty)? Yes by default; history authors say no — the server refuses (Empty) in `by`. */
  allowEmpty?: boolean;
```

the signature becomes `export function MultiValuePicker({ label, options, emptyCount, smart, selected, onChange, max, allowEmpty = true }: MultiValuePickerProps) {`, and `showEmpty` becomes:

```ts
  const showEmpty = allowEmpty && (needle === '' || EMPTY_LABEL.toLowerCase().includes(needle));
```

(b) Create `src/JiraUnpacked.Web/src/features/issues/HistoryConditionControls.tsx`:

```tsx
import { useQuery } from '@tanstack/react-query';
import type { FilterOperator, HistoryCompare, HistoryValue, HistoryValueKind, ListValue } from '../../api/issues';
import { getHistoryValues } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Field, Select } from '../../ui';
import { ChipInput } from './ChipInput';
import { DuringControl } from './DuringControl';
import { HistoryFieldSelect } from './HistoryFieldSelect';
import { HISTORY_COMPARES, MAX_CHANGE_COUNT, canonicalHistoryValue, freshHistoryValue } from './historyShapes';
import { INPUT_CLASS } from './styles';
import { MultiValuePicker } from './ValuePicker';

/** A name, or (Empty). */
type Name = string | null;
const isName = (v: ListValue): v is Name => v === null || typeof v === 'string';

interface HistoryListProps {
  projectId: string;
  historyField: string;
  kind: HistoryValueKind;
  /** Accessible name, e.g. "Values for condition 1". */
  name: string;
  id: string;
  selected: Name[];
  /** (Empty) on offer: for values yes, for authors no. */
  allowEmpty: boolean;
  max: number;
  onChange: (next: Name[]) => void;
}

/**
 * A multi-value list fed by history-values. Typed chips while it loads, if it fails, or before a
 * field is chosen — the list is an affordance, not a gate.
 */
function HistoryList({ projectId, historyField, kind, name, id, selected, allowEmpty, max, onChange }: HistoryListProps) {
  const list = useQuery({
    queryKey: queryKeys.historyValues(projectId, historyField, kind),
    queryFn: () => getHistoryValues(projectId, historyField, kind),
    enabled: historyField.trim() !== '',
    staleTime: 5 * 60 * 1000,
  });
  if (list.data) {
    return (
      <MultiValuePicker
        label={name}
        options={list.data.values}
        emptyCount={list.data.emptyCount}
        allowEmpty={allowEmpty}
        selected={selected}
        max={max}
        onChange={(next) => onChange(next.filter(isName))}
      />
    );
  }
  // Typing cannot produce (Empty), but one that arrived in a link is kept, not silently lost.
  const kept = selected.filter((v) => v === null);
  return (
    <ChipInput
      key={historyField}
      id={id}
      label={name}
      values={selected.filter((v): v is string => v !== null)}
      max={max - kept.length}
      onChange={(next) => onChange([...kept, ...next])}
    />
  );
}

interface HistoryConditionControlsProps {
  projectId: string;
  /** Position in the tree, e.g. "1" or "2.1" — part of every control's accessible name. */
  label: string;
  op: FilterOperator;
  /** Undefined = nothing readable yet: the controls start from the operator's fresh value. */
  value: HistoryValue | undefined;
  maxInValues: number;
  onChange: (next: HistoryValue) => void;
}

/**
 * The second line of a history row: the History field, then what the operator takes — the
 * values it was; from / to / by / during; or compare, count and during. Every change leaves in
 * the operator's canonical shape, so a link or a saved view never differs from what the editor
 * itself would write.
 */
export function HistoryConditionControls({ projectId, label, op, value, maxInValues, onChange }: HistoryConditionControlsProps) {
  const v = value ?? freshHistoryValue(op, '');
  const set = (patch: Partial<HistoryValue>) => onChange(canonicalHistoryValue(op, { ...v, ...patch }));
  const setList = (key: 'values' | 'from' | 'to' | 'by', next: Name[]) => {
    const patch: Partial<HistoryValue> = {};
    if (key === 'by') patch.by = next.filter((x): x is string => x !== null);
    else patch[key] = next;
    set(patch);
  };
  const list = (key: 'values' | 'from' | 'to' | 'by', kind: HistoryValueKind, name: string, allowEmpty: boolean) => (
    <HistoryList
      projectId={projectId}
      historyField={v.historyField}
      kind={kind}
      name={name}
      id={`condition-${label}-${key}`}
      selected={v[key] ?? []}
      allowEmpty={allowEmpty}
      max={maxInValues}
      onChange={(next) => setList(key, next)}
    />
  );
  const during = <DuringControl label={label} value={v.during} onChange={(next) => set({ during: next })} />;

  return (
    <div className="flex basis-full flex-wrap items-end gap-2">
      <HistoryFieldSelect projectId={projectId} label={label} value={v.historyField} onChange={(historyField) => set({ historyField })} />

      {op === 'Was' && list('values', 'value', `Values for condition ${label}`, true)}

      {op === 'Changed' && (
        <>
          {list('from', 'value', `From values for condition ${label}`, true)}
          {list('to', 'value', `To values for condition ${label}`, true)}
          {list('by', 'author', `Authors for condition ${label}`, false)}
          {during}
        </>
      )}

      {op === 'ChangeCount' && (
        <>
          <Field label={`Compare for condition ${label}`} id={`condition-${label}-compare`}>
            <Select value={v.compare ?? ''} onChange={(e) => set({ compare: e.target.value as HistoryCompare })}>
              {v.compare === undefined && <option value="">— choose —</option>}
              {HISTORY_COMPARES.map((c) => (
                <option key={c.key} value={c.key}>{c.label}</option>
              ))}
            </Select>
          </Field>
          <Field label={`Count for condition ${label}`} id={`condition-${label}-count`}>
            <input
              type="number"
              min={0}
              max={MAX_CHANGE_COUNT}
              step={1}
              className={INPUT_CLASS}
              value={v.count ?? ''}
              // Blank is "not entered yet": the key goes, and the row is unfinished until a number is typed.
              onChange={(e) => set({ count: e.target.value === '' ? undefined : Math.trunc(Number(e.target.value)) })}
            />
          </Field>
          {during}
        </>
      )}
    </div>
  );
}
```

(c) `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx`:

- Imports — add:

```tsx
import { HistoryConditionControls } from './HistoryConditionControls';
import { HISTORY_FIELD, readHistoryValue, takesHistory } from './historyShapes';
```

- `ConditionRowProps` gains:

```tsx
  /**
   * The filter already holds the most history conditions it may (catalog.maxHistoryConditions),
   * so this row may not become one. False for a row that already is one.
   */
  historyAtCap?: boolean;
```

and the signature becomes `export function ConditionRow({ projectId, condition, catalog, onChange, label, historyAtCap = false }: ConditionRowProps) {`.

- `renderValueControl` — first, before `if (takesNoValue(op)) return null;`:

```tsx
    if (fieldType === 'History') {
      if (!takesHistory(op)) return null;
      // Its own second line (basis-full): the field, then what the operator takes.
      return (
        <HistoryConditionControls
          projectId={projectId}
          label={label}
          op={op}
          value={readHistoryValue(op, condition.value)}
          maxInValues={catalog.maxInValues}
          onChange={(next) => onChange({ field, op, value: next })}
        />
      );
    }
```

- The field `<Select>`'s options become:

```tsx
          {!known && <option value={field} disabled>{field}</option>}
          {fieldNames(catalog).filter((f) => f !== HISTORY_FIELD).map((f) => (
            <option key={f} value={f}>{f}</option>
          ))}
          {/* Last, and named for what it is: not a column of the issue, but its past. */}
          {fieldNames(catalog).includes(HISTORY_FIELD) && (
            <option value={HISTORY_FIELD} disabled={historyAtCap}>
              {historyAtCap ? `History of a field… (limit: ${catalog.maxHistoryConditions})` : 'History of a field…'}
            </option>
          )}
```

(d) `src/JiraUnpacked.Web/src/features/issues/GroupEditor.tsx`:

- Import `import { HISTORY_FIELD } from './historyShapes';`.
- `limitHint` and the two add hints become:

```tsx
  /** Why adding `extra` would break a limit, as the hint the disabled button shows; undefined = it fits. */
  const limitHint = (extra: TreeCounts): string | undefined => {
    if (counts.conditions + extra.conditions > catalog.maxConditions) return `Limit: ${catalog.maxConditions} conditions`;
    if (counts.groups + extra.groups > catalog.maxGroups) return `Limit: ${catalog.maxGroups} groups`;
    if (counts.history + extra.history > catalog.maxHistoryConditions) {
      return `Limit: ${catalog.maxHistoryConditions} history conditions`;
    }
    return undefined;
  };
  const addConditionHint = limitHint({ conditions: 1, groups: 0, history: 0 });
  // A new group arrives with one blank condition, so it needs room for both.
  const addGroupHint = limitHint({ conditions: 1, groups: 1, history: 0 });
```

- Inside `conditions.map((c, i) => { … })`, replace the `copyHint` and `enableHint` lines (and the two comments above them) with:

```tsx
        const isHistory = c.field === HISTORY_FIELD;
        // Copying a row, or switching a disabled one back on, adds exactly that row — a history
        // row counts against the history cap too. Copying a switched-off row adds nothing that counts.
        const rowHint = limitHint({ conditions: 1, groups: 0, history: isHistory ? 1 : 0 });
        const copyHint = c.disabled ? undefined : rowHint;
        const enableHint = c.disabled ? rowHint : undefined;
```

- Pass the cap to the row:

```tsx
              <ConditionRow
                projectId={projectId}
                catalog={catalog}
                condition={c}
                label={rowLabel}
                historyAtCap={!isHistory && counts.history >= catalog.maxHistoryConditions}
                onChange={(next) =>
                  set({ conditions: conditions.map((x) => (x.id === c.id ? withIdentityOf(x, next) : x)) })}
              />
```

- [ ] **Step 4: Run the whole web suite, lint and build**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test && npm run build`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): history conditions in the filter builder — was, changed, number of changes, during, cap" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Final verification (after Task 8, before handing back)

- [ ] `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_history dotnet test JiraUnpacked.sln -v q` → PASS (whole suite, integration included; `HistoryQueryPlanTests` is inert without `JU_PERF_DB`).
- [ ] `cd C:/jw-filters && dotnet ef migrations has-pending-model-changes -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Infrastructure` → `No changes have been made to the model since the last migration.` (true whether or not Task 5 Step 4 added the index).
- [ ] `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test && npm run build` → PASS.
- [ ] `git -C C:/jw-filters diff origin/develop -- src/JiraUnpacked.Web/package.json src/JiraUnpacked.Web/package-lock.json '*.csproj'` → empty (no dependency changes).
- [ ] `git -C C:/jw-filters diff origin/develop -- src | grep -E "^\+.*(IgnoreQueryFilters|FromSql)"` → no output.
- [ ] `git -C C:/jw-filters grep -n "localStorage" -- src/JiraUnpacked.Web/src/features` → no new hits.
- [ ] The perf gate result is recorded: `git -C C:/jw-filters log -1 --format=%B <Task 5 commit>` shows the six operators with their match counts and times, all < 2000 ms (and, if the index was needed, both runs).
- [ ] `git -C C:/jw-filters status` clean; `git -C C:/jw-filters log --oneline origin/develop..` shows the spec commit, this plan's commit and eight task commits. Nothing pushed.

---

## Self-Review

**Spec coverage.**
§1 Model — pseudo-field `history`, type `History`, three appended operators: Task 1 (field, enum), Tasks 2–3 (operators). `Was` (From/To, current value for the five fields via dictionary resolution, null ↔ current value empty): Task 2. `Changed` with from/to/by/during, no parts = changed at all: Tasks 1–3. `ChangeCount` with compare/count/during, no rows = 0, `Eq 0` on an unused field matches everything: Task 3. `historyField` required, non-blank, ≤ 100, exact; unused name matches nothing: Task 1. Element matching, (Empty), exact, ≤ 100 × ≤ 255: Task 2. `by` exact, null refused: Task 2. compare set, count 0…10000: Task 3. Window kinds with InLast/InPeriod/Between rules and the `IClock`: Task 3 (shared readers). Empty rule unaffected: nothing touches it. Cap 5 + catalog `maxHistoryConditions`: Task 1.
§2 Translation — source from the compiler's injected context, correlated `Any`/`Count`, never materialised: Tasks 1–3 (spike first, SQL-shape tests); `==`/StartsWith/EndsWith/Contains on parameters, escaped: Task 2 (wildcard test on Postgres); window bounds and cutoff as parameters: Task 3. Performance gate with EXPLAIN (ANALYZE, BUFFERS), read-only, largest project, < 2 s, index only if needed and generated with `-s src/JiraUnpacked.Infrastructure`: Task 5.
§3 Value lists — both endpoints, caps 200/1000, split lists, author kind, unknown field → empty, tenant-scoped, 404: Task 4.
§4 SPA — "History of a field…" last; labels; History field select with counts and typed fallback; was / changed / number of changes controls; compare labels; count default ≥ 2; During with the four choices reusing sub-project 3's controls and zone capture; second line: Tasks 7–8. Completeness: Task 6. Cap with disabled entry and hint: Task 8. Canonical round trip through `readCondition`, `toSavable`, `isModified`: Task 6.
§5 Testing — translator unit tests for each operator, element matching incl. "PLAT 10" vs "PLAT 101", (Empty), `by`, each window, every compare incl. `Eq 0`, current-value `Was` for the five fields, the cap, the 400s (missing field, bad compare, count out of range, null in `by`, oversized lists): Tasks 1–3. Integration: list and export with each operator (Tasks 1–3), tenant isolation for matching and counting (Tasks 1, 3), both endpoints with split lists and caps (Task 4). Perf gate recorded (Task 5). Web: wire shapes, completeness, window control, history-field fallback, cap, URL and saved-view round trip (Tasks 6–8).

**Placeholder scan.** No TBD/TODO. Every code step shows its code; the edits to long existing files (ConditionRow in Task 8, filterTree, GroupEditor, ValuePicker) quote the exact lines to replace or insert. One deliberate fill-in remains: Task 5's commit message takes the *measured* timings — it says so, and Final verification checks it.

**Type consistency.** Server: `BuildAsync(…, CancellationToken ct = default, IQueryable<IssueChangeHistory>? history = null)` is what the compiler (Task 1), `HistorySqlShapeTests.SqlAsync` and `HistoryTranslationTests.BuildAsync` call; `HistoryScope`/`ReadHistoryScope`/`AnyRow`/`Source`/`Member` (Task 1) are reused unchanged by Tasks 2–3; Task 1's `BuildHistory` becomes `BuildHistoryAsync` in Task 2, with the dispatch line changed in the same step; `BuildChanged` becomes an instance method in Task 3 when it needs the clock; `ReadSince`/`ReadPeriod`/`ReadDateRange`/`DateRangeTest` (Task 3) are what `ReadWindow` calls; `FilterCatalogView` gains `MaxHistoryConditions` last and its one constructor call is updated in the same task. Web: `HistoryValue`/`HistoryWindow`/`HistoryCompare` (Task 6) are what `historyShapes`, `DuringControl`, `HistoryConditionControls` use; `TreeCounts.history` (Task 6) is what `GroupEditor` reads (Task 8); `conditionParts`/`PartNamer`/`AmountInputs`/`PeriodSelect`/`RangeInputs` (Task 7) are what `ConditionRow` and `DuringControl` import; `getHistoryFields`/`getHistoryValues`/`queryKeys.historyFields`/`historyValues` (Task 7) are what `HistoryFieldSelect` and `HistoryList` call; the accessible names in Tasks 7–8 are the ones their tests query.

**Review Focus.** Six lines above, each with its pinning test or check named in the owning task (1: Tasks 1, 3; 2: Task 2; 3: Task 3; 4: Tasks 2–3; 5: Task 6; 6: Task 5).
