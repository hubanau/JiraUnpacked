# Filter Readability (Summary, JQL / Open in Jira, Click-to-Filter) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a filter readable (one sentence above the builder, which starts collapsed when a link or view brings a filter), portable (the equivalent JQL, copied or opened in Jira, with every difference listed) and quick to build (filter to — or exclude — a value straight from a table cell).

**Architecture:** The server gains a pure, unit-tested `JqlTranslator` (partial class: tree/sort, fields, lookup-backed fields, dates, history) fed a tenant-scoped `JqlLookup` (account ids, sprint ids, team ids, Team / Story Points mappings) that `IssueJqlService` loads through the injected `AppDbContext` after the shared `IIssueFilterCompiler` has validated the filter — so the new `POST /api/projects/{projectId}/issues/jql` answers the same 404s and 400s as the query. The SPA gains a pure `describeFilter` over the pruned tree the list is queried with, a `FilterSummary` line with a Collapse / Edit filter toggle (component state), `JqlActions` (Copy JQL, Open in Jira, "⚠ N differences") fetched on demand through the query cache, and click-to-filter: a pure `cellFilter` module plus a `CellFilterMenu` in each filterable cell.

**Tech Stack:** .NET 8, EF Core 8.0.11 + Npgsql 8.0.11, xUnit 2.5; React 19, react-router-dom 7, @tanstack/react-query 5, Vitest + Testing Library + user-event 14 + MSW 2, Tailwind 4.

**Spec:** `docs/superpowers/specs/2026-10-15-filter-readability-design.md`

## Global Constraints

- **Work only in the worktree `C:/jw-filters`** (branch `feat/filter-readability`, based on `origin/develop`, which already has sub-projects 1–4: builder v2, saved views, richer values, history conditions). Never read or write `C:\Users\AliaksandrHubanau\Private\Claude\JiraUnpacked` — other sessions use that checkout. Do not push.
- **Integration tests use their own database:** every `dotnet test` run sets `JU_TEST_DATABASE=ju_test_readability` (Postgres = the `jiraunpacked-db` container on `localhost:5432`, already running). Bash: `JU_TEST_DATABASE=ju_test_readability dotnet test …`. PowerShell: `$env:JU_TEST_DATABASE='ju_test_readability'; dotnet test …`. The suite never truncates; a shared database collides with parallel branches. Integration tests that read with `IgnoreQueryFilters` must scope by their own `JiraProjectId` (none in this plan do).
- **Web setup:** `node_modules` already exists in `src/JiraUnpacked.Web`; if it is missing, run `npm ci` there once.
- SPA keeps **exactly four runtime deps**: `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`. **No new dependencies of any kind** (runtime or dev, .NET or npm).
- Endpoint: `POST /api/projects/{projectId}/issues/jql` with `{ filter?, sortBy?, sortDescending? }` (`filter` = the same `FilterGroup` the query takes; `sortDescending` defaults to `true`) → `200 { jql, url, warnings: [{ message }] }`. Tenant-scoped; **404** for an unknown or another tenant's project; an invalid filter or sort is **the same 400 (same title) as `POST /issues/query`**, checked in the query's order: project, then filter (by `IIssueFilterCompiler`), then sort (`SortFields.IsKnown`).
- `jql` = `project = "<KEY>"` + (` AND <tree>` when anything is left) + ` ORDER BY <field> <ASC|DESC>, key ASC` (just `ORDER BY key <dir>` when sorting by key; `ORDER BY key ASC` + a warning when JQL cannot sort by the list's column). `url` = `<connection BaseUrl without trailing slash>/issues/?jql=<Uri.EscapeDataString(jql)>`.
- **Nothing from the request reaches JQL unquoted:** every string goes through `JqlText.Quote` (`"` and `\` escaped); numbers through `InvariantCulture`; account ids, sprint ids and team ids come from the database (`JqlLookup`), never from the request. Digit-only ids (sprint, parent) are written bare, any other id quoted.
- All reads through the injected tenant-scoped `AppDbContext`. No `IgnoreQueryFilters`, no `FromSql` in `src/`.
- A part JQL cannot express is **left out with a warning** that names it (`Left out "<field> <Op> <values>" (<why>)`) and says **wider** (dropped from an All group) or **narrower** (dropped from an Any group) — a group with nothing left defers to the group holding it; at the root it is wider. Every warning appears once.
- SPA copy, verbatim: summary `Showing: <sentence>` (+ ` · N issues` / ` · 1 issue` when known); empty filter `All issues`; toggle **`Collapse`** / **`Edit filter`** (with `aria-expanded`); toolbar **`Copy JQL`**, **`Open in Jira`**, toast **`JQL copied`**, warnings button **`⚠ N differences`** / **`⚠ 1 difference`** (the ⚠ is `aria-hidden`); warnings dialog title **`How the JQL differs from this filter`**; copy fallback dialog title **`Copy the JQL`**; cell menu button `aria-label="Filter by <Column> <value>"` (empty cell: `(Empty)`), items **`Filter to "<value>"`** / **`Exclude "<value>"`** (empty cell: `Filter to (Empty)` / `Exclude (Empty)`).
- `window.open(url, '_blank', 'noopener,noreferrer')`, and only for an `http(s)://` URL. Clipboard through `navigator.clipboard.writeText`, falling back to a dialog with the JQL selected for copying by hand.
- The collapse toggle is **component state** — not the URL, not localStorage. `src/ui/` imports neither `src/api/` nor `src/auth/`; new modules live in `src/features/issues/`. Colours only through `--ju-*` CSS variables. Only `ju.username` and `ju.theme` may go to `localStorage`.
- Out of scope (spec): importing JQL into a filter; editing the sentence; per-user Jira locale/week-start detection; custom fields other than the mapped Team and Story Points.
- Every commit message ends with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (second `-m`).

## Review Focus

1. **A display name two Jira accounts share, and a sprint name two boards share.** The issue filter matches both, so the JQL must list both ids — not the first one found. Pinned by Task 2 (`Assignee_is_named_by_account_id`, the "Sam" lines) and Task 5 (the lookup groups by name, `ByName`).
2. **Quotes and backslashes in a value or a project key, and a base URL with a trailing slash.** Expected: escaped inside a quoted literal; the link has exactly one `/` before `issues/` and an encoded JQL with no raw space or quote. Pinned by Task 1 (`Strings_are_quoted_with_quotes_and_backslashes_escaped`) and Task 5 (`Translates_with_this_tenants_ids_and_links_to_the_connections_jira`).
3. **A part left out deep in the tree** — inside an Any inside an All, a group whose every part is left out, a filter that is left out entirely. Expected: the warning says wider or narrower correctly, and an empty result is `project = … ORDER BY …`, never `AND ()`. Pinned by Task 1 (`A_group_with_nothing_left_is_judged_by_the_group_holding_it`, `When_nothing_is_left_the_jql_is_the_whole_project`) and Task 4 (`Number_of_changes_and_unsupported_fields_are_left_out`).
4. **A `url` that is not a web address** (a connection whose base URL slipped past validation). Expected: the SPA never opens it and says why. Pinned by Task 8 (`never opens a link that is not a web address`).
5. **What the list opened with** — a bare URL, a link with a filter, a default view that arrives after mount, and a later "Edit filter". Expected: only a filter makes the builder start collapsed; the default view counts as loaded on mount; nothing the user toggles afterwards is overridden. Pinned by Task 7 (`opens a link with a filter collapsed …`, `opens a default view collapsed`, `opens a bare list with the builder expanded …`).
6. **Click-to-filter at the edges** — an Any root, a switched-off root, the depth and group limits a wrap would break, a click repeated. Expected: always narrows, wrapped only when needed, disabled with the builder's own hint, never added twice. Pinned by Task 9 (`cellFilter.test.ts` and `wraps an Any root so a click always narrows`, `adds an identical condition only once`, `disables both items at the condition limit …`).

## Decisions this plan makes where the spec is silent

- **`describeFilter(filter: FilterGroup | undefined): string` takes the pruned wire group**, not `(tree, catalog)`: IssueList already computes `pruneTree(tree, knownFields)` for the query, so the sentence and the request are the same object by construction, and the catalog's one role (dropping retired fields) is already applied.
- **Sentence wording** beyond the spec's examples: text operators quote their value (`Summary contains "login"`); a Between with one open bound reads `≥ x` / `≤ x`; a history window `between A and B` / `since A` / `until B` / `in the last N days` / `in period last month (Zone)`; a history list of one reads `was X`, of several `was any of X, Y`; `changed = 1 time` / `N times`; units singular for 1; dates are a fixed English `1 Sep 2026` (`1 Sep 2026 10:30` with a time), not the locale's, so a shared link reads the same; a nested group of one part is not parenthesised; a history condition names the field as Jira reports it (`Sprint`, `status`).
- **The collapse rule is decided once,** in the render where the saved-views gate (`holdQuery`) settles: collapsed iff the tree is non-empty. So a link or a (default) view with a filter starts collapsed; a view without a filter, or a URL with only a sort, starts expanded. Opening a view later from the menu does not change it.
- **The JQL is fetched on demand**, not on every edit: on hover/focus of either button (a prefetch) and on click, through `queryClient.fetchQuery` under `queryKeys.issueJql(projectId, request)` with a 30 s `staleTime`. A disabled `useQuery` on the same key shows "⚠ N differences" once the JQL for *the current* filter is known — never an earlier filter's. The prefetch also keeps `window.open` close to the click, which is what popup blockers judge.
- **Clipboard fallback** is a dialog with the JQL in a read-only textarea that selects itself on focus — no deprecated `execCommand`.
- **The project key is always quoted** (`project = "ACME"`), so a key that is a JQL word can never break the query.
- **Unknown names** (assignee, sprint, team): the issue filter matches no issue for a name with no row either, so dropping such a name from a list keeps the meaning; it is still reported ("No Jira account in this project's data is named 'Ghost' …"). An *is / is any of* with nothing left is left out (JQL has no "false"); an *is not / is none of* with nothing left is `<field> is not EMPTY` — exactly how the list reads it.
- **Sprint warning:** any sprint condition other than is empty / is not empty adds "Jira matches an issue to every sprint it has been in; this list matches only its latest sprint …" — a real difference the spec's table does not list.
- **History of people** (assignee, reporter — and every `by`) is named by account id; there an unknown name is NOT equivalent (the list matches the changelog's display names), so dropping it is reported as **narrower**, and a list left with nothing makes the whole condition left out. JQL history fields: the changelog names `status`, `assignee`, `reporter`, `priority`, `resolution`, `Fix Version` (→ `fixVersion`); anything else is left out.
- **CHANGED with lists:** JQL takes one value per `FROM`/`TO`/`BY`, so lists become their combinations OR-ed (each still one change satisfying all parts), at most **20** clauses; more is left out with the count in the reason. (Empty) is `WAS EMPTY` / `FROM EMPTY` / `TO EMPTY`.
- **Dates:** comparisons and bounds are UTC, `"yyyy-MM-dd"` at midnight else `"yyyy-MM-dd HH:mm"`; a date-only Between end becomes `< next day`; a Between whose ends are both open (e.g. `[null, "9999-12-31"]`) is `<field> is not EMPTY`. Calendar periods are `(<f> >= startOfX(n) AND <f> <= endOfX(n))`; a history window `inPeriod` is `DURING (startOfX(n), endOfX(n))`, `inLast` `AFTER -Nd`, `between` `DURING (a, b)` / `AFTER a` / `BEFORE b`.
- **Sort:** `statusCategory`, and `team` / `storyPoints` without a custom-field mapping, cannot be sorted by in JQL → `ORDER BY key ASC` + a warning. A mapping is usable only as `customfield_<digits>` → `cf[<digits>]`.
- **Click-to-filter:** a switched-off root is wrapped too (adding to it would change nothing); an item whose operator the field lacks (Exclude on Parent — `parent` has no `Neq`) is disabled with `Not available for <Column>`; the depth hint reads `Limit: 3 levels of groups`; the duplicate check applies only when no wrap is needed (inside a wrap the same condition still narrows). The menu is a popover of plain buttons (`role="group"`), like the Views menu — not an ARIA menu.
- **Existing tests:** a link or default view with a filter now opens collapsed, so the existing tests that mount that way and then use the builder first click **Edit filter** (helper `expandFilter()`); one cell lookup by exact accessible name gains a prefix match because a filterable cell now also contains its ⋯ button. No other existing test changes.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Domain/Abstractions/IIssueJqlService.cs` | **new** — request/response records and the service contract |
| `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlText.cs` | **new** — quoting, ids, numbers, dates, `cf[N]`, joining |
| `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlLookup.cs` | **new** — `JqlLookup` (ids by name, mappings) and `JqlTranslation` |
| `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.cs` | **new** — tree walk, left-out direction, ORDER BY, field dispatch |
| `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.Fields.cs` | **new** — names (status, type, priority, category), key, summary, parent; shared `ReadPick`/`Dictionary` |
| `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.Lookups.cs` | **new** — assignee, sprint (smart), team, story points |
| `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.Dates.cs` | **new** — comparisons, Between, relative, periods (quarters); readers shared with history windows |
| `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.History.cs` | **new** — WAS, CHANGED, windows |
| `src/JiraUnpacked.Infrastructure/Reporting/Jql/IssueJqlService.cs` | **new** — validate like the query, load the lookup, build jql + url |
| `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` | registers `IIssueJqlService`, `JqlTranslator` |
| `src/JiraUnpacked.Api/Controllers/IssuesController.cs` | `POST …/issues/jql` |
| `tests/JiraUnpacked.Tests/Reporting/JqlTestKit.cs` | **new** — builders for the translator tests |
| `tests/JiraUnpacked.Tests/Reporting/JqlTranslatorTests.cs` | **new** — tree, sort, names, key, summary, parent, left-out direction |
| `tests/JiraUnpacked.Tests/Reporting/JqlLookupFieldTests.cs` | **new** — assignee, sprint, team, story points |
| `tests/JiraUnpacked.Tests/Reporting/JqlDateTests.cs` | **new** — dates with a fixed clock |
| `tests/JiraUnpacked.Tests/Reporting/JqlHistoryTests.cs` | **new** — WAS, CHANGED, windows, left-out history |
| `tests/JiraUnpacked.Tests/Integration/JqlTestData.cs` | **new** — users, sprints, teams, mappings, base URL fixtures |
| `tests/JiraUnpacked.Tests/Integration/IssueJqlEndpointTests.cs` | **new** — endpoint: ids, url, tenant isolation, 404, 400 parity |
| `src/JiraUnpacked.Web/src/features/issues/describeFilter.ts` (+ `.test.ts`) | **new** — the sentence |
| `src/JiraUnpacked.Web/src/features/issues/FilterSummary.tsx` (+ `.test.tsx`) | **new** — summary line + toggle |
| `src/JiraUnpacked.Web/src/test/expandFilter.ts` | **new** — test helper: click Edit filter |
| `src/JiraUnpacked.Web/src/api/issues.ts` | `JqlRequest`, `JqlResult`, `getIssueJql` |
| `src/JiraUnpacked.Web/src/api/queryKeys.ts` | `issueJql` |
| `src/JiraUnpacked.Web/src/features/issues/JqlActions.tsx` (+ `.test.tsx`) | **new** — Copy JQL, Open in Jira, differences |
| `src/JiraUnpacked.Web/src/features/issues/cellFilter.ts` (+ `.test.ts`) | **new** — cell values, conditions, wrap rule, limits |
| `src/JiraUnpacked.Web/src/features/issues/CellFilterMenu.tsx` | **new** — the ⋯ popover |
| `src/JiraUnpacked.Web/src/features/issues/CellFilter.test.tsx` | **new** — click-to-filter through IssueList |
| `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx` | summary + collapse, JqlActions, cell menus |
| `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`, `SavedViews.test.tsx`, `ColumnChooser.test.tsx` | expand first / prefix cell name (see Tasks 7, 9) |

---

### Task 1: The JQL translator core — text, tree, sort, names, key, summary, parent (server, pure)

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlText.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlLookup.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.Fields.cs`
- Create: `tests/JiraUnpacked.Tests/Reporting/JqlTestKit.cs`
- Create: `tests/JiraUnpacked.Tests/Reporting/JqlTranslatorTests.cs`

**Interfaces:**
- Consumes: `FilterGroup`, `FilterCondition`, `FilterOperator`, `FilterLogic` (Domain); `IClock`; `SortFields.Default` (internal, `JiraUnpacked.Infrastructure.Reporting.Filtering`).
- Produces: `public sealed partial class JqlTranslator(IClock clock)` with `public JqlTranslation Translate(string projectKey, FilterGroup? filter, string? sortBy, bool sortDescending, JqlLookup lookup)`.
- Produces: `public sealed record JqlLookup(IReadOnlyDictionary<string, IReadOnlyList<string>> AccountIds, IReadOnlyDictionary<string, IReadOnlyList<string>> SprintIds, IReadOnlyDictionary<string, IReadOnlyList<string>> TeamIds, string? TeamField, string? StoryPointsField)` with `static JqlLookup Empty`; `public sealed record JqlTranslation(string Jql, IReadOnlyList<string> Warnings)`.
- Produces (internal to the partial class, used by Tasks 2–4): `Run` (`Lookup`, `Warnings`, `Warn(string)`), `Clause` (`Of(string)`, `Out(string why)`), `Pick(List<string> Names, bool WantsEmpty, List<string> Smart)`, `ReadPick(FilterCondition)`, `Dictionary(string field, FilterOperator op, IReadOnlyList<string> literals, bool wantsEmpty, IReadOnlyList<string> extra, string whyNone)`, the `TranslateCondition` switch (Tasks 2–4 add arms).
- Produces (internal static `JqlText`): `Quote(string)`, `Id(string)`, `List(IEnumerable<string>)`, `Number(decimal)`, `Date(DateTime)`, `CustomField(string?)`, `Join(IReadOnlyList<string> parts, string op)`.
- Produces (tests): `JqlTestKit` — `Now` (2026-10-15 12:00 UTC), `Sut()`, `J`, `C(field, op, value?)`, `L(field, op, params values)`, `All(...)`, `Any(...)`, `g.With(groups...)`, `Ids((name, ids[])...)`, `Translate(filter, lookup?, sortBy?, sortDescending = true)` (project key `ACME`), `Where(JqlTranslation)`, `Where(FilterCondition, JqlLookup?)`.

- [ ] **Step 1: Write the test kit and the failing tests**

Create `tests/JiraUnpacked.Tests/Reporting/JqlTestKit.cs`:

```csharp
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Jql;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>Builders for the JQL translator's tests: conditions, groups, lookups, and the WHERE part of a result.</summary>
internal static class JqlTestKit
{
    /// <summary>A Thursday noon: this quarter is Oct–Dec 2026, the last one Jul–Sep.</summary>
    internal static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    internal static JqlTranslator Sut() => new(new FakeClock(Now));

    internal static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    /// <summary>A single-value condition; no value for IsNull / IsNotNull.</summary>
    internal static FilterCondition C(string field, FilterOperator op, object? value = null) =>
        new(field, op, value is null ? null : J(value), null);

    /// <summary>A list condition. A lone null must be passed as <c>new object?[] { null }</c> (params would take it as the array).</summary>
    internal static FilterCondition L(string field, FilterOperator op, params object?[] values) =>
        new(field, op, null, values.Select(J).ToList());

    internal static FilterGroup All(params FilterCondition[] cs) => new(FilterLogic.And, cs.ToList(), null);

    internal static FilterGroup Any(params FilterCondition[] cs) => new(FilterLogic.Or, cs.ToList(), null);

    internal static FilterGroup With(this FilterGroup g, params FilterGroup[] groups) => g with { Groups = groups.ToList() };

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Ids(params (string Name, string[] Ids)[] rows) =>
        rows.ToDictionary(r => r.Name, r => (IReadOnlyList<string>)r.Ids, StringComparer.Ordinal);

    internal static JqlTranslation Translate(
        FilterGroup? filter, JqlLookup? lookup = null, string? sortBy = null, bool sortDescending = true) =>
        Sut().Translate("ACME", filter, sortBy, sortDescending, lookup ?? JqlLookup.Empty);

    /// <summary>The part between <c>project = "ACME" AND </c> and <c> ORDER BY</c>.</summary>
    internal static string Where(JqlTranslation t)
    {
        const string head = "project = \"ACME\" AND ";
        Assert.StartsWith(head, t.Jql);
        return t.Jql[head.Length..t.Jql.LastIndexOf(" ORDER BY ", StringComparison.Ordinal)];
    }

    internal static string Where(FilterCondition c, JqlLookup? lookup = null) => Where(Translate(All(c), lookup));
}
```

Create `tests/JiraUnpacked.Tests/Reporting/JqlTranslatorTests.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Jql;
using static JiraUnpacked.Tests.Reporting.JqlTestKit;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>The translator's tree walk, sort, the by-name fields, key, summary and parent — pure, no database.</summary>
public sealed class JqlTranslatorTests
{
    [Fact]
    public void No_filter_is_the_whole_project_in_the_lists_default_order()
    {
        var t = Translate(null);
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", t.Jql);
        Assert.Empty(t.Warnings);
    }

    [Fact]
    public void The_sort_becomes_order_by_with_key_as_the_tiebreaker()
    {
        Assert.EndsWith(" ORDER BY priority ASC, key ASC", Translate(null, sortBy: "priority", sortDescending: false).Jql);
        Assert.EndsWith(" ORDER BY created DESC, key ASC", Translate(null, sortBy: "createdAt").Jql);
        Assert.EndsWith(" ORDER BY issuetype DESC, key ASC", Translate(null, sortBy: "issueType").Jql);
        Assert.EndsWith(" ORDER BY key DESC", Translate(null, sortBy: "jiraKey").Jql);
    }

    [Fact]
    public void A_sort_jql_cannot_express_falls_back_to_key_and_says_so()
    {
        var t = Translate(null, sortBy: "statusCategory");
        Assert.EndsWith(" ORDER BY key ASC", t.Jql);
        Assert.Equal(new[] { "The JQL cannot sort by 'statusCategory', so it sorts by key." }, t.Warnings);
    }

    [Theory]
    [InlineData("status", "status")]
    [InlineData("issueType", "issuetype")]
    [InlineData("priority", "priority")]
    [InlineData("statusCategory", "statusCategory")]
    public void Dictionary_fields_compare_by_name(string field, string jql)
    {
        Assert.Equal($"{jql} = \"Done\"", Where(C(field, FilterOperator.Eq, "Done")));
        Assert.Equal($"{jql} != \"Done\"", Where(C(field, FilterOperator.Neq, "Done")));
        Assert.Equal($"{jql} in (\"To Do\", \"Done\")", Where(L(field, FilterOperator.In, "To Do", "Done")));
        Assert.Equal($"{jql} not in (\"To Do\", \"Done\")", Where(L(field, FilterOperator.NotIn, "To Do", "Done")));
        Assert.Equal($"{jql} is EMPTY", Where(C(field, FilterOperator.IsNull)));
        Assert.Equal($"{jql} is not EMPTY", Where(C(field, FilterOperator.IsNotNull)));
    }

    [Fact]
    public void Empty_in_a_list_follows_the_lists_empty_rule()
    {
        Assert.Equal("(status in (\"Done\") OR status is EMPTY)", Where(L("status", FilterOperator.In, "Done", null)));
        Assert.Equal("status is EMPTY", Where(L("status", FilterOperator.In, new object?[] { null })));
        // "is none of" never matches an empty value, here or in JQL: a null in it adds nothing.
        Assert.Equal("status not in (\"Done\")", Where(L("status", FilterOperator.NotIn, "Done", null)));
        Assert.Equal("status is not EMPTY", Where(L("status", FilterOperator.NotIn, new object?[] { null })));
    }

    [Fact]
    public void Strings_are_quoted_with_quotes_and_backslashes_escaped()
    {
        // The value is:  In "QA" \ 2   — the JQL literal:  "In \"QA\" \\ 2"
        Assert.Equal("status = \"In \\\"QA\\\" \\\\ 2\"", Where(C("status", FilterOperator.Eq, "In \"QA\" \\ 2")));
        Assert.StartsWith("project = \"A\\\"B\" ", Sut().Translate("A\"B", null, null, true, JqlLookup.Empty).Jql);
    }

    [Fact]
    public void Groups_join_with_and_or_and_nest_in_parentheses()
    {
        var filter = All(C("status", FilterOperator.Eq, "Done"))
            .With(Any(C("priority", FilterOperator.Eq, "High"), C("priority", FilterOperator.IsNull)));
        Assert.Equal("(status = \"Done\" AND (priority = \"High\" OR priority is EMPTY))", Where(Translate(filter)));
        Assert.Equal("(status = \"Done\" OR status = \"To Do\")",
            Where(Translate(Any(C("status", FilterOperator.Eq, "Done"), C("status", FilterOperator.Eq, "To Do")))));
    }

    [Fact]
    public void Disabled_parts_are_skipped_and_a_disabled_root_is_no_filter()
    {
        var filter = new FilterGroup(FilterLogic.And,
            new List<FilterCondition>
            {
                C("status", FilterOperator.Eq, "Done"),
                C("priority", FilterOperator.Eq, "Low") with { Disabled = true },
            },
            new List<FilterGroup> { Any(C("status", FilterOperator.IsNull)) with { Disabled = true } });
        Assert.Equal("status = \"Done\"", Where(Translate(filter)));
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", Translate(filter with { Disabled = true }).Jql);
    }

    [Fact]
    public void Key_summary_and_parent()
    {
        Assert.Equal("key = \"ACME-1\"", Where(C("jiraKey", FilterOperator.Eq, "ACME-1")));
        Assert.Equal("key in (\"ACME-1\", \"ACME-2\")", Where(L("jiraKey", FilterOperator.In, "ACME-1", "ACME-2")));
        Assert.Equal("key not in (\"ACME-1\")", Where(L("jiraKey", FilterOperator.NotIn, "ACME-1")));
        Assert.Equal("parent = 10023", Where(C("parent", FilterOperator.Eq, "10023")));
        Assert.Equal("parent = \"x-1\"", Where(C("parent", FilterOperator.Eq, "x-1")));
        Assert.Equal("parent is EMPTY", Where(C("parent", FilterOperator.IsNull)));
        Assert.Equal("summary is not EMPTY", Where(C("summary", FilterOperator.IsNotNull)));
    }

    [Fact]
    public void Text_search_is_word_based_and_says_so_once()
    {
        var t = Translate(All(
            C("summary", FilterOperator.Contains, "login"),
            C("summary", FilterOperator.NotContains, "flaky"),
            C("summary", FilterOperator.StartsWith, "Crash")));
        Assert.Equal("(summary ~ \"login\" AND NOT summary ~ \"flaky\" AND summary ~ \"Crash*\")", Where(t));
        Assert.Contains("word-based, not substring", Assert.Single(t.Warnings));
    }

    [Fact]
    public void A_left_out_part_of_an_all_group_makes_the_jql_wider()
    {
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"), C("jiraKey", FilterOperator.Contains, "PLAT")));
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Equal(new[]
        {
            "Left out \"jiraKey Contains PLAT\" (JQL cannot match part of an issue key), so the JQL is wider than this filter: it can match issues this list does not show.",
        }, t.Warnings);
    }

    [Fact]
    public void A_left_out_part_of_an_any_group_makes_the_jql_narrower()
    {
        var t = Translate(Any(C("status", FilterOperator.Eq, "Done"), C("jiraKey", FilterOperator.StartsWith, "PLAT")));
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains("narrower than this filter", Assert.Single(t.Warnings));
    }

    [Fact]
    public void A_group_with_nothing_left_is_judged_by_the_group_holding_it()
    {
        // The Any group loses its only part, so the All above drops a requirement: wider.
        var t = Translate(All(C("status", FilterOperator.Eq, "Done")).With(Any(C("jiraKey", FilterOperator.Contains, "PLAT"))));
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains("wider than this filter", Assert.Single(t.Warnings));

        // Inside an Any, the same emptied group was an alternative: narrower.
        var u = Translate(Any(C("status", FilterOperator.Eq, "Done")).With(All(C("jiraKey", FilterOperator.Contains, "PLAT"))));
        Assert.Contains("narrower than this filter", Assert.Single(u.Warnings));
    }

    [Fact]
    public void When_nothing_is_left_the_jql_is_the_whole_project()
    {
        var t = Translate(Any(C("jiraKey", FilterOperator.Contains, "PLAT")));
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", t.Jql);
        Assert.Contains("wider than this filter", Assert.Single(t.Warnings));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~JqlTranslatorTests"`
Expected: build FAILS — `The type or namespace name 'Jql' does not exist in the namespace 'JiraUnpacked.Infrastructure.Reporting'`.

- [ ] **Step 3: Write `JqlText` and `JqlLookup`**

Create `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlText.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// How values are written into JQL. Every string a filter carries goes through <see cref="Quote"/>:
/// nothing from a request reaches the JQL unquoted or unescaped.
/// </summary>
internal static class JqlText
{
    private static readonly Regex CustomFieldId = new(@"^customfield_(\d+)\z", RegexOptions.CultureInvariant);

    /// <summary>"x", with \ and " escaped — the two characters a JQL string literal reserves.</summary>
    internal static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>A Jira id: bare when it is all digits (sprint and issue ids, as Jira's own examples write them), quoted otherwise.</summary>
    internal static string Id(string id) => id.Length > 0 && id.All(char.IsAsciiDigit) ? id : Quote(id);

    internal static string List(IEnumerable<string> literals) => "(" + string.Join(", ", literals) + ")";

    internal static string Number(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary>A UTC instant as a JQL date: "yyyy-MM-dd" at midnight, "yyyy-MM-dd HH:mm" otherwise.</summary>
    internal static string Date(DateTime utc) => Quote(utc.TimeOfDay == TimeSpan.Zero
        ? utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    /// <summary>"customfield_10016" → "cf[10016]"; anything else is not a field JQL can name this way → null.</summary>
    internal static string? CustomField(string? jiraFieldId) =>
        jiraFieldId is not null && CustomFieldId.Match(jiraFieldId) is { Success: true } m ? $"cf[{m.Groups[1].Value}]" : null;

    /// <summary>One part as it is; several joined by <paramref name="op"/> inside parentheses, so the result is always one operand.</summary>
    internal static string Join(IReadOnlyList<string> parts, string op) =>
        parts.Count == 1 ? parts[0] : "(" + string.Join($" {op} ", parts) + ")";
}
```

Create `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlLookup.cs`:

```csharp
namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// What JQL needs that a filter does not carry: ids where the filter has display names, and the
/// project's custom fields. Loaded tenant-scoped by IssueJqlService; the translator only reads it,
/// so it is testable without a database. Names match exactly (ordinal), as the issue filter's do.
/// </summary>
/// <param name="AccountIds">Jira user display name → account ids (a name two accounts share maps to both).</param>
/// <param name="SprintIds">Sprint name → Jira sprint ids, this project's sprints only.</param>
/// <param name="TeamIds">Team name → Jira team ids, this project's teams only.</param>
/// <param name="TeamField">The Team mapping's Jira field id ("customfield_10001"), or null.</param>
/// <param name="StoryPointsField">The Story Points mapping's Jira field id, or null.</param>
public sealed record JqlLookup(
    IReadOnlyDictionary<string, IReadOnlyList<string>> AccountIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SprintIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> TeamIds,
    string? TeamField,
    string? StoryPointsField)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> None =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public static JqlLookup Empty { get; } = new(None, None, None, null, null);
}

/// <summary>The JQL, and every way it differs from the filter it came from (each once, in the order they arose).</summary>
public sealed record JqlTranslation(string Jql, IReadOnlyList<string> Warnings);
```

- [ ] **Step 4: Write the translator core**

Create `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.cs`:

```csharp
using System.Text;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// A filter as JQL: <c>project = "KEY" AND (tree) ORDER BY …</c>. Pure — the ids it needs come in
/// a <see cref="JqlLookup"/>, "now" from the clock — so every row of the spec's table is a unit test.
/// It expects a filter IssueFilterCompiler has already accepted (IssueJqlService checks first), yet
/// never throws on a shape it does not expect: that part is left out, with a warning.
///
/// A part JQL cannot express is LEFT OUT, never approximated silently, and the warning says which
/// way that moves the result (see <see cref="TranslateGroup"/>). Split by concern: this file walks
/// the tree and sorts; .Fields, .Lookups, .Dates and .History translate conditions.
/// </summary>
public sealed partial class JqlTranslator
{
    private readonly IClock _clock;

    /// <summary>The clock fixes the quarters, which JQL has no functions for (see .Dates).</summary>
    public JqlTranslator(IClock clock) => _clock = clock;

    public JqlTranslation Translate(
        string projectKey, FilterGroup? filter, string? sortBy, bool sortDescending, JqlLookup lookup)
    {
        var run = new Run(lookup);
        string? where = null;
        if (filter is not null && filter.Disabled != true)
        {
            var (clause, pending) = TranslateGroup(filter, run);
            where = clause;
            // Nothing of the filter is left: the project clause alone matches every issue in it.
            foreach (var what in pending) run.Warn(LeftOut(what, wider: true));
        }

        var jql = new StringBuilder("project = ").Append(JqlText.Quote(projectKey));
        if (where is not null) jql.Append(" AND ").Append(where);
        jql.Append(' ').Append(OrderBy(sortBy, sortDescending, run));
        return new JqlTranslation(jql.ToString(), run.Warnings);
    }

    /// <summary>Running state: the lookup, and the warnings in the order they arose, each once.</summary>
    private sealed class Run
    {
        public Run(JqlLookup lookup) => Lookup = lookup;

        public JqlLookup Lookup { get; }

        public List<string> Warnings { get; } = new();

        public void Warn(string message)
        {
            if (!Warnings.Contains(message)) Warnings.Add(message);
        }
    }

    /// <summary>A condition's JQL — or, when JQL cannot express it, why it is left out.</summary>
    private readonly record struct Clause(string? Jql, string? Why)
    {
        public static Clause Of(string jql) => new(jql, null);

        public static Clause Out(string why) => new(null, why);
    }

    /// <summary>
    /// The group's JQL, or null when nothing of it is left — then its left-out parts come back
    /// PENDING, because whether dropping them widens or narrows depends on the group holding this
    /// one. Where something is left, each part dropped here is settled by this group's own logic:
    /// under All a dropped part was a requirement (the JQL is wider), under Any an alternative
    /// (narrower). And and Or are monotone, so that direction holds all the way to the root.
    /// </summary>
    private (string? Jql, List<string> Pending) TranslateGroup(FilterGroup g, Run run)
    {
        var parts = new List<string>();
        var pending = new List<string>();
        foreach (var c in (g.Conditions ?? new List<FilterCondition>()).Where(c => c.Disabled != true))
        {
            var clause = TranslateCondition(c, run);
            if (clause.Jql is not null) parts.Add(clause.Jql);
            else pending.Add($"{Describe(c)} ({clause.Why})");
        }
        foreach (var child in (g.Groups ?? new List<FilterGroup>()).Where(x => x.Disabled != true))
        {
            var (jql, childPending) = TranslateGroup(child, run);
            if (jql is not null) parts.Add(jql);
            pending.AddRange(childPending);   // only a child with nothing left hands any back
        }
        if (parts.Count == 0) return (null, pending);

        foreach (var what in pending) run.Warn(LeftOut(what, wider: g.Logic == FilterLogic.And));
        return (JqlText.Join(parts, g.Logic == FilterLogic.Or ? "OR" : "AND"), new List<string>());
    }

    private static string LeftOut(string what, bool wider) => wider
        ? $"Left out {what}, so the JQL is wider than this filter: it can match issues this list does not show."
        : $"Left out {what}, so the JQL is narrower than this filter: it can miss issues this list shows.";

    private Clause TranslateCondition(FilterCondition c, Run run) => c.Field switch
    {
        "status" => Names("status", c),
        "issueType" => Names("issuetype", c),
        "priority" => Names("priority", c),
        "statusCategory" => Names("statusCategory", c),
        "summary" => Summary(c, run),
        "jiraKey" => Key(c),
        "parent" => Parent(c),
        _ => Clause.Out($"JQL has no field for '{c.Field}'"),
    };

    /// <summary>A condition as the warnings name it: "assignee Eq Ghost", "history ChangeCount of Sprint".</summary>
    private static string Describe(FilterCondition c)
    {
        var shown = new List<string>();
        if (c.Value is { ValueKind: not JsonValueKind.Null } v) shown.Add(Show(v));
        if (c.Values is { } vs) shown.AddRange(vs.Select(Show));
        return shown.Count == 0 ? $"\"{c.Field} {c.Op}\"" : $"\"{c.Field} {c.Op} {string.Join(", ", shown)}\"";
    }

    private static string Show(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => "(Empty)",
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Object when e.TryGetProperty("historyField", out var hf) && hf.ValueKind == JsonValueKind.String
            => $"of {hf.GetString()}",
        _ => e.GetRawText(),
    };

    /// <summary>The list's sort columns JQL orders by the same field (team and storyPoints go through their mappings).</summary>
    private static readonly IReadOnlyDictionary<string, string> SortFieldsJql = new Dictionary<string, string>
    {
        ["jiraKey"] = "key", ["summary"] = "summary", ["status"] = "status", ["issueType"] = "issuetype",
        ["priority"] = "priority", ["assignee"] = "assignee", ["sprint"] = "sprint",
        ["createdAt"] = "created", ["updatedAt"] = "updated",
    };

    /// <summary>
    /// <c>ORDER BY field dir, key ASC</c> — the list's own tiebreaker — or by key alone when JQL
    /// cannot sort by the list's column.
    /// </summary>
    private static string OrderBy(string? sortBy, bool descending, Run run)
    {
        var name = sortBy ?? SortFields.Default;
        var field = name switch
        {
            "team" => JqlText.CustomField(run.Lookup.TeamField),
            "storyPoints" => JqlText.CustomField(run.Lookup.StoryPointsField),
            _ => SortFieldsJql.GetValueOrDefault(name),
        };
        if (field is null)
        {
            run.Warn($"The JQL cannot sort by '{name}', so it sorts by key.");
            return "ORDER BY key ASC";
        }
        var direction = descending ? "DESC" : "ASC";
        return field == "key" ? $"ORDER BY key {direction}" : $"ORDER BY {field} {direction}, key ASC";
    }
}
```

Create `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.Fields.cs`:

```csharp
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>The fields JQL names as the filter does: status, type, priority, category by name; key; summary; parent.</summary>
public sealed partial class JqlTranslator
{
    /// <summary>What a condition named: strings, whether (Empty) was among them, and smart sprint keys.</summary>
    private sealed record Pick(List<string> Names, bool WantsEmpty, List<string> Smart);

    /// <summary><c>value</c> for Eq/Neq, <c>values</c> for In/NotIn; null = (Empty); <c>{"smart": key}</c> = a smart sprint value.</summary>
    private static Pick ReadPick(FilterCondition c)
    {
        var names = new List<string>();
        var smart = new List<string>();
        var wantsEmpty = false;
        IEnumerable<JsonElement> items = c.Op is FilterOperator.In or FilterOperator.NotIn
            ? c.Values ?? new List<JsonElement>()
            : c.Value is { } v ? new[] { v } : Array.Empty<JsonElement>();
        foreach (var e in items)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String:
                    if (!names.Contains(e.GetString()!)) names.Add(e.GetString()!);
                    break;
                case JsonValueKind.Null:
                    wantsEmpty = true;
                    break;
                case JsonValueKind.Object when e.TryGetProperty("smart", out var k) && k.ValueKind == JsonValueKind.String:
                    if (!smart.Contains(k.GetString()!)) smart.Add(k.GetString()!);
                    break;
            }
        }
        return new Pick(names, wantsEmpty, smart);
    }

    /// <summary>
    /// Every dictionary-like condition once its values are JQL literals. Is / is any of: the list,
    /// any <paramref name="extra"/> alternatives (smart sprints) and (Empty), OR-ed — with nothing at
    /// all to match it is left out with <paramref name="whyNone"/> (JQL has no "false"). Is not / is
    /// none of: the exclusions (and <paramref name="extra"/>) AND-ed. JQL's != and not in already
    /// leave empty values out, which is this list's rule too — so with nothing to exclude they mean
    /// "has a value", exactly as the list reads them.
    /// </summary>
    private static Clause Dictionary(
        string field, FilterOperator op, IReadOnlyList<string> literals, bool wantsEmpty,
        IReadOnlyList<string> extra, string whyNone)
    {
        var parts = new List<string>();
        switch (op)
        {
            case FilterOperator.IsNull:
                return Clause.Of($"{field} is EMPTY");
            case FilterOperator.IsNotNull:
                return Clause.Of($"{field} is not EMPTY");
            case FilterOperator.Eq or FilterOperator.In:
                if (literals.Count == 1 && op == FilterOperator.Eq) parts.Add($"{field} = {literals[0]}");
                else if (literals.Count > 0) parts.Add($"{field} in {JqlText.List(literals)}");
                parts.AddRange(extra);
                if (wantsEmpty) parts.Add($"{field} is EMPTY");
                return parts.Count == 0 ? Clause.Out(whyNone) : Clause.Of(JqlText.Join(parts, "OR"));
            case FilterOperator.Neq or FilterOperator.NotIn:
                if (literals.Count == 1 && op == FilterOperator.Neq) parts.Add($"{field} != {literals[0]}");
                else if (literals.Count > 0) parts.Add($"{field} not in {JqlText.List(literals)}");
                parts.AddRange(extra);
                return Clause.Of(parts.Count == 0 ? $"{field} is not EMPTY" : JqlText.Join(parts, "AND"));
            default:
                return Clause.Out($"JQL has no '{op}' for {field}");
        }
    }

    /// <summary>status, issuetype, priority, statusCategory: by name, as Jira matches them.</summary>
    private static Clause Names(string field, FilterCondition c)
    {
        var pick = ReadPick(c);
        return Dictionary(field, c.Op, pick.Names.Select(JqlText.Quote).ToList(), pick.WantsEmpty,
            Array.Empty<string>(), "it names no value");
    }

    private const string TextSearchWarning =
        "JQL text search is word-based, not substring: summary ~ matches whole words, so it can find more or fewer issues than \"contains\" does here.";

    /// <summary>summary: ~ for contains and starts with (a trailing *), NOT … ~ for does not contain.</summary>
    private static Clause Summary(FilterCondition c, Run run)
    {
        if (c.Op == FilterOperator.IsNull) return Clause.Of("summary is EMPTY");
        if (c.Op == FilterOperator.IsNotNull) return Clause.Of("summary is not EMPTY");
        if (c.Value is not { ValueKind: JsonValueKind.String } v) return Clause.Out("it has no text to search for");
        var text = v.GetString()!;
        var clause = c.Op switch
        {
            FilterOperator.Contains => Clause.Of($"summary ~ {JqlText.Quote(text)}"),
            FilterOperator.NotContains => Clause.Of($"NOT summary ~ {JqlText.Quote(text)}"),
            FilterOperator.StartsWith => Clause.Of($"summary ~ {JqlText.Quote(text + "*")}"),
            _ => Clause.Out($"JQL has no '{c.Op}' for summary"),
        };
        if (clause.Jql is not null) run.Warn(TextSearchWarning);
        return clause;
    }

    /// <summary>jiraKey: whole keys only — JQL cannot match part of one.</summary>
    private static Clause Key(FilterCondition c)
    {
        if (c.Op is FilterOperator.Contains or FilterOperator.StartsWith)
            return Clause.Out("JQL cannot match part of an issue key");
        var keys = ReadPick(c).Names.Select(JqlText.Quote).ToList();
        if (keys.Count == 0) return Clause.Out("it names no key");
        return c.Op switch
        {
            FilterOperator.Eq => Clause.Of($"key = {keys[0]}"),
            FilterOperator.In => Clause.Of($"key in {JqlText.List(keys)}"),
            FilterOperator.NotIn => Clause.Of($"key not in {JqlText.List(keys)}"),
            _ => Clause.Out($"JQL has no '{c.Op}' for key"),
        };
    }

    /// <summary>parent: by the parent's Jira issue id, which is what the filter holds.</summary>
    private static Clause Parent(FilterCondition c) => c.Op switch
    {
        FilterOperator.IsNull => Clause.Of("parent is EMPTY"),
        FilterOperator.IsNotNull => Clause.Of("parent is not EMPTY"),
        FilterOperator.Eq when c.Value is { ValueKind: JsonValueKind.String } v => Clause.Of($"parent = {JqlText.Id(v.GetString()!)}"),
        _ => Clause.Out("it names no parent"),
    };
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~JqlTranslatorTests"`
Expected: PASS (all tests in `JqlTranslatorTests`).

- [ ] **Step 6: Commit**

```bash
git -C C:/jw-filters add src/JiraUnpacked.Infrastructure/Reporting/Jql tests/JiraUnpacked.Tests/Reporting/JqlTestKit.cs tests/JiraUnpacked.Tests/Reporting/JqlTranslatorTests.cs
git -C C:/jw-filters commit -m "feat(jql): translator core — tree, left-out direction, sort, names, key, summary, parent" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Assignee, sprint (smart values), team and story points (server, pure)

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.Lookups.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.cs` (the `TranslateCondition` switch)
- Create: `tests/JiraUnpacked.Tests/Reporting/JqlLookupFieldTests.cs`

**Interfaces:**
- Consumes (Task 1): `Run`, `Clause`, `ReadPick`, `Pick`, `Dictionary(...)`, `JqlText.Quote/Id/Number/Join/CustomField`, `JqlLookup`; `JqlTestKit` (`C`, `L`, `All`, `Ids`, `Translate`, `Where`).
- Produces: arms `"assignee"`, `"sprint"`, `"team"`, `"storyPoints"` in `TranslateCondition`; `Resolve(IReadOnlyList<string> names, IReadOnlyDictionary<string, IReadOnlyList<string>> ids, Func<string, string> literal, string what, Run run)`; `Mapped(string? jiraFieldId, string role)` → `(string? Field, string? Why)`; `Number(List<JsonElement>? values, int at)` → `decimal?`.

- [ ] **Step 1: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Reporting/JqlLookupFieldTests.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Jql;
using static JiraUnpacked.Tests.Reporting.JqlTestKit;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>The fields JQL names by id or by custom field, through a lookup like the one IssueJqlService loads.</summary>
public sealed class JqlLookupFieldTests
{
    private static readonly JqlLookup Lookup = new(
        Ids(("Alice", new[] { "acc-alice" }), ("Sam", new[] { "acc-sam-1", "acc-sam-2" })),
        Ids(("Sprint 7", new[] { "107" }), ("Sprint 8", new[] { "108" })),
        Ids(("Core", new[] { "team-1" })),
        "customfield_10001",
        "customfield_10016");

    private static object Smart(string key) => new { smart = key };

    [Fact]
    public void Assignee_is_named_by_account_id()
    {
        Assert.Equal("assignee = \"acc-alice\"", Where(C("assignee", FilterOperator.Eq, "Alice"), Lookup));
        // Two accounts share the name: the filter matches both, so the JQL lists both.
        Assert.Equal("assignee in (\"acc-sam-1\", \"acc-sam-2\")", Where(C("assignee", FilterOperator.Eq, "Sam"), Lookup));
        Assert.Equal("(assignee in (\"acc-alice\") OR assignee is EMPTY)", Where(L("assignee", FilterOperator.In, "Alice", null), Lookup));
        Assert.Equal("assignee != \"acc-alice\"", Where(C("assignee", FilterOperator.Neq, "Alice"), Lookup));
        Assert.Equal("assignee not in (\"acc-alice\", \"acc-sam-1\", \"acc-sam-2\")",
            Where(L("assignee", FilterOperator.NotIn, "Alice", "Sam"), Lookup));
        Assert.Equal("assignee is EMPTY", Where(C("assignee", FilterOperator.IsNull), Lookup));
    }

    [Fact]
    public void A_name_with_no_account_is_left_out_of_the_list_with_a_warning()
    {
        var t = Translate(All(L("assignee", FilterOperator.In, "Alice", "Ghost")), Lookup);
        Assert.Equal("assignee in (\"acc-alice\")", Where(t));
        Assert.Equal(new[]
        {
            "No Jira account in this project's data is named 'Ghost'; this list matches no issue by that name either, so the JQL leaves it out.",
        }, t.Warnings);

        // "is not Ghost" excludes nobody, here and in Jira: it means "has an assignee".
        Assert.Equal("assignee is not EMPTY", Where(C("assignee", FilterOperator.Neq, "Ghost"), Lookup));
    }

    [Fact]
    public void A_condition_on_a_name_with_no_account_is_left_out()
    {
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"), C("assignee", FilterOperator.Eq, "Ghost")), Lookup);
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains(t.Warnings, w =>
            w.StartsWith("Left out \"assignee Eq Ghost\" (no Jira account in this project's data has that name)", StringComparison.Ordinal)
            && w.Contains("wider", StringComparison.Ordinal));
    }

    [Fact]
    public void Sprint_is_named_by_id_and_smart_values_by_sprint_functions()
    {
        Assert.Equal("sprint = 107", Where(C("sprint", FilterOperator.Eq, "Sprint 7"), Lookup));
        Assert.Equal("sprint in openSprints()", Where(C("sprint", FilterOperator.Eq, Smart("open")), Lookup));
        Assert.Equal("sprint in futureSprints()", Where(C("sprint", FilterOperator.Eq, Smart("future")), Lookup));
        Assert.Equal("sprint in closedSprints()", Where(C("sprint", FilterOperator.Eq, Smart("closed")), Lookup));
        Assert.Equal("(sprint in openSprints() AND sprint not in futureSprints())",
            Where(C("sprint", FilterOperator.Eq, Smart("active")), Lookup));
        Assert.Equal("(sprint in (107) OR (sprint in openSprints() AND sprint not in futureSprints()) OR sprint is EMPTY)",
            Where(L("sprint", FilterOperator.In, "Sprint 7", Smart("active"), null), Lookup));
        Assert.Equal("(sprint not in openSprints() OR sprint in futureSprints())",
            Where(C("sprint", FilterOperator.Neq, Smart("active")), Lookup));
        Assert.Equal("(sprint not in (108) AND sprint not in closedSprints())",
            Where(L("sprint", FilterOperator.NotIn, "Sprint 8", Smart("closed")), Lookup));
        Assert.Equal("sprint is EMPTY", Where(C("sprint", FilterOperator.IsNull), Lookup));
    }

    [Fact]
    public void Sprint_conditions_warn_once_that_jira_matches_every_sprint_an_issue_was_in()
    {
        var t = Translate(All(C("sprint", FilterOperator.Eq, "Sprint 7"), C("sprint", FilterOperator.Neq, "Sprint 8")), Lookup);
        Assert.Contains("every sprint it has been in", Assert.Single(t.Warnings));
        Assert.Empty(Translate(All(C("sprint", FilterOperator.IsNull)), Lookup).Warnings);
    }

    [Fact]
    public void Team_uses_the_mapped_custom_field_and_team_ids()
    {
        Assert.Equal("cf[10001] = \"team-1\"", Where(C("team", FilterOperator.Eq, "Core"), Lookup));
        Assert.Equal("cf[10001] is EMPTY", Where(C("team", FilterOperator.IsNull), Lookup));

        var unmapped = Translate(All(C("status", FilterOperator.Eq, "Done"), C("team", FilterOperator.Eq, "Core")),
            Lookup with { TeamField = null });
        Assert.Equal("status = \"Done\"", Where(unmapped));
        Assert.Contains("(no Team field is mapped for this project)", Assert.Single(unmapped.Warnings));

        var notCustom = Translate(All(C("status", FilterOperator.Eq, "Done"), C("team", FilterOperator.Eq, "Core")),
            Lookup with { TeamField = "team" });
        Assert.Contains("the Team mapping 'team' is not a custom field JQL can name", Assert.Single(notCustom.Warnings));
    }

    [Fact]
    public void Story_points_use_the_mapped_custom_field()
    {
        Assert.Equal("cf[10016] = 3", Where(C("storyPoints", FilterOperator.Eq, 3), Lookup));
        Assert.Equal("cf[10016] != 2.5", Where(C("storyPoints", FilterOperator.Neq, 2.5m), Lookup));
        Assert.Equal("cf[10016] < 3", Where(C("storyPoints", FilterOperator.Lt, 3), Lookup));
        Assert.Equal("cf[10016] <= 3", Where(C("storyPoints", FilterOperator.Lte, 3), Lookup));
        Assert.Equal("cf[10016] > 3", Where(C("storyPoints", FilterOperator.Gt, 3), Lookup));
        Assert.Equal("cf[10016] >= 3", Where(C("storyPoints", FilterOperator.Gte, 3), Lookup));
        Assert.Equal("(cf[10016] >= 1 AND cf[10016] <= 3)", Where(L("storyPoints", FilterOperator.Between, 1, 3), Lookup));
        Assert.Equal("cf[10016] <= 3", Where(L("storyPoints", FilterOperator.Between, null, 3), Lookup));
        Assert.Equal("cf[10016] is not EMPTY", Where(C("storyPoints", FilterOperator.IsNotNull), Lookup));

        var unmapped = Translate(All(C("storyPoints", FilterOperator.Eq, 3)), Lookup with { StoryPointsField = null });
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", unmapped.Jql);
        Assert.Contains("(no Story Points field is mapped for this project)", Assert.Single(unmapped.Warnings));
    }

    [Fact]
    public void Team_and_story_points_sort_by_their_mapped_fields()
    {
        Assert.EndsWith(" ORDER BY cf[10001] DESC, key ASC", Translate(null, Lookup, "team").Jql);
        Assert.EndsWith(" ORDER BY cf[10016] ASC, key ASC", Translate(null, Lookup, "storyPoints", false).Jql);
        var t = Translate(null, Lookup with { StoryPointsField = null }, "storyPoints");
        Assert.EndsWith(" ORDER BY key ASC", t.Jql);
        Assert.Equal(new[] { "The JQL cannot sort by 'storyPoints', so it sorts by key." }, t.Warnings);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~JqlLookupFieldTests"`
Expected: FAIL — e.g. `Assert.StartsWith() Failure` in `Where` (assignee/sprint/team/storyPoints are still "JQL has no field for …" and left out). `Team_and_story_points_sort_by_their_mapped_fields` passes already (sorting by mapping is Task 1's).

- [ ] **Step 3: Write the lookup-backed fields**

Create `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.Lookups.cs`:

```csharp
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>assignee, sprint, team and story points: the fields JQL names by id or by custom field.</summary>
public sealed partial class JqlTranslator
{
    private const string SprintWarning =
        "Jira matches an issue to every sprint it has been in; this list matches only its latest sprint, so sprint conditions can match more issues in Jira.";

    /// <summary>
    /// names → literal ids through <paramref name="ids"/>, every id a name has. A name with no id
    /// matches no issue in this list either (the filter resolves names the same way, and this
    /// project's issues only point at this project's rows), so leaving it out of the JQL keeps the
    /// meaning — the warning still says so, because someone typed it.
    /// </summary>
    private static List<string> Resolve(
        IReadOnlyList<string> names, IReadOnlyDictionary<string, IReadOnlyList<string>> ids,
        Func<string, string> literal, string what, Run run)
    {
        var literals = new List<string>();
        var unknown = new List<string>();
        foreach (var name in names)
        {
            if (ids.TryGetValue(name, out var found) && found.Count > 0)
            {
                foreach (var id in found)
                {
                    var l = literal(id);
                    if (!literals.Contains(l)) literals.Add(l);
                }
            }
            else
            {
                unknown.Add(name);
            }
        }
        if (unknown.Count > 0)
            run.Warn($"No {what} in this project's data is named {string.Join(", ", unknown.Select(n => $"'{n}'"))}; this list matches no issue by that name either, so the JQL leaves it out.");
        return literals;
    }

    /// <summary>assignee: by account id — the only stable way JQL names a user.</summary>
    private static Clause Assignee(FilterCondition c, Run run)
    {
        var pick = ReadPick(c);
        var ids = Resolve(pick.Names, run.Lookup.AccountIds, JqlText.Quote, "Jira account", run);
        return Dictionary("assignee", c.Op, ids, pick.WantsEmpty, Array.Empty<string>(),
            "no Jira account in this project's data has that name");
    }

    /// <summary>The JQL for each smart sprint value, and for "not" it (empty excluded, as the list's rule says).</summary>
    private static readonly IReadOnlyDictionary<string, (string Is, string IsNot)> SmartSprintJql =
        new Dictionary<string, (string, string)>
        {
            ["active"] = ("(sprint in openSprints() AND sprint not in futureSprints())",
                          "(sprint not in openSprints() OR sprint in futureSprints())"),
            ["future"] = ("sprint in futureSprints()", "sprint not in futureSprints()"),
            ["open"] = ("sprint in openSprints()", "sprint not in openSprints()"),
            ["closed"] = ("sprint in closedSprints()", "sprint not in closedSprints()"),
        };

    /// <summary>sprint: names by Jira sprint id, smart values by Jira's sprint functions.</summary>
    private static Clause Sprint(FilterCondition c, Run run)
    {
        var pick = ReadPick(c);
        var ids = Resolve(pick.Names, run.Lookup.SprintIds, JqlText.Id, "sprint", run);
        var negative = c.Op is FilterOperator.Neq or FilterOperator.NotIn;
        var smart = pick.Smart.Where(SmartSprintJql.ContainsKey)
            .Select(k => negative ? SmartSprintJql[k].IsNot : SmartSprintJql[k].Is)
            .ToList();
        var clause = Dictionary("sprint", c.Op, ids, pick.WantsEmpty, smart, "no sprint in this project has that name");
        if (clause.Jql is not null && c.Op is not (FilterOperator.IsNull or FilterOperator.IsNotNull))
            run.Warn(SprintWarning);
        return clause;
    }

    /// <summary>A mapped custom field as cf[N] — or why it cannot be named.</summary>
    private static (string? Field, string? Why) Mapped(string? jiraFieldId, string role) =>
        jiraFieldId is null ? (null, $"no {role} field is mapped for this project")
        : JqlText.CustomField(jiraFieldId) is { } cf ? (cf, null)
        : (null, $"the {role} mapping '{jiraFieldId}' is not a custom field JQL can name");

    /// <summary>team: the project's Team custom field, by Jira team id.</summary>
    private static Clause Team(FilterCondition c, Run run)
    {
        var (field, why) = Mapped(run.Lookup.TeamField, "Team");
        if (field is null) return Clause.Out(why!);
        var pick = ReadPick(c);
        var ids = Resolve(pick.Names, run.Lookup.TeamIds, JqlText.Quote, "team", run);
        return Dictionary(field, c.Op, ids, pick.WantsEmpty, Array.Empty<string>(), "no team in this project has that name");
    }

    /// <summary>storyPoints: the project's Story Points custom field; comparisons and Between as they are.</summary>
    private static Clause StoryPoints(FilterCondition c, Run run)
    {
        var (field, why) = Mapped(run.Lookup.StoryPointsField, "Story Points");
        if (field is null) return Clause.Out(why!);
        switch (c.Op)
        {
            case FilterOperator.IsNull:
                return Clause.Of($"{field} is EMPTY");
            case FilterOperator.IsNotNull:
                return Clause.Of($"{field} is not EMPTY");
            case FilterOperator.Between:
            {
                var parts = new List<string>();
                if (Number(c.Values, 0) is { } lo) parts.Add($"{field} >= {JqlText.Number(lo)}");
                if (Number(c.Values, 1) is { } hi) parts.Add($"{field} <= {JqlText.Number(hi)}");
                return parts.Count == 0 ? Clause.Out("it has no bounds") : Clause.Of(JqlText.Join(parts, "AND"));
            }
        }
        if (c.Value is not { ValueKind: JsonValueKind.Number } v || !v.TryGetDecimal(out var d))
            return Clause.Out("it has no number to compare with");
        var symbol = c.Op switch
        {
            FilterOperator.Eq => "=",
            FilterOperator.Neq => "!=",
            FilterOperator.Lt => "<",
            FilterOperator.Lte => "<=",
            FilterOperator.Gt => ">",
            FilterOperator.Gte => ">=",
            _ => null,
        };
        return symbol is null
            ? Clause.Out($"JQL has no '{c.Op}' for story points")
            : Clause.Of($"{field} {symbol} {JqlText.Number(d)}");
    }

    private static decimal? Number(List<JsonElement>? values, int at) =>
        values is { } vs && vs.Count > at && vs[at].ValueKind == JsonValueKind.Number && vs[at].TryGetDecimal(out var d)
            ? d
            : null;
}
```

In `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.cs`, replace:

```csharp
        "statusCategory" => Names("statusCategory", c),
        "summary" => Summary(c, run),
```

with:

```csharp
        "statusCategory" => Names("statusCategory", c),
        "assignee" => Assignee(c, run),
        "sprint" => Sprint(c, run),
        "team" => Team(c, run),
        "storyPoints" => StoryPoints(c, run),
        "summary" => Summary(c, run),
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~JqlLookupFieldTests|FullyQualifiedName~JqlTranslatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git -C C:/jw-filters add src/JiraUnpacked.Infrastructure/Reporting/Jql tests/JiraUnpacked.Tests/Reporting/JqlLookupFieldTests.cs
git -C C:/jw-filters commit -m "feat(jql): assignee by account id, sprints by id and sprint functions, mapped team and story points" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Dates — comparisons, Between, relative amounts, calendar periods and quarters (server, pure)

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.Dates.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.cs` (the `TranslateCondition` switch)
- Create: `tests/JiraUnpacked.Tests/Reporting/JqlDateTests.cs`

**Interfaces:**
- Consumes (Task 1): `Run`, `Clause`, `JqlText.Date/Join`, `_clock`; `RelativeDates.Periods`, `RelativeDates.TryResolveZone(string?, out TimeZoneInfo)`, `RelativeDates.Period(DateTime nowUtc, string period, TimeZoneInfo zone)` (internal, `Reporting.Filtering`); `JqlTestKit` (`Now` = 2026-10-15 12:00 UTC).
- Produces (used by Task 4's windows): `ReadRange(JsonElement? from, JsonElement? to)` → `(DateTime? From, DateTime? To, bool ToExclusive)`; `TryReadDate(JsonElement, out DateTime, out bool dateOnly)`; `Relative(JsonElement? value, Run run)` → `string?` ("-14d"); `Period(JsonElement? value, Run run)` → `PeriodBounds?` (`Start`, `End`, `EndInclusive`); constants `MonthsWarning`, `PeriodWarning`.

- [ ] **Step 1: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Reporting/JqlDateTests.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using static JiraUnpacked.Tests.Reporting.JqlTestKit;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>Date conditions as JQL, the translator's clock pinned to Thursday 15 Oct 2026, noon UTC.</summary>
public sealed class JqlDateTests
{
    [Fact]
    public void Comparisons_write_utc_dates_with_a_time_only_when_there_is_one()
    {
        Assert.Equal("created < \"2026-09-01\"", Where(C("createdAt", FilterOperator.Lt, "2026-09-01")));
        Assert.Equal("created >= \"2026-09-01 10:30\"", Where(C("createdAt", FilterOperator.Gte, "2026-09-01T10:30:00Z")));
        Assert.Equal("updated <= \"2026-09-01 08:30\"", Where(C("updatedAt", FilterOperator.Lte, "2026-09-01T10:30:00+02:00")));
        Assert.Equal("updated > \"2026-09-01\"", Where(C("updatedAt", FilterOperator.Gt, "2026-09-01")));
    }

    [Fact]
    public void Between_runs_through_a_date_only_end_and_either_bound_may_be_open()
    {
        Assert.Equal("(created >= \"2026-09-01\" AND created < \"2026-10-01\")",
            Where(L("createdAt", FilterOperator.Between, "2026-09-01", "2026-09-30")));
        Assert.Equal("created >= \"2026-09-01 08:00\"", Where(L("createdAt", FilterOperator.Between, "2026-09-01T08:00:00Z", null)));
        Assert.Equal("created <= \"2026-09-30 18:00\"", Where(L("createdAt", FilterOperator.Between, null, "2026-09-30T18:00:00Z")));
        // Through the last day a date can hold excludes nothing: every issue, as the list reads it.
        Assert.Equal("created is not EMPTY", Where(L("createdAt", FilterOperator.Between, null, "9999-12-31")));
    }

    [Fact]
    public void In_the_last_and_more_than_ago_are_relative_offsets()
    {
        Assert.Equal("created >= -14d", Where(C("createdAt", FilterOperator.InLast, new { amount = 14, unit = "days" })));
        Assert.Equal("created >= -3h", Where(C("createdAt", FilterOperator.InLast, new { amount = 3, unit = "hours" })));
        Assert.Equal("updated >= -2w", Where(C("updatedAt", FilterOperator.InLast, new { amount = 2, unit = "weeks" })));
        Assert.Equal("updated < -14d", Where(C("updatedAt", FilterOperator.NotInLast, new { amount = 14, unit = "days" })));
        Assert.Empty(Translate(All(C("createdAt", FilterOperator.InLast, new { amount = 14, unit = "days" }))).Warnings);
    }

    [Fact]
    public void Months_are_approximated_as_thirty_days_and_say_so()
    {
        var t = Translate(All(C("createdAt", FilterOperator.InLast, new { amount = 2, unit = "months" })));
        Assert.Equal("created >= -60d", Where(t));
        Assert.Equal(new[] { "JQL has no month unit for relative dates, so months are approximated as 30 days." }, t.Warnings);
    }

    [Fact]
    public void Calendar_periods_use_jiras_start_and_end_functions_with_one_warning()
    {
        var t = Translate(All(
            C("createdAt", FilterOperator.InPeriod, new { period = "thisWeek", timeZone = "Europe/Warsaw" }),
            C("updatedAt", FilterOperator.InPeriod, new { period = "lastMonth" })));
        Assert.Equal(
            "((created >= startOfWeek() AND created <= endOfWeek()) AND (updated >= startOfMonth(-1) AND updated <= endOfMonth(-1)))",
            Where(t));
        Assert.Contains("Jira user's own time zone", Assert.Single(t.Warnings));

        Assert.Equal("(created >= startOfDay() AND created <= endOfDay())",
            Where(C("createdAt", FilterOperator.InPeriod, new { period = "today" })));
        Assert.Equal("(created >= startOfDay(-1) AND created <= endOfDay(-1))",
            Where(C("createdAt", FilterOperator.InPeriod, new { period = "yesterday" })));
        Assert.Equal("(created >= startOfYear(-1) AND created <= endOfYear(-1))",
            Where(C("createdAt", FilterOperator.InPeriod, new { period = "lastYear" })));
    }

    [Fact]
    public void Quarters_are_fixed_dates_from_the_server_clock_in_the_conditions_zone()
    {
        var t = Translate(All(C("createdAt", FilterOperator.InPeriod, new { period = "lastQuarter" })));
        Assert.Equal("(created >= \"2026-07-01\" AND created < \"2026-10-01\")", Where(t));
        Assert.Equal(new[]
        {
            "JQL has no quarter functions, so last quarter is written as fixed dates from \"2026-07-01\" to \"2026-10-01\": the JQL will not move with the calendar.",
        }, t.Warnings);

        // Warsaw is UTC+2 on 1 Oct and UTC+1 on 1 Jan.
        Assert.Equal("(created >= \"2026-09-30 22:00\" AND created < \"2026-12-31 23:00\")",
            Where(C("createdAt", FilterOperator.InPeriod, new { period = "thisQuarter", timeZone = "Europe/Warsaw" })));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~JqlDateTests"`
Expected: FAIL — `Assert.StartsWith() Failure` (createdAt/updatedAt are still left out).

- [ ] **Step 3: Write the date translation**

Create `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.Dates.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// Dates: comparisons, Between, relative amounts and calendar periods. The readers here are shared
/// with history windows (.History), so a window reads exactly as a date condition does.
/// </summary>
public sealed partial class JqlTranslator
{
    private const string MonthsWarning = "JQL has no month unit for relative dates, so months are approximated as 30 days.";

    private const string PeriodWarning =
        "Jira evaluates calendar periods (startOfWeek() and the like) in the Jira user's own time zone, and starts weeks on their locale's first day; this list uses the condition's time zone and Monday.";

    private static readonly Regex DateOnlyShape = new(@"^\d{4}-\d{2}-\d{2}\z", RegexOptions.CultureInvariant);

    /// <summary>JQL's start/end functions for every calendar period but the quarters, which JQL has none for.</summary>
    private static readonly IReadOnlyDictionary<string, (string Unit, int Offset)> PeriodFunctions =
        new Dictionary<string, (string, int)>
        {
            ["today"] = ("Day", 0), ["yesterday"] = ("Day", -1),
            ["thisWeek"] = ("Week", 0), ["lastWeek"] = ("Week", -1),
            ["thisMonth"] = ("Month", 0), ["lastMonth"] = ("Month", -1),
            ["thisYear"] = ("Year", 0), ["lastYear"] = ("Year", -1),
        };

    /// <summary>A calendar period's JQL bounds: functions where JQL has them (end inclusive), fixed dates for quarters (end exclusive).</summary>
    private sealed record PeriodBounds(string Start, string End, bool EndInclusive);

    /// <summary>created / updated.</summary>
    private Clause DateField(string field, FilterCondition c, Run run)
    {
        switch (c.Op)
        {
            case FilterOperator.Between:
            {
                var (from, to, toExclusive) = ReadRange(Bound(c.Values, 0), Bound(c.Values, 1));
                var parts = new List<string>();
                if (from is { } lo) parts.Add($"{field} >= {JqlText.Date(lo)}");
                if (to is { } hi) parts.Add($"{field} {(toExclusive ? "<" : "<=")} {JqlText.Date(hi)}");
                // Both ends open matches every issue: a clause no issue fails says exactly that.
                return Clause.Of(parts.Count == 0 ? $"{field} is not EMPTY" : JqlText.Join(parts, "AND"));
            }
            case FilterOperator.InLast or FilterOperator.NotInLast:
                return Relative(c.Value, run) is { } offset
                    ? Clause.Of($"{field} {(c.Op == FilterOperator.InLast ? ">=" : "<")} {offset}")
                    : Clause.Out("it has no amount");
            case FilterOperator.InPeriod:
                return Period(c.Value, run) is { } p
                    ? Clause.Of($"({field} >= {p.Start} AND {field} {(p.EndInclusive ? "<=" : "<")} {p.End})")
                    : Clause.Out("it has no period");
        }
        if (c.Value is not { } v || !TryReadDate(v, out var at, out _)) return Clause.Out("it has no date");
        var symbol = c.Op switch
        {
            FilterOperator.Lt => "<",
            FilterOperator.Lte => "<=",
            FilterOperator.Gt => ">",
            FilterOperator.Gte => ">=",
            _ => null,
        };
        return symbol is null ? Clause.Out($"JQL has no '{c.Op}' for {field}") : Clause.Of($"{field} {symbol} {JqlText.Date(at)}");
    }

    private static JsonElement? Bound(List<JsonElement>? values, int at) =>
        values is { } vs && vs.Count > at && vs[at].ValueKind != JsonValueKind.Null ? vs[at] : null;

    /// <summary>As the filter reads a range: UTC; a date-only `to` runs through that day (exclusive at the next midnight).</summary>
    private static (DateTime? From, DateTime? To, bool ToExclusive) ReadRange(JsonElement? fromEl, JsonElement? toEl)
    {
        DateTime? from = fromEl is { } f && TryReadDate(f, out var a, out _) ? a : null;
        if (toEl is not { } t || !TryReadDate(t, out var b, out var dateOnly)) return (from, null, false);
        // Through 9999-12-31 excludes nothing and has no next midnight: an open end, as the filter reads it.
        if (dateOnly && b.Date == DateTime.MaxValue.Date) return (from, null, false);
        return dateOnly ? (from, b.AddDays(1), true) : (from, b, false);
    }

    /// <summary>An ISO-8601 date or timestamp, read as UTC like every date in the filter.</summary>
    private static bool TryReadDate(JsonElement v, out DateTime value, out bool dateOnly)
    {
        value = default;
        dateOnly = false;
        if (v.ValueKind != JsonValueKind.String) return false;
        var s = v.GetString()!;
        dateOnly = DateOnlyShape.IsMatch(s);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
    }

    /// <summary>{"amount", "unit"} → a JQL relative offset ("-14d"); months as 30-day blocks, with a warning.</summary>
    private static string? Relative(JsonElement? value, Run run)
    {
        if (value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number || !a.TryGetInt32(out var n)
            || !v.TryGetProperty("unit", out var u) || u.ValueKind != JsonValueKind.String)
            return null;
        switch (u.GetString())
        {
            case "hours":
                return $"-{n}h";
            case "days":
                return $"-{n}d";
            case "weeks":
                return $"-{n}w";
            case "months":
                run.Warn(MonthsWarning);
                return $"-{n * 30}d";
            default:
                return null;
        }
    }

    /// <summary>{"period", "timeZone"?} → JQL bounds. Quarters are computed now, in the condition's zone, exactly as the filter computes them.</summary>
    private PeriodBounds? Period(JsonElement? value, Run run)
    {
        if (value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("period", out var pe) || pe.ValueKind != JsonValueKind.String)
            return null;
        var period = pe.GetString()!;
        if (PeriodFunctions.TryGetValue(period, out var fn))
        {
            run.Warn(PeriodWarning);
            var arg = fn.Offset == 0 ? "" : fn.Offset.ToString(CultureInfo.InvariantCulture);
            return new PeriodBounds($"startOf{fn.Unit}({arg})", $"endOf{fn.Unit}({arg})", EndInclusive: true);
        }
        if (!RelativeDates.Periods.Contains(period)) return null;

        var zoneId = v.TryGetProperty("timeZone", out var tz) && tz.ValueKind == JsonValueKind.String ? tz.GetString() : null;
        if (!RelativeDates.TryResolveZone(zoneId, out var zone)) return null;
        var (start, end) = RelativeDates.Period(_clock.UtcNow, period, zone);
        run.Warn($"JQL has no quarter functions, so {(period == "thisQuarter" ? "this" : "last")} quarter is written as fixed dates from {JqlText.Date(start)} to {JqlText.Date(end)}: the JQL will not move with the calendar.");
        return new PeriodBounds(JqlText.Date(start), JqlText.Date(end), EndInclusive: false);
    }
}
```

In `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.cs`, replace:

```csharp
        "storyPoints" => StoryPoints(c, run),
        "summary" => Summary(c, run),
```

with:

```csharp
        "storyPoints" => StoryPoints(c, run),
        "createdAt" => DateField("created", c, run),
        "updatedAt" => DateField("updated", c, run),
        "summary" => Summary(c, run),
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~JqlDateTests|FullyQualifiedName~JqlLookupFieldTests|FullyQualifiedName~JqlTranslatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git -C C:/jw-filters add src/JiraUnpacked.Infrastructure/Reporting/Jql tests/JiraUnpacked.Tests/Reporting/JqlDateTests.cs
git -C C:/jw-filters commit -m "feat(jql): dates — comparisons, Between, relative offsets, period functions, quarters as fixed dates" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: History — WAS, CHANGED and windows; number of changes left out (server, pure)

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.History.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.cs` (the `TranslateCondition` switch)
- Create: `tests/JiraUnpacked.Tests/Reporting/JqlHistoryTests.cs`

**Interfaces:**
- Consumes (Tasks 1, 3): `Run`, `Clause`, `JqlText.Quote/List/Join/Date`, `JqlLookup.AccountIds`, `ReadRange`, `Relative`, `Period`, `PeriodBounds`; `JqlTestKit`.
- Produces: arm `"history"` in `TranslateCondition`; `internal static IReadOnlyDictionary<string, string> HistoryFieldsJql`; `internal const int MaxChangedClauses = 20`.

- [ ] **Step 1: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Reporting/JqlHistoryTests.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Jql;
using static JiraUnpacked.Tests.Reporting.JqlTestKit;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>History conditions as JQL's WAS and CHANGED, the clock pinned to 15 Oct 2026.</summary>
public sealed class JqlHistoryTests
{
    private static readonly JqlLookup People = JqlLookup.Empty with
    {
        AccountIds = Ids(("Alice", new[] { "acc-alice" }), ("Bob", new[] { "acc-bob" })),
    };

    private static FilterCondition H(FilterOperator op, object value) => C("history", op, value);

    [Fact]
    public void Was_names_the_values_and_empty()
    {
        Assert.Equal("status WAS \"Blocked\"",
            Where(H(FilterOperator.Was, new { historyField = "status", values = new[] { "Blocked" } })));
        Assert.Equal("status WAS IN (\"Blocked\", \"On hold\")",
            Where(H(FilterOperator.Was, new { historyField = "status", values = new[] { "Blocked", "On hold" } })));
        Assert.Equal("(status WAS \"Blocked\" OR status WAS EMPTY)",
            Where(H(FilterOperator.Was, new { historyField = "status", values = new string?[] { null, "Blocked" } })));
        Assert.Equal("fixVersion WAS \"1.0\"",
            Where(H(FilterOperator.Was, new { historyField = "Fix Version", values = new[] { "1.0" } })));
        Assert.Equal("assignee WAS \"acc-alice\"",
            Where(H(FilterOperator.Was, new { historyField = "assignee", values = new[] { "Alice" } }), People));
    }

    [Fact]
    public void A_person_with_no_account_narrows_the_history_condition_and_alone_leaves_it_out()
    {
        var t = Translate(All(H(FilterOperator.Was, new { historyField = "assignee", values = new[] { "Alice", "Ghost" } })), People);
        Assert.Equal("assignee WAS \"acc-alice\"", Where(t));
        var warning = Assert.Single(t.Warnings);
        Assert.Contains("'Ghost'", warning);
        Assert.Contains("narrower", warning);

        var u = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Was, new { historyField = "assignee", values = new[] { "Ghost" } })), People);
        Assert.Equal("status = \"Done\"", Where(u));
        Assert.StartsWith("Left out \"history Was of assignee\"", Assert.Single(u.Warnings));
    }

    [Fact]
    public void Changed_takes_from_to_and_by()
    {
        Assert.Equal("status CHANGED", Where(H(FilterOperator.Changed, new { historyField = "status" })));
        Assert.Equal("status CHANGED FROM \"To Do\" TO \"Done\" BY \"acc-bob\"",
            Where(H(FilterOperator.Changed, new { historyField = "status", from = new[] { "To Do" }, to = new[] { "Done" }, by = new[] { "Bob" } }), People));
        Assert.Equal("status CHANGED TO EMPTY",
            Where(H(FilterOperator.Changed, new { historyField = "status", to = new string?[] { null } })));
        Assert.Equal("assignee CHANGED FROM \"acc-alice\"",
            Where(H(FilterOperator.Changed, new { historyField = "assignee", from = new[] { "Alice" } }), People));
    }

    [Fact]
    public void Lists_in_changed_become_their_combinations_up_to_twenty()
    {
        Assert.Equal("(status CHANGED FROM \"A\" TO \"Done\" OR status CHANGED FROM \"B\" TO \"Done\")",
            Where(H(FilterOperator.Changed, new { historyField = "status", from = new[] { "A", "B" }, to = new[] { "Done" } })));

        var five = new[] { "a", "b", "c", "d", "e" };
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Changed, new { historyField = "status", from = five, to = five })));
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains("it needs 25 CHANGED clauses", Assert.Single(t.Warnings));
    }

    [Fact]
    public void An_author_with_no_account_leaves_changed_out()
    {
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Changed, new { historyField = "status", by = new[] { "Ghost" } })), People);
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains("(no Jira account in this project's data has those names)", Assert.Single(t.Warnings));
    }

    [Fact]
    public void Windows_become_after_during_and_before()
    {
        string W(object during) => Where(H(FilterOperator.Changed, new { historyField = "status", during }));
        Assert.Equal("status CHANGED AFTER -30d", W(new { kind = "inLast", amount = 30, unit = "days" }));
        Assert.Equal("status CHANGED DURING (startOfMonth(-1), endOfMonth(-1))", W(new { kind = "inPeriod", period = "lastMonth" }));
        Assert.Equal("status CHANGED DURING (\"2026-07-01\", \"2026-10-01\")", W(new { kind = "inPeriod", period = "lastQuarter" }));
        Assert.Equal("status CHANGED DURING (\"2026-09-01\", \"2026-10-01\")", W(new { kind = "between", from = "2026-09-01", to = "2026-09-30" }));
        Assert.Equal("status CHANGED AFTER \"2026-09-01\"", W(new { kind = "between", from = "2026-09-01", to = (string?)null }));
        Assert.Equal("status CHANGED BEFORE \"2026-10-01\"", W(new { kind = "between", from = (string?)null, to = "2026-09-30" }));

        var months = Translate(All(H(FilterOperator.Changed,
            new { historyField = "status", during = new { kind = "inLast", amount = 1, unit = "months" } })));
        Assert.Equal("status CHANGED AFTER -30d", Where(months));
        Assert.Contains("approximated as 30 days", Assert.Single(months.Warnings));
    }

    [Fact]
    public void Number_of_changes_and_unsupported_fields_are_left_out()
    {
        var count = Translate(Any(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.ChangeCount, new { historyField = "Sprint", compare = "Gte", count = 3 })));
        Assert.Equal("status = \"Done\"", Where(count));
        Assert.Equal(
            "Left out \"history ChangeCount of Sprint\" (JQL cannot count changes), so the JQL is narrower than this filter: it can miss issues this list shows.",
            Assert.Single(count.Warnings));

        var sprint = Translate(All(C("status", FilterOperator.Eq, "Done"),
            H(FilterOperator.Was, new { historyField = "Sprint", values = new[] { "PLAT 101" } })));
        Assert.Contains("not 'Sprint'", Assert.Single(sprint.Warnings));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~JqlHistoryTests"`
Expected: FAIL — `Assert.StartsWith() Failure` (history is still "JQL has no field for 'history'" and left out). `Number_of_changes_and_unsupported_fields_are_left_out` fails on the warning text.

- [ ] **Step 3: Write the history translation**

Create `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.History.cs`:

```csharp
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>History conditions as WAS and CHANGED, for the fields JQL keeps the history of. JQL cannot count changes.</summary>
public sealed partial class JqlTranslator
{
    /// <summary>
    /// The changelog's field names (as Jira reports them, matched exactly like the filter) that
    /// JQL's WAS and CHANGED accept, and their JQL names.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> HistoryFieldsJql =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = "status",
            ["assignee"] = "assignee",
            ["reporter"] = "reporter",
            ["priority"] = "priority",
            ["resolution"] = "resolution",
            ["Fix Version"] = "fixVersion",
        };

    /// <summary>History fields whose values are people: JQL names them by account id.</summary>
    private static readonly HashSet<string> PeopleFields = new(StringComparer.Ordinal) { "assignee", "reporter" };

    /// <summary>One CHANGED clause takes one FROM, TO and BY; lists are written as their combinations, at most this many.</summary>
    internal const int MaxChangedClauses = 20;

    /// <summary>A history list as JQL literals, whether (Empty) was in it, and the people names no account has.</summary>
    private sealed record HistoryList(List<string> Literals, bool WantsEmpty, List<string> Unknown);

    private Clause History(FilterCondition c, Run run)
    {
        if (c.Op == FilterOperator.ChangeCount) return Clause.Out("JQL cannot count changes");
        if (c.Value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("historyField", out var hf) || hf.ValueKind != JsonValueKind.String)
            return Clause.Out("it names no field");
        var name = hf.GetString()!;
        if (!HistoryFieldsJql.TryGetValue(name, out var field))
            return Clause.Out($"JQL keeps the history of status, assignee, reporter, priority, resolution and Fix Version only, not '{name}'");
        return c.Op == FilterOperator.Was ? Was(field, name, v, run) : Changed(field, name, v, run);
    }

    /// <summary>
    /// The list under <paramref name="key"/>, or null when absent. People (and every BY) by
    /// account id; anything else by its name. Null or "" is (Empty).
    /// </summary>
    private static HistoryList? ReadHistoryList(JsonElement v, string key, string historyField, Run run)
    {
        if (!v.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var names = new List<string>();
        var wantsEmpty = false;
        foreach (var e in arr.EnumerateArray())
        {
            var s = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (string.IsNullOrEmpty(s)) wantsEmpty = true;
            else if (!names.Contains(s)) names.Add(s);
        }
        if (key != "by" && !PeopleFields.Contains(historyField))
            return new HistoryList(names.Select(JqlText.Quote).ToList(), wantsEmpty, new List<string>());

        var literals = new List<string>();
        var unknown = new List<string>();
        foreach (var n in names)
        {
            if (run.Lookup.AccountIds.TryGetValue(n, out var ids) && ids.Count > 0)
            {
                foreach (var id in ids)
                {
                    var l = JqlText.Quote(id);
                    if (!literals.Contains(l)) literals.Add(l);
                }
            }
            else
            {
                unknown.Add(n);
            }
        }
        return new HistoryList(literals, wantsEmpty, unknown);
    }

    /// <summary>
    /// Unlike a current assignee, history matches the changelog's display names — a person with no
    /// account row can still be in it. So dropping a name here does change the meaning: narrower.
    /// </summary>
    private static string UnknownPeople(List<string> unknown) =>
        $"No Jira account in this project's data is named {string.Join(", ", unknown.Select(n => $"'{n}'"))}, so the JQL leaves {(unknown.Count == 1 ? "that name" : "those names")} out of a history condition and is narrower than this filter: it can miss issues this list shows.";

    /// <summary>WAS / WAS IN for the values, WAS EMPTY for (Empty), OR-ed.</summary>
    private static Clause Was(string field, string name, JsonElement v, Run run)
    {
        var list = ReadHistoryList(v, "values", name, run);
        if (list is null) return Clause.Out("it names no value");
        var parts = new List<string>();
        if (list.Literals.Count == 1) parts.Add($"{field} WAS {list.Literals[0]}");
        else if (list.Literals.Count > 1) parts.Add($"{field} WAS IN {JqlText.List(list.Literals)}");
        if (list.WantsEmpty) parts.Add($"{field} WAS EMPTY");
        if (parts.Count == 0) return Clause.Out("no Jira account in this project's data has those names");
        if (list.Unknown.Count > 0) run.Warn(UnknownPeople(list.Unknown));
        return Clause.Of(JqlText.Join(parts, "OR"));
    }

    /// <summary>
    /// CHANGED [FROM x] [TO y] [BY account] [window]. JQL takes one value per FROM, TO and BY, so
    /// lists become their combinations, OR-ed — each still ONE change satisfying all its parts, as
    /// the filter means it — up to <see cref="MaxChangedClauses"/>.
    /// </summary>
    private Clause Changed(string field, string name, JsonElement v, Run run)
    {
        var fromList = ReadHistoryList(v, "from", name, run);
        var toList = ReadHistoryList(v, "to", name, run);
        var byList = ReadHistoryList(v, "by", name, run);
        // A list given but left with nothing JQL can name would drop that requirement entirely.
        foreach (var list in new[] { fromList, toList, byList })
        {
            if (list is not null && list.Literals.Count == 0 && !list.WantsEmpty)
                return Clause.Out("no Jira account in this project's data has those names");
        }

        string? window = null;
        if (v.TryGetProperty("during", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            window = Window(d, run);
            if (window is null) return Clause.Out("its window cannot be read");
        }

        static List<string?> Options(HistoryList? list, string keyword)
        {
            if (list is null) return new List<string?> { null };
            var options = list.Literals.Select(l => (string?)$"{keyword} {l}").ToList();
            if (list.WantsEmpty) options.Add($"{keyword} EMPTY");
            return options;
        }

        var combos = Options(fromList, "FROM")
            .SelectMany(f => Options(toList, "TO"), (f, t) => (f, t))
            .SelectMany(ft => Options(byList, "BY"), (ft, b) => new[] { ft.f, ft.t, b })
            .ToList();
        if (combos.Count > MaxChangedClauses)
            return Clause.Out($"it needs {combos.Count} CHANGED clauses — JQL takes one value per FROM, TO and BY — and at most {MaxChangedClauses} are written");

        foreach (var list in new[] { fromList, toList, byList })
        {
            if (list is { Unknown.Count: > 0 }) run.Warn(UnknownPeople(list.Unknown));
        }

        var clauses = combos.Select(parts =>
        {
            var words = new List<string> { $"{field} CHANGED" };
            words.AddRange(parts.OfType<string>());
            if (window is not null) words.Add(window);
            return string.Join(" ", words);
        }).ToList();
        return Clause.Of(JqlText.Join(clauses, "OR"));
    }

    /// <summary>A `during` as a CHANGED predicate: AFTER a relative offset, DURING a period, or the between bounds.</summary>
    private string? Window(JsonElement d, Run run)
    {
        if (!d.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String) return null;
        switch (k.GetString())
        {
            case "inLast":
                return Relative(d, run) is { } offset ? $"AFTER {offset}" : null;
            case "inPeriod":
                return Period(d, run) is { } p ? $"DURING ({p.Start}, {p.End})" : null;
            case "between":
                JsonElement? fromEl = d.TryGetProperty("from", out var f) && f.ValueKind != JsonValueKind.Null ? f : null;
                JsonElement? toEl = d.TryGetProperty("to", out var t) && t.ValueKind != JsonValueKind.Null ? t : null;
                var (lo, hi, _) = ReadRange(fromEl, toEl);
                if (lo is { } start && hi is { } end) return $"DURING ({JqlText.Date(start)}, {JqlText.Date(end)})";
                if (lo is { } since) return $"AFTER {JqlText.Date(since)}";
                if (hi is { } until) return $"BEFORE {JqlText.Date(until)}";
                return null;
            default:
                return null;
        }
    }
}
```

In `src/JiraUnpacked.Infrastructure/Reporting/Jql/JqlTranslator.cs`, replace:

```csharp
        "parent" => Parent(c),
        _ => Clause.Out($"JQL has no field for '{c.Field}'"),
```

with:

```csharp
        "parent" => Parent(c),
        "history" => History(c, run),
        _ => Clause.Out($"JQL has no field for '{c.Field}'"),
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~Jql"`
Expected: PASS (`JqlTranslatorTests`, `JqlLookupFieldTests`, `JqlDateTests`, `JqlHistoryTests`).

- [ ] **Step 5: Commit**

```bash
git -C C:/jw-filters add src/JiraUnpacked.Infrastructure/Reporting/Jql tests/JiraUnpacked.Tests/Reporting/JqlHistoryTests.cs
git -C C:/jw-filters commit -m "feat(jql): history as WAS and CHANGED with windows; number of changes left out with a warning" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The endpoint — validate like the query, load the tenant's lookup, build the link (server)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IIssueJqlService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Jql/IssueJqlService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` (after the `IHistoryValueService` registration)
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs` (constructor + new action)
- Create: `tests/JiraUnpacked.Tests/Integration/JqlTestData.cs`
- Create: `tests/JiraUnpacked.Tests/Integration/IssueJqlEndpointTests.cs`

**Interfaces:**
- Consumes (Tasks 1–4): `JqlTranslator.Translate(...)`, `JqlLookup`, `JqlTranslation`; `IIssueFilterCompiler.CompileAsync(ReportFilter?, CancellationToken)`; `SortFields.IsKnown/Describe`; `FieldRole.Team/StoryPoints`; tests: `MetricsTestData.AuthedTenantAsync`, `MetricsTestData.SeedProjectAsync` (key `ACME`, `SeedContext(Tenant, ConnectionId, ProjectId, …)`), `MetricsTestData.Exec`.
- Produces: `public sealed record IssueJqlRequest(FilterGroup? Filter, string? SortBy = null, bool SortDescending = true)`; `public sealed record JqlWarning(string Message)`; `public sealed record IssueJqlView(string Jql, string Url, IReadOnlyList<JqlWarning> Warnings)`; `public sealed record IssueJqlResult(IssueJqlView? View, string? Error)` (both null = 404); `IIssueJqlService.BuildAsync(Guid projectId, IssueJqlRequest request, CancellationToken ct = default)`.
- Produces (HTTP): `POST /api/projects/{projectId:guid}/issues/jql` → `200 { jql, url, warnings: [{ message }] }` | `400 ProblemDetails { title }` | `404`.
- Produces (tests): `JqlTestData.SeedUserAsync(ctx, displayName, accountId, asTenant?)`, `SeedSprintAsync(ctx, name, jiraSprintId, asTenant?)`, `SeedTeamAsync(ctx, name, jiraTeamId, asTenant?)`, `SeedMappingAsync(ctx, role, jiraFieldId, asTenant?)`, `SetBaseUrlAsync(ctx, baseUrl)`.

- [ ] **Step 1: Write the fixtures and the failing integration tests**

Create `tests/JiraUnpacked.Tests/Integration/JqlTestData.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/JqlTestData.cs
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The rows the JQL lookup reads. Seeding uses the superuser connection (bypasses RLS) purely to
/// build fixtures; every assertion reads through the API as the owning tenant. <c>asTenant</c>
/// writes a row under ANOTHER tenant's id while keeping this project's connection and project id —
/// exactly the row a missing tenant filter would leak into the JQL.
/// </summary>
internal static class JqlTestData
{
    internal static Task SeedUserAsync(SeedContext ctx, string displayName, string accountId, Guid? asTenant = null) =>
        ExecAsync(@"INSERT INTO ""JiraUsers""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraAccountId"",""DisplayName"")
                    VALUES (@i,@t,@c,@a,@n)",
            ("i", Guid.NewGuid()), ("t", asTenant ?? ctx.Tenant), ("c", ctx.ConnectionId), ("a", accountId), ("n", displayName));

    internal static Task SeedSprintAsync(SeedContext ctx, string name, string jiraSprintId, Guid? asTenant = null) =>
        ExecAsync(@"INSERT INTO ""Sprints""(""Id"",""EndUserId"",""JiraProjectId"",""JiraSprintId"",""Name"",""State"")
                    VALUES (@i,@t,@p,@s,@n,'active')",
            ("i", Guid.NewGuid()), ("t", asTenant ?? ctx.Tenant), ("p", ctx.ProjectId), ("s", jiraSprintId), ("n", name));

    internal static Task SeedTeamAsync(SeedContext ctx, string name, string jiraTeamId, Guid? asTenant = null) =>
        ExecAsync(@"INSERT INTO ""Teams""(""Id"",""EndUserId"",""JiraProjectId"",""JiraTeamId"",""Name"")
                    VALUES (@i,@t,@p,@j,@n)",
            ("i", Guid.NewGuid()), ("t", asTenant ?? ctx.Tenant), ("p", ctx.ProjectId), ("j", jiraTeamId), ("n", name));

    /// <summary><paramref name="role"/> as stored: "Team", "StoryPoints", …</summary>
    internal static Task SeedMappingAsync(SeedContext ctx, string role, string jiraFieldId, Guid? asTenant = null) =>
        ExecAsync(@"INSERT INTO ""FieldMappings""(""Id"",""EndUserId"",""JiraProjectId"",""Role"",""JiraFieldId"")
                    VALUES (@i,@t,@p,@r,@f)",
            ("i", Guid.NewGuid()), ("t", asTenant ?? ctx.Tenant), ("p", ctx.ProjectId), ("r", role), ("f", jiraFieldId));

    internal static Task SetBaseUrlAsync(SeedContext ctx, string baseUrl) =>
        ExecAsync(@"UPDATE ""JiraConnections"" SET ""BaseUrl"" = @u WHERE ""Id"" = @c",
            ("u", baseUrl), ("c", ctx.ConnectionId));

    private static async Task ExecAsync(string sql, params (string, object?)[] ps)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn, sql, ps);
    }
}
```

Create `tests/JiraUnpacked.Tests/Integration/IssueJqlEndpointTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/IssueJqlEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// POST /issues/jql end to end, as the owning tenant: the tenant's own ids and mappings, the link
/// to its Jira, the same 404s and 400s as the query — and never another tenant's rows.
/// </summary>
public sealed class IssueJqlEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public IssueJqlEndpointTests(JiraUnpackedApiFactory f) => _factory = f;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record WarningDto(string Message);

    private sealed record JqlDto(string Jql, string Url, List<WarningDto> Warnings);

    private static object Cond(string field, string op, object? value = null) => new { field, op, value };

    private static Task<HttpResponseMessage> PostJqlAsync(HttpClient client, Guid projectId, object body) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/issues/jql", body);

    private static async Task<JqlDto> JqlAsync(HttpClient client, Guid projectId, object body)
    {
        var resp = await PostJqlAsync(client, projectId, body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JqlDto>())!;
    }

    private static async Task<string> TitleAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    [Fact]
    public async Task Translates_with_this_tenants_ids_and_links_to_the_connections_jira()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await JqlTestData.SetBaseUrlAsync(ctx, "https://acme.atlassian.net/");
        await JqlTestData.SeedUserAsync(ctx, "Alice", "acc-alice");
        await JqlTestData.SeedSprintAsync(ctx, "Sprint 7", "107");
        await JqlTestData.SeedTeamAsync(ctx, "Core", "team-1");
        await JqlTestData.SeedMappingAsync(ctx, "Team", "customfield_10001");
        await JqlTestData.SeedMappingAsync(ctx, "StoryPoints", "customfield_10016");

        var body = await JqlAsync(client, ctx.ProjectId, new
        {
            filter = new
            {
                logic = "And",
                conditions = new[]
                {
                    Cond("assignee", "Eq", "Alice"), Cond("sprint", "Eq", "Sprint 7"),
                    Cond("team", "Eq", "Core"), Cond("storyPoints", "Gte", 3),
                },
            },
            sortBy = "priority",
            sortDescending = false,
        });

        const string expected =
            "project = \"ACME\" AND (assignee = \"acc-alice\" AND sprint = 107 AND cf[10001] = \"team-1\" AND cf[10016] >= 3) ORDER BY priority ASC, key ASC";
        Assert.Equal(expected, body.Jql);
        // One slash before issues/, whatever the stored base URL ends with; the JQL fully encoded.
        Assert.Equal("https://acme.atlassian.net/issues/?jql=" + Uri.EscapeDataString(expected), body.Url);
        Assert.DoesNotContain(" ", body.Url);
        Assert.DoesNotContain("\"", body.Url);
        Assert.Contains("every sprint it has been in", Assert.Single(body.Warnings).Message);
    }

    [Fact]
    public async Task Another_tenants_accounts_sprints_teams_and_mappings_are_never_used()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        // Same connection, same project, same names — only the tenant differs.
        await JqlTestData.SeedUserAsync(ctx, "Alice", "acc-other", asTenant: other);
        await JqlTestData.SeedSprintAsync(ctx, "Sprint 7", "999", asTenant: other);
        await JqlTestData.SeedTeamAsync(ctx, "Core", "team-other", asTenant: other);
        await JqlTestData.SeedMappingAsync(ctx, "Team", "customfield_10001", asTenant: other);

        var body = await JqlAsync(client, ctx.ProjectId, new
        {
            filter = new
            {
                logic = "And",
                conditions = new[] { Cond("assignee", "Eq", "Alice"), Cond("sprint", "Eq", "Sprint 7"), Cond("team", "Eq", "Core") },
            },
        });

        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", body.Jql);
        foreach (var leaked in new[] { "acc-other", "999", "team-other", "cf[" })
            Assert.DoesNotContain(leaked, body.Jql);
        Assert.Equal(3, body.Warnings.Count(w => w.Message.StartsWith("Left out", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_unknown_or_another_tenants_project_is_404()
    {
        var (client, _) = await MetricsTestData.AuthedTenantAsync(_factory);
        var (_, other) = await MetricsTestData.AuthedTenantAsync(_factory);
        var foreign = await MetricsTestData.SeedProjectAsync(other);

        Assert.Equal(HttpStatusCode.NotFound, (await PostJqlAsync(client, Guid.NewGuid(), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostJqlAsync(client, foreign.ProjectId, new { })).StatusCode);
    }

    [Fact]
    public async Task An_invalid_filter_or_sort_is_the_same_400_the_query_gives()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var bad = new object[]
        {
            new { filter = new { logic = "And", conditions = new[] { Cond("bogus", "Eq", "x") } } },
            new { filter = new { logic = "And", conditions = Array.Empty<object>() } },
            new { filter = new { logic = "And", conditions = new[] { Cond("storyPoints", "Eq", "three") } } },
            new { sortBy = "bogus" },
        };
        foreach (var body in bad)
        {
            var query = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query", body);
            var jql = await PostJqlAsync(client, ctx.ProjectId, body);
            Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, jql.StatusCode);
            Assert.Equal(await TitleAsync(query), await TitleAsync(jql));
        }
    }

    [Fact]
    public async Task No_filter_or_a_switched_off_one_is_the_whole_project()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", (await JqlAsync(client, ctx.ProjectId, new { })).Jql);
        var off = await JqlAsync(client, ctx.ProjectId, new
        {
            filter = new { logic = "And", conditions = new[] { Cond("status", "Eq", "Done") }, disabled = true },
        });
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", off.Jql);
        Assert.Empty(off.Warnings);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~IssueJqlEndpointTests"`
Expected: FAIL — every test gets `404 NotFound` where it expects 200/400 (the route does not exist yet); `An_unknown_or_another_tenants_project_is_404` passes by accident.

- [ ] **Step 3: Write the contract, the service, the registration and the action**

Create `src/JiraUnpacked.Domain/Abstractions/IIssueJqlService.cs`:

```csharp
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>The list's filter (the same tree the query takes) and its sort, to be written as JQL.</summary>
public sealed record IssueJqlRequest(FilterGroup? Filter, string? SortBy = null, bool SortDescending = true);

/// <summary>One way the JQL differs from the filter: a part left out, an approximation, a sort JQL cannot do.</summary>
public sealed record JqlWarning(string Message);

/// <summary>The JQL, a link that opens it in the project's Jira, and every difference from the filter.</summary>
public sealed record IssueJqlView(string Jql, string Url, IReadOnlyList<JqlWarning> Warnings);

/// <summary>View + null Error => 200. Error => 400 (the query's own message). Both null => unknown or another tenant's project => 404.</summary>
public sealed record IssueJqlResult(IssueJqlView? View, string? Error);

public interface IIssueJqlService
{
    Task<IssueJqlResult> BuildAsync(Guid projectId, IssueJqlRequest request, CancellationToken ct = default);
}
```

Create `src/JiraUnpacked.Infrastructure/Reporting/Jql/IssueJqlService.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// The list's filter as JQL and a link that opens it in the project's Jira. Checked exactly as the
/// issue query checks it — project, then filter (by the shared compiler), then sort — so a filter
/// the list refuses is refused here with the same 400. Every id in the JQL comes from this
/// tenant's own rows, read through the injected tenant-scoped context; none from the request.
/// </summary>
public sealed class IssueJqlService : IIssueJqlService
{
    private readonly AppDbContext _db;
    private readonly IIssueFilterCompiler _compiler;
    private readonly JqlTranslator _translator;

    public IssueJqlService(AppDbContext db, IIssueFilterCompiler compiler, JqlTranslator translator)
    {
        _db = db;
        _compiler = compiler;
        _translator = translator;
    }

    public async Task<IssueJqlResult> BuildAsync(Guid projectId, IssueJqlRequest request, CancellationToken ct = default)
    {
        // Existence FIRST, as the query does: an unknown or another tenant's project is a 404 whatever the filter is.
        var project = await _db.JiraProjects.Where(p => p.Id == projectId)
            .Select(p => new { p.JiraProjectKey, p.JiraConnectionId })
            .FirstOrDefaultAsync(ct);
        if (project is null) return new IssueJqlResult(null, null);

        var compiled = await _compiler.CompileAsync(new ReportFilter(null, request.Filter), ct);
        if (compiled.Error is not null) return new IssueJqlResult(null, compiled.Error);
        if (!SortFields.IsKnown(request.SortBy)) return new IssueJqlResult(null, SortFields.Describe(request.SortBy));

        var baseUrl = await _db.JiraConnections.Where(c => c.Id == project.JiraConnectionId)
            .Select(c => c.BaseUrl).FirstOrDefaultAsync(ct) ?? "";
        var lookup = await LoadLookupAsync(projectId, project.JiraConnectionId, ct);

        var translation = _translator.Translate(
            project.JiraProjectKey, request.Filter, request.SortBy, request.SortDescending, lookup);
        var url = $"{baseUrl.TrimEnd('/')}/issues/?jql={Uri.EscapeDataString(translation.Jql)}";
        return new IssueJqlResult(
            new IssueJqlView(translation.Jql, url, translation.Warnings.Select(w => new JqlWarning(w)).ToList()),
            null);
    }

    /// <summary>
    /// What the translator needs, through the tenant-scoped context: the connection's users, the
    /// project's sprints and teams, and its Team and Story Points mappings. Small tables, read whole.
    /// </summary>
    private async Task<JqlLookup> LoadLookupAsync(Guid projectId, Guid connectionId, CancellationToken ct)
    {
        var users = await _db.JiraUsers.Where(u => u.JiraConnectionId == connectionId)
            .Select(u => new { u.DisplayName, u.JiraAccountId }).ToListAsync(ct);
        var sprints = await _db.Sprints.Where(s => s.JiraProjectId == projectId)
            .Select(s => new { s.Name, s.JiraSprintId }).ToListAsync(ct);
        var teams = await _db.Teams.Where(t => t.JiraProjectId == projectId)
            .Select(t => new { t.Name, t.JiraTeamId }).ToListAsync(ct);
        var mappings = await _db.FieldMappings.Where(m => m.JiraProjectId == projectId)
            .Select(m => new { m.Role, m.JiraFieldId }).ToListAsync(ct);

        return new JqlLookup(
            ByName(users.Select(u => (u.DisplayName, u.JiraAccountId))),
            ByName(sprints.Select(s => (s.Name, s.JiraSprintId))),
            ByName(teams.Select(t => (t.Name, t.JiraTeamId))),
            mappings.FirstOrDefault(m => m.Role == FieldRole.Team)?.JiraFieldId,
            mappings.FirstOrDefault(m => m.Role == FieldRole.StoryPoints)?.JiraFieldId);
    }

    /// <summary>Name → every id it has (a shared name keeps all of them), exact names, ids de-duplicated in a stable order.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ByName(IEnumerable<(string Name, string Id)> rows) =>
        rows.GroupBy(r => r.Name, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.Id).Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
}
```

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, replace:

```csharp
        services.AddScoped<IHistoryValueService, HistoryValueService>();
```

with:

```csharp
        services.AddScoped<IHistoryValueService, HistoryValueService>();
        services.AddScoped<IIssueJqlService, JiraUnpacked.Infrastructure.Reporting.Jql.IssueJqlService>();
        services.AddScoped<JiraUnpacked.Infrastructure.Reporting.Jql.JqlTranslator>();
```

In `src/JiraUnpacked.Api/Controllers/IssuesController.cs`, replace:

```csharp
    private readonly IHistoryValueService _history;

    public IssuesController(IIssueQueryService issues, IFilterValueService filterValues, IHistoryValueService history)
    {
        _issues = issues;
        _filterValues = filterValues;
        _history = history;
    }
```

with:

```csharp
    private readonly IHistoryValueService _history;
    private readonly IIssueJqlService _jql;

    public IssuesController(
        IIssueQueryService issues, IFilterValueService filterValues, IHistoryValueService history, IIssueJqlService jql)
    {
        _issues = issues;
        _filterValues = filterValues;
        _history = history;
        _jql = jql;
    }
```

and replace:

```csharp
    /// <summary>
    /// The values a dictionary field actually takes in this project, for the filter's value
```

with:

```csharp
    /// <summary>
    /// The list's filter as JQL, with a link that opens it in the project's Jira and every way the
    /// JQL differs from the filter. The same 404s and 400s as the query: the filter is checked by
    /// the same compiler first.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/issues/jql")]
    public async Task<IActionResult> Jql(Guid projectId, IssueJqlRequest request, CancellationToken ct = default)
    {
        var result = await _jql.BuildAsync(projectId, request, ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        return result.View is null ? NotFound() : Ok(result.View);
    }

    /// <summary>
    /// The values a dictionary field actually takes in this project, for the filter's value
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~IssueJqlEndpointTests|FullyQualifiedName~Jql"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git -C C:/jw-filters add src/JiraUnpacked.Domain/Abstractions/IIssueJqlService.cs src/JiraUnpacked.Infrastructure/Reporting/Jql/IssueJqlService.cs src/JiraUnpacked.Infrastructure/DependencyInjection.cs src/JiraUnpacked.Api/Controllers/IssuesController.cs tests/JiraUnpacked.Tests/Integration/JqlTestData.cs tests/JiraUnpacked.Tests/Integration/IssueJqlEndpointTests.cs
git -C C:/jw-filters commit -m "feat(jql): POST /issues/jql — tenant-scoped lookup, query-parity 400s, link to the connection's Jira" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: `describeFilter` — the filter as one sentence (SPA, pure)

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/describeFilter.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/describeFilter.test.ts`

**Interfaces:**
- Consumes: `FilterGroup`, `FilterCondition`, `HistoryWindow`, `ListValue`, `ConditionValue` (`src/api/issues.ts`); `ISSUE_COLUMNS` (`columns.ts`); `OPERATOR_LABELS` (`filterCatalogHelpers.ts`); `HISTORY_COMPARES`, `readHistoryValue`, `takesHistory` (`historyShapes.ts`); `PERIODS`, `readPeriod`, `readRelativeAmount`, `readSmart`, `smartLabel` (`valueShapes.ts`); in tests `pruneTree`, `emptyRoot`, `newId`, `EditorGroup` (`filterTree.ts`).
- Produces: `describeFilter(filter: FilterGroup | undefined): string`; `fieldLabel(field: string): string`; `formatDate(raw: string): string`; `listText(items: readonly string[]): string`.

- [ ] **Step 1: Write the failing tests**

Create `src/JiraUnpacked.Web/src/features/issues/describeFilter.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import type { FilterCondition, FilterGroup } from '../../api/issues';
import { describeFilter, formatDate, listText } from './describeFilter';
import { emptyRoot, newId, pruneTree, type EditorGroup } from './filterTree';

const all = (...conditions: FilterCondition[]): FilterGroup => ({ logic: 'And', conditions });
const one = (c: FilterCondition) => describeFilter(all(c));

describe('describeFilter', () => {
  it('reads no filter as All issues', () => {
    expect(describeFilter(undefined)).toBe('All issues');
    expect(describeFilter(pruneTree(emptyRoot()))).toBe('All issues');
  });

  it('names fields by their column headers and operators by the editor’s words', () => {
    expect(one({ field: 'status', op: 'Eq', value: 'Done' })).toBe('Status is Done');
    expect(one({ field: 'statusCategory', op: 'Neq', value: 'Done' })).toBe('Category is not Done');
    expect(one({ field: 'issueType', op: 'In', values: ['Bug', 'Story'] })).toBe('Type is any of Bug, Story');
    expect(one({ field: 'priority', op: 'NotIn', values: ['Low'] })).toBe('Priority is none of Low');
    expect(one({ field: 'storyPoints', op: 'Gte', value: 3 })).toBe('Points ≥ 3');
    expect(one({ field: 'assignee', op: 'IsNull' })).toBe('Assignee is empty');
    expect(one({ field: 'team', op: 'IsNotNull' })).toBe('Team is not empty');
    expect(one({ field: 'summary', op: 'Contains', value: 'login' })).toBe('Summary contains "login"');
    expect(one({ field: 'summary', op: 'NotContains', value: 'flaky' })).toBe('Summary does not contain "flaky"');
    expect(one({ field: 'jiraKey', op: 'StartsWith', value: 'PLAT-1' })).toBe('Key starts with "PLAT-1"');
  });

  it('reads (Empty) in a list as empty and smart sprints by their label', () => {
    expect(one({ field: 'sprint', op: 'In', values: [{ smart: 'active' }, null, 'Sprint 7'] }))
      .toBe('Sprint is any of Active sprint, empty, Sprint 7');
    expect(one({ field: 'sprint', op: 'Eq', value: { smart: 'open' } })).toBe('Sprint is Open sprints (active + future)');
  });

  it('lists up to five values and truncates more', () => {
    expect(listText(['a', 'b', 'c', 'd', 'e'])).toBe('a, b, c, d, e');
    expect(one({ field: 'status', op: 'In', values: ['Backlog', 'To Do', 'A', 'B', 'C', 'D'] }))
      .toBe('Status is any of Backlog, To Do, … and 4 more');
  });

  it('reads dates, ranges and relative dates', () => {
    expect(formatDate('2026-09-01')).toBe('1 Sep 2026');
    expect(formatDate('2026-09-01T10:30:00Z')).toBe('1 Sep 2026 10:30');
    expect(formatDate('not a date')).toBe('not a date');
    expect(one({ field: 'createdAt', op: 'Lt', value: '2026-09-01' })).toBe('Created < 1 Sep 2026');
    expect(one({ field: 'createdAt', op: 'Between', values: ['2026-09-01', '2026-09-30'] }))
      .toBe('Created between 1 Sep 2026 and 30 Sep 2026');
    expect(one({ field: 'createdAt', op: 'Between', values: ['2026-09-01', null] })).toBe('Created ≥ 1 Sep 2026');
    expect(one({ field: 'storyPoints', op: 'Between', values: [null, 5] })).toBe('Points ≤ 5');
    expect(one({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } })).toBe('Updated in the last 14 days');
    expect(one({ field: 'updatedAt', op: 'InLast', value: { amount: 1, unit: 'weeks' } })).toBe('Updated in the last 1 week');
    expect(one({ field: 'updatedAt', op: 'NotInLast', value: { amount: 3, unit: 'months' } })).toBe('Updated more than 3 months ago');
    expect(one({ field: 'createdAt', op: 'InPeriod', value: { period: 'thisWeek', timeZone: 'Europe/Warsaw' } }))
      .toBe('Created in period this week (Europe/Warsaw)');
    expect(one({ field: 'createdAt', op: 'InPeriod', value: { period: 'lastMonth' } })).toBe('Created in period last month');
  });

  it('reads history conditions', () => {
    expect(one({ field: 'history', op: 'Was', value: { historyField: 'status', values: ['Blocked'] } }))
      .toBe('status was Blocked');
    expect(one({ field: 'history', op: 'Was', value: { historyField: 'status', values: ['Blocked', null] } }))
      .toBe('status was any of Blocked, empty');
    expect(one({
      field: 'history', op: 'Changed',
      value: {
        historyField: 'Sprint', from: ['A'], to: ['B'], by: ['Alice'],
        during: { kind: 'between', from: '2026-09-01', to: '2026-09-30' },
      },
    })).toBe('Sprint changed from A to B by Alice between 1 Sep 2026 and 30 Sep 2026');
    expect(one({ field: 'history', op: 'Changed', value: { historyField: 'status' } })).toBe('status changed');
    expect(one({
      field: 'history', op: 'Changed',
      value: { historyField: 'status', during: { kind: 'inPeriod', period: 'lastMonth', timeZone: 'UTC' } },
    })).toBe('status changed in period last month (UTC)');
    expect(one({
      field: 'history', op: 'Changed',
      value: { historyField: 'status', during: { kind: 'between', from: '2026-09-01', to: null } },
    })).toBe('status changed since 1 Sep 2026');
    expect(one({
      field: 'history', op: 'ChangeCount',
      value: { historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 30, unit: 'days' } },
    })).toBe('Sprint changed ≥ 3 times in the last 30 days');
    expect(one({ field: 'history', op: 'ChangeCount', value: { historyField: 'Sprint', compare: 'Eq', count: 1 } }))
      .toBe('Sprint changed = 1 time');
  });

  it('joins with the group’s logic and parenthesises nested groups of more than one part', () => {
    const filter: FilterGroup = {
      logic: 'And',
      conditions: [
        { field: 'status', op: 'In', values: ['Backlog', 'To Do'] },
        {
          field: 'history', op: 'ChangeCount',
          value: { historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 30, unit: 'days' } },
        },
      ],
      groups: [{
        logic: 'Or',
        conditions: [{ field: 'assignee', op: 'Eq', value: 'Alice' }, { field: 'priority', op: 'NotIn', values: ['Low'] }],
      }],
    };
    expect(describeFilter(filter)).toBe(
      'Status is any of Backlog, To Do and Sprint changed ≥ 3 times in the last 30 days'
      + ' and (Assignee is Alice or Priority is none of Low)');
    expect(describeFilter({ logic: 'Or', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }, { field: 'status', op: 'IsNull' }] }))
      .toBe('Status is Done or Status is empty');
    expect(describeFilter({ logic: 'And', groups: [{ logic: 'Or', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] }] }))
      .toBe('Status is Done');
  });

  it('reads what is sent: disabled and unfinished parts are not in it', () => {
    const tree: EditorGroup = {
      id: newId(),
      logic: 'And',
      conditions: [
        { id: newId(), field: 'status', op: 'Eq', value: 'Done' },
        { id: newId(), field: 'priority', op: 'Eq', value: 'Low', disabled: true },
        { id: newId(), field: 'assignee', op: 'Eq', value: '' },
      ],
      groups: [{ id: newId(), logic: 'Or', conditions: [{ id: newId(), field: 'team', op: 'IsNull' }], groups: [], disabled: true }],
    };
    expect(describeFilter(pruneTree(tree))).toBe('Status is Done');
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/describeFilter.test.ts`
Expected: FAIL — `Failed to resolve import "./describeFilter"`.

- [ ] **Step 3: Write `describeFilter`**

Create `src/JiraUnpacked.Web/src/features/issues/describeFilter.ts`:

```ts
import type {
  ConditionValue, FilterCondition, FilterGroup, HistoryWindow, ListValue,
} from '../../api/issues';
import { ISSUE_COLUMNS } from './columns';
import { OPERATOR_LABELS } from './filterCatalogHelpers';
import { HISTORY_COMPARES, readHistoryValue, takesHistory } from './historyShapes';
import { PERIODS, readPeriod, readRelativeAmount, readSmart, smartLabel } from './valueShapes';

/**
 * The filter as one sentence — "Status is any of Backlog, To Do and (Assignee is Alice or …)" —
 * for the summary line above the builder. It reads the PRUNED tree (pruneTree), the very object
 * the list is queried with, so the sentence says what is applied: switched-off and unfinished
 * parts are not in it.
 *
 * Field names are the table's column headers ("Category", "Type", "Points"), operators the
 * editor's own words (OPERATOR_LABELS), and dates a fixed English form rather than the locale's,
 * so a shared link reads the same for whoever opens it.
 */

const MAX_LISTED = 5;
const SHOWN_WHEN_TRUNCATED = 2;
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const DATE_FIELDS: ReadonlySet<string> = new Set(['createdAt', 'updatedAt']);

export const fieldLabel = (field: string): string =>
  ISSUE_COLUMNS.find((c) => c.id === field)?.header ?? field;

/** "2026-09-01" → "1 Sep 2026"; with a time → "1 Sep 2026 10:30"; anything else as written. */
export function formatDate(raw: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2}))?/.exec(raw);
  if (!m) return raw;
  const month = MONTHS[Number(m[2]) - 1];
  if (!month) return raw;
  const day = `${Number(m[3])} ${month} ${m[1]}`;
  return m[4] && !(m[4] === '00' && m[5] === '00') ? `${day} ${m[4]}:${m[5]}` : day;
}

/** "A, B, C"; more than five → "A, B, … and 4 more". */
export function listText(items: readonly string[]): string {
  if (items.length <= MAX_LISTED) return items.join(', ');
  return `${items.slice(0, SHOWN_WHEN_TRUNCATED).join(', ')}, … and ${items.length - SHOWN_WHEN_TRUNCATED} more`;
}

function valueText(field: string, v: ListValue | ConditionValue | undefined): string {
  if (v === null || v === undefined || v === '') return 'empty';
  if (typeof v === 'number') return String(v);
  if (typeof v === 'string') return DATE_FIELDS.has(field) ? formatDate(v) : v;
  const smart = readSmart(v);
  return smart ? smartLabel(smart) : JSON.stringify(v);
}

/** "14 days", "1 week". */
function amountText(v: unknown): string {
  const rel = readRelativeAmount(v);
  if (!rel) return '…';
  return `${rel.amount} ${rel.amount === 1 ? rel.unit.slice(0, -1) : rel.unit}`;
}

/** "this week (Europe/Warsaw)"; no zone, no parentheses. */
function periodText(v: unknown): string {
  const p = readPeriod(v);
  if (!p) return '…';
  const label = (PERIODS.find((x) => x.key === p.period)?.label ?? p.period).toLowerCase();
  return p.timeZone ? `${label} (${p.timeZone})` : label;
}

const isBound = (x: ListValue | undefined): boolean => x !== null && x !== undefined && x !== '';

/** Between: "between A and B"; one bound open: "≥ A" / "≤ B". */
function rangeText(field: string, values: ListValue[] | undefined): string {
  const [from, to] = values ?? [];
  if (isBound(from) && isBound(to)) return `between ${valueText(field, from)} and ${valueText(field, to)}`;
  return isBound(from) ? `≥ ${valueText(field, from)}` : `≤ ${valueText(field, to)}`;
}

function windowText(w: HistoryWindow): string {
  if (w.kind === 'inLast') return `in the last ${amountText(w)}`;
  if (w.kind === 'inPeriod') return `in period ${periodText(w)}`;
  if (w.from && w.to) return `between ${formatDate(w.from)} and ${formatDate(w.to)}`;
  return w.from ? `since ${formatDate(w.from)}` : `until ${formatDate(w.to ?? '')}`;
}

/** One value as itself; several as "any of A, B". */
function oneOrAny(values: readonly (string | null)[]): string {
  const shown = values.map((v) => (v === null ? 'empty' : v));
  return shown.length === 1 ? shown[0] : `any of ${listText(shown)}`;
}

/** "status was Blocked", "Sprint changed from A to B by Alice …", "Sprint changed ≥ 3 times …". */
function describeHistory(c: FilterCondition): string {
  const v = readHistoryValue(c.op, c.value);
  // Unreachable for a pruned tree (an unreadable value is unfinished, so never sent).
  if (!v) return `${fieldLabel(c.field)} ${OPERATOR_LABELS[c.op]}`;
  const during = v.during ? ` ${windowText(v.during)}` : '';
  if (c.op === 'Was') return `${v.historyField} was ${oneOrAny(v.values ?? [])}`;
  if (c.op === 'Changed') {
    const parts = [`${v.historyField} changed`];
    if (v.from?.length) parts.push(`from ${oneOrAny(v.from)}`);
    if (v.to?.length) parts.push(`to ${oneOrAny(v.to)}`);
    if (v.by?.length) parts.push(`by ${oneOrAny(v.by)}`);
    return parts.join(' ') + during;
  }
  const compare = HISTORY_COMPARES.find((x) => x.key === v.compare)?.label ?? '';
  const count = v.count ?? 0;
  return `${v.historyField} changed ${compare} ${count} ${count === 1 ? 'time' : 'times'}${during}`;
}

function describeCondition(c: FilterCondition): string {
  if (takesHistory(c.op)) return describeHistory(c);
  const field = fieldLabel(c.field);
  switch (c.op) {
    case 'IsNull':
      return `${field} is empty`;
    case 'IsNotNull':
      return `${field} is not empty`;
    case 'In':
    case 'NotIn':
      return `${field} ${OPERATOR_LABELS[c.op]} ${listText((c.values ?? []).map((v) => valueText(c.field, v)))}`;
    case 'Between':
      return `${field} ${rangeText(c.field, c.values)}`;
    case 'InLast':
      return `${field} in the last ${amountText(c.value)}`;
    case 'NotInLast':
      return `${field} more than ${amountText(c.value)} ago`;
    case 'InPeriod':
      return `${field} in period ${periodText(c.value)}`;
    case 'Contains':
    case 'NotContains':
    case 'StartsWith':
      return `${field} ${OPERATOR_LABELS[c.op]} "${valueText(c.field, c.value)}"`;
    default:
      return `${field} ${OPERATOR_LABELS[c.op]} ${valueText(c.field, c.value)}`;
  }
}

function describeGroup(g: FilterGroup, nested: boolean): string {
  const parts = [
    ...(g.conditions ?? []).filter((c) => !c.disabled).map(describeCondition),
    ...(g.groups ?? []).filter((x) => !x.disabled).map((x) => describeGroup(x, true)),
  ].filter((s) => s !== '');
  const text = parts.join(g.logic === 'Or' ? ' or ' : ' and ');
  return nested && parts.length > 1 ? `(${text})` : text;
}

/** The pruned filter as one sentence; no filter at all reads "All issues". */
export function describeFilter(filter: FilterGroup | undefined): string {
  if (!filter || filter.disabled) return 'All issues';
  const text = describeGroup(filter, false);
  return text === '' ? 'All issues' : text;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/describeFilter.test.ts && npm run typecheck`
Expected: PASS; typecheck clean.

- [ ] **Step 5: Commit**

```bash
git -C C:/jw-filters add src/JiraUnpacked.Web/src/features/issues/describeFilter.ts src/JiraUnpacked.Web/src/features/issues/describeFilter.test.ts
git -C C:/jw-filters commit -m "feat(web): describeFilter — the pruned filter as one sentence" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: The summary line and the collapse rule (SPA)

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/FilterSummary.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/FilterSummary.test.tsx`
- Create: `src/JiraUnpacked.Web/src/test/expandFilter.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx` (seven tests expand first)
- Modify: `src/JiraUnpacked.Web/src/features/issues/SavedViews.test.tsx` (two tests expand first)

**Interfaces:**
- Consumes (Task 6): `describeFilter(filter)`; `isEmptyTree` (`filterTree.ts`); `useSavedViews(...).holdQuery`; `Button` (`src/ui`); tests: `FILTER_CATALOG`, `noSavedViews`, `mockViewsApi`, `savedView`.
- Produces: `FilterSummary({ sentence: string; count?: number; expanded: boolean; controls: string; onToggle: () => void })` rendering `<section aria-label="Filter summary">`; in IssueList: `expanded` / `setExpanded` (`useState<boolean | null>`), `builderOpen`, `sentence`, `count` (Tasks 8–9 use `setExpanded`, `filter`, `sentence`); `BUILDER_ID = 'issue-filter-builder'`.
- Produces (tests): `expandFilter(): Promise<void>` (`src/test/expandFilter.ts`) — clicks **Edit filter**.

- [ ] **Step 1: Write the failing tests**

Create `src/JiraUnpacked.Web/src/test/expandFilter.ts`:

```ts
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

/**
 * Opens the filter builder. A list opened from a link or a saved view WITH a filter shows only its
 * summary sentence until "Edit filter" is clicked — the builder is not mounted before that.
 */
export async function expandFilter(): Promise<void> {
  await userEvent.click(await screen.findByRole('button', { name: 'Edit filter' }));
}
```

Create `src/JiraUnpacked.Web/src/features/issues/FilterSummary.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import IssueList from './IssueList';
import type { IssueListItem } from '../../api/issues';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { mockViewsApi, noSavedViews, savedView } from '../../test/viewsFixture';

const ROW: IssueListItem = {
  jiraIssueId: '10001', jiraKey: 'ACME-1', summary: 'Login fails', status: 'Open', statusCategory: 'To Do',
  issueType: 'Bug', priority: 'High', assignee: 'Alice', sprint: 'Sprint 1', team: 'Core', storyPoints: 3,
  parentJiraIssueId: null, jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-01-02T00:00:00Z',
};

function mockList(totalCount: number, withViews = true) {
  server.use(
    http.get('/api/filter-catalog', () => HttpResponse.json(FILTER_CATALOG)),
    http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })),
    http.post('/api/projects/p1/issues/query', () =>
      HttpResponse.json({ items: [ROW], page: 1, pageSize: 50, totalCount })),
  );
  if (withViews) server.use(noSavedViews);
}

function renderAt(url: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const link = (filter: unknown) => `/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(filter))}`;
const summary = () => screen.getByRole('region', { name: 'Filter summary' });

describe('the filter summary', () => {
  it('opens a bare list with the builder expanded, reading All issues with its count', async () => {
    mockList(1);
    renderAt('/projects/p1/issues');

    expect(await screen.findByRole('button', { name: /add condition/i })).toBeInTheDocument();
    expect(summary()).toHaveTextContent('Showing: All issues');
    await waitFor(() => expect(summary()).toHaveTextContent('· 1 issue'));
    expect(screen.getByRole('button', { name: 'Collapse' })).toHaveAttribute('aria-expanded', 'true');
  });

  it('opens a link with a filter collapsed: the sentence and its count, no builder', async () => {
    mockList(42);
    renderAt(link({ logic: 'And', conditions: [{ field: 'status', op: 'In', values: ['Backlog', 'To Do'] }] }));

    const toggle = await screen.findByRole('button', { name: 'Edit filter' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(summary()).toHaveTextContent('Showing: Status is any of Backlog, To Do');
    await waitFor(() => expect(summary()).toHaveTextContent('· 42 issues'));
    expect(screen.queryByLabelText(/^match/i)).not.toBeInTheDocument();
  });

  it('expands and collapses on the toggle, and the sentence follows every edit', async () => {
    mockList(42);
    const user = userEvent.setup({ delay: null });
    renderAt(link({ logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }] }));

    await user.click(await screen.findByRole('button', { name: 'Edit filter' }));
    await user.type(screen.getByLabelText('Value for condition 1'), 's');
    expect(summary()).toHaveTextContent('Showing: Summary contains "logins"');

    await user.click(screen.getByRole('button', { name: 'Collapse' }));
    expect(screen.queryByLabelText('Value for condition 1')).not.toBeInTheDocument();
    expect(summary()).toHaveTextContent('Summary contains "logins"');
    expect(screen.getByRole('button', { name: 'Edit filter' })).toHaveAttribute('aria-expanded', 'false');
  });

  it('opens a default view with a filter collapsed, as loaded on mount', async () => {
    mockList(42, false);
    mockViewsApi([savedView({
      id: 'v-login', name: 'Login', isDefault: true,
      filter: { logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }] },
    })]);
    renderAt('/projects/p1/issues');

    expect(await screen.findByRole('button', { name: 'Edit filter' })).toBeInTheDocument();
    expect(summary()).toHaveTextContent('Showing: Summary contains "login"');
  });

  it('opens a link with only a sort expanded', async () => {
    mockList(42);
    renderAt('/projects/p1/issues?sortBy=summary');

    expect(await screen.findByRole('button', { name: 'Collapse' })).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/FilterSummary.test.tsx`
Expected: FAIL — `Unable to find role="region" and name "Filter summary"` / no button named "Edit filter".

- [ ] **Step 3: Write `FilterSummary` and wire it into IssueList**

Create `src/JiraUnpacked.Web/src/features/issues/FilterSummary.tsx`:

```tsx
import { Button } from '../../ui';

interface FilterSummaryProps {
  /** describeFilter of what the list is queried with. */
  sentence: string;
  /** The result count, when known for THIS filter (not a placeholder's). */
  count?: number;
  expanded: boolean;
  /** The id of the builder's wrapper, which the toggle opens and closes. */
  controls: string;
  onToggle: () => void;
}

/**
 * "Showing: <sentence> · N issues" above the builder, with the Collapse / Edit filter toggle.
 * Holds no state: whether the builder is open belongs to IssueList (component state, never the
 * URL or storage — see its collapse rule).
 */
export function FilterSummary({ sentence, count, expanded, controls, onToggle }: FilterSummaryProps) {
  return (
    <section aria-label="Filter summary" className="flex items-start gap-3">
      <p className="flex-1 text-sm text-[var(--ju-text)]">
        <span className="font-medium">Showing:</span> {sentence}
        {count !== undefined && (
          <span className="text-[var(--ju-muted)]"> · {count} {count === 1 ? 'issue' : 'issues'}</span>
        )}
      </p>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        aria-expanded={expanded}
        aria-controls={expanded ? controls : undefined}
        onClick={onToggle}
      >
        {expanded ? 'Collapse' : 'Edit filter'}
      </Button>
    </section>
  );
}
```

In `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`:

Replace:

```tsx
import { pruneTree } from './filterTree';
```

with:

```tsx
import { isEmptyTree, pruneTree } from './filterTree';
import { describeFilter } from './describeFilter';
import { FilterSummary } from './FilterSummary';
```

Replace:

```tsx
export default function IssueList() {
```

with:

```tsx
/** The builder's wrapper: what the summary's toggle opens and closes. */
const BUILDER_ID = 'issue-filter-builder';

export default function IssueList() {
```

Replace:

```tsx
  const savedViews = useSavedViews(id, { viewId, hasListState, applyParams, setViewId }, snapshot);
```

with:

```tsx
  const savedViews = useSavedViews(id, { viewId, hasListState, applyParams, setViewId }, snapshot);

  // Whether the builder is open. Decided ONCE, in the render where the list knows what it opened
  // with — the saved-views gate has settled, so a default view counts as loaded on mount: a link
  // or view that brings a filter reads as a sentence first; anything else opens the builder.
  // After that it is the user's (Collapse / Edit filter). Component state on purpose: not the URL,
  // not storage. Set during render (React re-renders at once), as useListState does with its tree.
  const [expanded, setExpanded] = useState<boolean | null>(null);
  if (expanded === null && !savedViews.holdQuery) setExpanded(isEmptyTree(tree));
  const builderOpen = expanded ?? isEmptyTree(tree);
```

Replace:

```tsx
  const filter = useMemo(() => pruneTree(tree, knownFields), [tree, knownFields]);
```

with:

```tsx
  const filter = useMemo(() => pruneTree(tree, knownFields), [tree, knownFields]);
  // The sentence reads the very object the request carries: it says what is applied.
  const sentence = useMemo(() => describeFilter(filter), [filter]);
```

Replace:

```tsx
  const shown = data ?? lastGoodData;
```

with:

```tsx
  const shown = data ?? lastGoodData;
  // THIS filter's count: a placeholder (or the last good page after a 400) belongs to another one.
  const count = data && !isPlaceholderData ? data.totalCount : undefined;
```

Replace:

```tsx
        <div className="flex-1">
          <FilterBuilder projectId={id} tree={tree} onChange={setTree} />
        </div>
```

with:

```tsx
        <div className="flex flex-1 flex-col gap-3">
          <FilterSummary
            sentence={sentence}
            count={count}
            expanded={builderOpen}
            controls={BUILDER_ID}
            onToggle={() => setExpanded(!builderOpen)}
          />
          {/* Collapsed means unmounted: nothing of the builder runs, not even its value lists. */}
          {builderOpen && (
            <div id={BUILDER_ID}>
              <FilterBuilder projectId={id} tree={tree} onChange={setTree} />
            </div>
          )}
        </div>
```

- [ ] **Step 4: Run the new tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/FilterSummary.test.tsx`
Expected: PASS.

- [ ] **Step 5: Expand first in the existing tests that open with a filter and then use the builder**

In `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`, add `import { expandFilter } from '../../test/expandFilter';` after the `import { noSavedViews } from '../../test/viewsFixture';` line, then add the line `await expandFilter();` in these seven tests, exactly where shown (each `old` → `new`; include the neighbouring line given to make the match unique):

1. `carries Any through to the request when the toggle is switched` —
   old:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${ONE_CONDITION}`);
       await screen.findByLabelText(/match/i);
   ```
   new:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${ONE_CONDITION}`);
       await expandFilter();
       await screen.findByLabelText(/match/i);
   ```
2. `reads a legacy flat link with its old logic, and writes a tree without it on the next edit` —
   old: `    await user.selectOptions(await screen.findByLabelText(/match/i), 'And');`
   new:
   ```tsx
       await expandFilter();
       await user.selectOptions(await screen.findByLabelText(/match/i), 'And');
   ```
3. `offers the values from this project as a dropdown for a dictionary field` —
   old:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${STATUS_CONDITION}`);

       // Re-queried inside waitFor
   ```
   new:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${STATUS_CONDITION}`);
       await expandFilter();

       // Re-queried inside waitFor
   ```
4. `keeps a free text box for a field that has no value list` —
   old:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${SUMMARY_CONDITION}`);
   ```
   new:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${SUMMARY_CONDITION}`);
       await expandFilter();
   ```
5. `falls back to the text box when the value list cannot be loaded` —
   old:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${STATUS_CONDITION}`);

       const control = await screen.findByLabelText(/value for condition 1/i);
   ```
   new:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${STATUS_CONDITION}`);
       await expandFilter();

       const control = await screen.findByLabelText(/value for condition 1/i);
   ```
6. `does not ask for a value list when the operator takes no value` —
   old:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${isNull}`);
   ```
   new:
   ```tsx
       renderIssueListAt(`/projects/p1/issues?filter=${isNull}`);
       await expandFilter();
   ```
7. `sends the tree it shows, minus the parts switched off` —
   old: `    await user.click(await screen.findByRole('button', { name: 'Disable condition 2.1' }));`
   new:
   ```tsx
       await expandFilter();
       await user.click(await screen.findByRole('button', { name: 'Disable condition 2.1' }));
   ```

In `src/JiraUnpacked.Web/src/features/issues/SavedViews.test.tsx`, add `import { expandFilter } from '../../test/expandFilter';` after its `viewsFixture` import, then:

8. `opens the default only once: Clear all and a later bare URL leave the list bare` —
   old: `    await user.click(screen.getByRole('button', { name: 'Clear all' }));`
   new:
   ```tsx
       await expandFilter();
       await user.click(screen.getByRole('button', { name: 'Clear all' }));
   ```
9. `drops a view id that names no view, and keeps the rest of the link` —
   old: `    expect(screen.getByLabelText('Value for condition 1')).toHaveValue('crash');`
   new:
   ```tsx
       await expandFilter();
       expect(screen.getByLabelText('Value for condition 1')).toHaveValue('crash');
   ```

- [ ] **Step 6: Run the whole web suite**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npx vitest run`
Expected: PASS. If any other existing test fails because it mounts IssueList with `filter=` (or a default view) and then reaches for a builder control (`Match`, `Value for condition …`, `Field for condition …`, `Add condition`, `Clear all`, `Disable condition …`), add the same single line `await expandFilter();` before its first builder query (and the import) — no other change to an existing test.

- [ ] **Step 7: Commit**

```bash
git -C C:/jw-filters add src/JiraUnpacked.Web/src/features/issues/FilterSummary.tsx src/JiraUnpacked.Web/src/features/issues/FilterSummary.test.tsx src/JiraUnpacked.Web/src/test/expandFilter.ts src/JiraUnpacked.Web/src/features/issues/IssueList.tsx src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx src/JiraUnpacked.Web/src/features/issues/SavedViews.test.tsx
git -C C:/jw-filters commit -m "feat(web): summary line above the builder; a link or view with a filter opens collapsed" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Copy JQL, Open in Jira and the differences (SPA)

**Files:**
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts` (append after `getHistoryValues`)
- Modify: `src/JiraUnpacked.Web/src/api/queryKeys.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/JqlActions.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/JqlActions.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`

**Interfaces:**
- Consumes: `POST /api/projects/{id}/issues/jql` (Task 5); `apiFetch`, `ApiError`; `Button`, `Modal`, `Toast` (`src/ui`); IssueList's `filter`, `sortBy`, `sortDescending` (Task 7 left them in place).
- Produces: `interface JqlRequest { filter?: FilterGroup; sortBy: string; sortDescending: boolean }`; `interface JqlWarning { message: string }`; `interface JqlResult { jql: string; url: string; warnings: JqlWarning[] }`; `getIssueJql(projectId: string, request: JqlRequest): Promise<JqlResult>`; `queryKeys.issueJql(projectId: string, request: unknown)` → `['projects', projectId, 'jql', JSON.stringify(request)]`; `JqlActions({ projectId: string; request: JqlRequest })`.

- [ ] **Step 1: Write the failing tests**

Create `src/JiraUnpacked.Web/src/features/issues/JqlActions.test.tsx`:

```tsx
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/setup';
import type { JqlRequest, JqlResult } from '../../api/issues';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { noSavedViews } from '../../test/viewsFixture';
import IssueList from './IssueList';
import { JqlActions } from './JqlActions';

const REQUEST: JqlRequest = {
  filter: { logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] },
  sortBy: 'updatedAt',
  sortDescending: true,
};

const RESULT: JqlResult = {
  jql: 'project = "ACME" AND status = "Done" ORDER BY updated DESC, key ASC',
  url: 'https://acme.atlassian.net/issues/?jql=project%20%3D%20%22ACME%22',
  warnings: [],
};

function mockJql(result: JqlResult = RESULT): unknown[] {
  const bodies: unknown[] = [];
  server.use(http.post('/api/projects/p1/issues/jql', async ({ request }) => {
    bodies.push(await request.json());
    return HttpResponse.json(result);
  }));
  return bodies;
}

function renderActions(request: JqlRequest = REQUEST) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <JqlActions projectId="p1" request={request} />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('JqlActions', () => {
  it('copies the JQL of the filter it is given and says so', async () => {
    const bodies = mockJql();
    const user = userEvent.setup();
    const writeText = vi.spyOn(navigator.clipboard, 'writeText');
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Copy JQL' }));

    expect(await screen.findByRole('status')).toHaveTextContent('JQL copied');
    expect(writeText).toHaveBeenCalledWith(RESULT.jql);
    expect(bodies).toEqual([REQUEST]);
  });

  it('shows the JQL to copy by hand when the browser refuses', async () => {
    mockJql();
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new Error('denied'));
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Copy JQL' }));

    const dialog = await screen.findByRole('dialog', { name: 'Copy the JQL' });
    expect(within(dialog).getByLabelText('JQL')).toHaveValue(RESULT.jql);
    expect(screen.queryByText('JQL copied')).not.toBeInTheDocument();
  });

  it('opens the Jira link in a new tab without an opener or a referrer', async () => {
    mockJql();
    const user = userEvent.setup();
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Open in Jira' }));

    await waitFor(() => expect(open).toHaveBeenCalledWith(RESULT.url, '_blank', 'noopener,noreferrer'));
  });

  it('never opens a link that is not a web address', async () => {
    mockJql({ ...RESULT, url: 'javascript:alert(1)' });
    const user = userEvent.setup();
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Open in Jira' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The Jira link is not a web address.');
    expect(open).not.toHaveBeenCalled();
  });

  it('lists the differences once the JQL is known', async () => {
    const messages = [
      'Left out "history ChangeCount of Sprint" (JQL cannot count changes), so the JQL is wider than this filter: it can match issues this list does not show.',
      'JQL has no month unit for relative dates, so months are approximated as 30 days.',
    ];
    mockJql({ ...RESULT, warnings: messages.map((message) => ({ message })) });
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, 'writeText');
    renderActions();
    expect(screen.queryByRole('button', { name: /differences?$/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Copy JQL' }));
    await user.click(await screen.findByRole('button', { name: '2 differences' }));

    const dialog = screen.getByRole('dialog', { name: 'How the JQL differs from this filter' });
    expect(within(dialog).getAllByRole('listitem').map((li) => li.textContent)).toEqual(messages);
  });

  it('shows the server’s reason when the filter cannot be translated', async () => {
    server.use(http.post('/api/projects/p1/issues/jql', () =>
      HttpResponse.json({ title: "Unknown field 'bogus'." }, { status: 400 })));
    const user = userEvent.setup();
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Copy JQL' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("Unknown field 'bogus'.");
  });
});

describe('JqlActions on the issue list', () => {
  it('asks for the JQL of the filter the list is queried with, and its sort', async () => {
    server.use(
      http.get('/api/filter-catalog', () => HttpResponse.json(FILTER_CATALOG)),
      noSavedViews,
      http.post('/api/projects/p1/issues/query', () =>
        HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })),
    );
    const bodies = mockJql();
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, 'writeText');
    const filter = {
      logic: 'And',
      conditions: [
        { field: 'summary', op: 'Contains', value: 'login' },
        { field: 'status', op: 'Eq', value: 'Done', disabled: true },
      ],
    };
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter
          initialEntries={[`/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(filter))}&sortBy=priority&sortDesc=false`]}
        >
          <Routes>
            <Route path="/projects/:id/issues" element={<IssueList />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await user.click(await screen.findByRole('button', { name: 'Copy JQL' }));

    await waitFor(() => expect(bodies).toEqual([{
      filter: { logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }] },
      sortBy: 'priority',
      sortDescending: false,
    }]));
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/JqlActions.test.tsx`
Expected: FAIL — `Failed to resolve import "./JqlActions"`.

- [ ] **Step 3: Write the API call, the query key, the component, and wire it into the toolbar**

In `src/JiraUnpacked.Web/src/api/issues.ts`, replace:

```ts
export interface ExportView {
```

with:

```ts
/** What POST /issues/jql takes: the list's pruned filter (absent = none) and its sort. */
export interface JqlRequest {
  filter?: FilterGroup;
  sortBy: string;
  sortDescending: boolean;
}

/** One way the JQL differs from the filter: a part left out, an approximation, a sort JQL cannot do. */
export interface JqlWarning {
  message: string;
}

/** The filter as JQL, a link that opens it in this project's Jira, and every difference from the filter. */
export interface JqlResult {
  jql: string;
  url: string;
  warnings: JqlWarning[];
}

/** The same 404s and 400s as the issue query: the server checks the filter with the same compiler first. */
export const getIssueJql = (projectId: string, request: JqlRequest) =>
  apiFetch<JqlResult>(`/api/projects/${projectId}/issues/jql`, { method: 'POST', body: request });

export interface ExportView {
```

In `src/JiraUnpacked.Web/src/api/queryKeys.ts`, replace:

```ts
  issueDetail: (projectId: string, jiraIssueId: string) =>
```

with:

```ts
  // Its own segment, not under 'issues': nothing that refreshes the list should drop it, and its
  // request (filter + sort) is part of the key, so one filter's JQL never answers for another.
  issueJql: (projectId: string, request: unknown) =>
    ['projects', projectId, 'jql', JSON.stringify(request ?? {})] as const,
  issueDetail: (projectId: string, jiraIssueId: string) =>
```

Create `src/JiraUnpacked.Web/src/features/issues/JqlActions.tsx`:

```tsx
import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { getIssueJql, type JqlRequest, type JqlResult } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Button, Modal, Toast } from '../../ui';

/** How long "JQL copied" stays up. */
const TOAST_MS = 4000;

/** Only a web address is ever opened, whatever the server sends. */
const isWebUrl = (url: string): boolean => /^https?:\/\//i.test(url);

interface JqlActionsProps {
  projectId: string;
  /** The list's pruned filter and sort — exactly what the issue query is sent. */
  request: JqlRequest;
}

/**
 * Copy JQL and Open in Jira for the list on screen and — once the JQL is known — a "⚠ N
 * differences" button listing what JQL could not say the same way.
 *
 * Fetched on demand (hover, focus, click), not on every edit: the list already sends a request per
 * edit, and the JQL is wanted only when asked for. Hover and focus start it early so a click usually
 * finds it ready — which matters for Open in Jira, because a tab opened long after the click is what
 * popup blockers block.
 */
export function JqlActions({ projectId, request }: JqlActionsProps) {
  const queryClient = useQueryClient();
  const options = {
    queryKey: queryKeys.issueJql(projectId, request),
    queryFn: () => getIssueJql(projectId, request),
    staleTime: 30_000,
  };
  // Only watches the cache, for THIS filter: an earlier filter's differences are never shown.
  const { data } = useQuery({ ...options, enabled: false });

  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [manualCopy, setManualCopy] = useState<string | null>(null);
  const [showWarnings, setShowWarnings] = useState(false);

  useEffect(() => {
    if (!copied) return undefined;
    const timer = setTimeout(() => setCopied(false), TOAST_MS);
    return () => clearTimeout(timer);
  }, [copied]);

  const prefetch = () => {
    void queryClient.prefetchQuery(options);
  };

  async function load(): Promise<JqlResult | undefined> {
    setError(null);
    try {
      return await queryClient.fetchQuery(options);
    } catch (e) {
      setError(e instanceof ApiError ? e.title : 'Could not build the JQL.');
      return undefined;
    }
  }

  async function copy() {
    const result = await load();
    if (!result) return;
    try {
      // Missing outside a secure context, refused when the browser says no: either way the JQL is
      // shown to copy by hand rather than lost.
      if (!navigator.clipboard?.writeText) throw new Error('No clipboard');
      await navigator.clipboard.writeText(result.jql);
      setCopied(true);
    } catch {
      setManualCopy(result.jql);
    }
  }

  async function openInJira() {
    const result = await load();
    if (!result) return;
    if (!isWebUrl(result.url)) {
      setError('The Jira link is not a web address.');
      return;
    }
    window.open(result.url, '_blank', 'noopener,noreferrer');
  }

  const differences = data?.warnings.length ?? 0;

  return (
    <>
      <Button type="button" variant="secondary" onMouseEnter={prefetch} onFocus={prefetch} onClick={() => void copy()}>
        Copy JQL
      </Button>
      <Button type="button" variant="secondary" onMouseEnter={prefetch} onFocus={prefetch} onClick={() => void openInJira()}>
        Open in Jira
      </Button>
      {differences > 0 && (
        <Button type="button" variant="ghost" size="sm" aria-haspopup="dialog" onClick={() => setShowWarnings(true)}>
          <span aria-hidden="true">⚠</span> {differences} {differences === 1 ? 'difference' : 'differences'}
        </Button>
      )}
      {error && <p role="alert" className="text-sm text-[var(--ju-danger)]">{error}</p>}

      <Modal open={showWarnings} onClose={() => setShowWarnings(false)} title="How the JQL differs from this filter">
        <ul className="flex list-disc flex-col gap-2 pl-5 text-sm text-[var(--ju-text)]">
          {data?.warnings.map((w) => <li key={w.message}>{w.message}</li>)}
        </ul>
        <div className="mt-4 flex justify-end">
          <Button type="button" variant="secondary" onClick={() => setShowWarnings(false)}>Close</Button>
        </div>
      </Modal>

      <Modal open={manualCopy !== null} onClose={() => setManualCopy(null)} title="Copy the JQL">
        <p className="text-sm text-[var(--ju-text-secondary)]">
          The browser did not let the page copy it. Select the JQL and copy it yourself.
        </p>
        <textarea
          readOnly
          aria-label="JQL"
          rows={4}
          value={manualCopy ?? ''}
          onFocus={(e) => e.currentTarget.select()}
          className="mt-2 w-full rounded-md border border-[var(--ju-border)] bg-[var(--ju-surface)] p-2 font-mono text-xs text-[var(--ju-text)]"
        />
        <div className="mt-4 flex justify-end">
          <Button type="button" variant="secondary" onClick={() => setManualCopy(null)}>Close</Button>
        </div>
      </Modal>

      {copied && (
        <div className="fixed bottom-4 right-4 z-50">
          <Toast message="JQL copied" onDismiss={() => setCopied(false)} />
        </div>
      )}
    </>
  );
}
```

In `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`:

Replace:

```tsx
import type { IssueFilterRequest, IssueListItem, PagedResult } from '../../api/issues';
```

with:

```tsx
import type { IssueFilterRequest, IssueListItem, JqlRequest, PagedResult } from '../../api/issues';
```

Replace:

```tsx
import { FilterSummary } from './FilterSummary';
```

with:

```tsx
import { FilterSummary } from './FilterSummary';
import { JqlActions } from './JqlActions';
```

Replace:

```tsx
  const sentence = useMemo(() => describeFilter(filter), [filter]);
```

with:

```tsx
  const sentence = useMemo(() => describeFilter(filter), [filter]);
  // What Copy JQL / Open in Jira translate: the very filter and sort the list is queried with.
  const jqlRequest: JqlRequest = useMemo(
    () => (filter ? { filter, sortBy, sortDescending } : { sortBy, sortDescending }),
    [filter, sortBy, sortDescending],
  );
```

Replace:

```tsx
        <div className="flex items-center gap-2">
```

with:

```tsx
        <div className="flex flex-wrap items-center justify-end gap-2">
```

Replace:

```tsx
            Export CSV
          </Button>
        </div>
```

with:

```tsx
            Export CSV
          </Button>
          <JqlActions projectId={id} request={jqlRequest} />
        </div>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/JqlActions.test.tsx && npm run typecheck && npm run lint && npx vitest run src/features/issues src/api`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git -C C:/jw-filters add src/JiraUnpacked.Web/src/api/issues.ts src/JiraUnpacked.Web/src/api/queryKeys.ts src/JiraUnpacked.Web/src/features/issues/JqlActions.tsx src/JiraUnpacked.Web/src/features/issues/JqlActions.test.tsx src/JiraUnpacked.Web/src/features/issues/IssueList.tsx
git -C C:/jw-filters commit -m "feat(web): Copy JQL, Open in Jira and the list of differences" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Click-to-filter from the table (SPA)

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/cellFilter.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/cellFilter.test.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/CellFilterMenu.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/CellFilter.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx` (one cell lookup)

**Interfaces:**
- Consumes: `countEnabled`, `newId`, `toWireCondition`, `pruneTree`, `EditorGroup`, `EditorCondition` (`filterTree.ts`); `isOperatorAllowed` (`filterCatalogHelpers.ts`); `IssueColumn` (`columns.ts`); IssueList's `catalog`, `knownFields`, `tree`, `setTree`, `setExpanded` (Task 7).
- Produces: `type CellValue = string | number | null`; `type CellFilterMode = 'include' | 'exclude'`; `CELL_FILTER_VALUES: Readonly<Record<string, (item: IssueListItem) => CellValue>>`; `cellCondition(field, value, mode): FilterCondition`; `treeDepth(g: EditorGroup): number`; `needsWrap(root: EditorGroup): boolean`; `cellFilterHint(root: EditorGroup, catalog: FilterCatalog): string | undefined`; `addCellCondition(root: EditorGroup, condition: FilterCondition): EditorGroup`; `CellFilterMenu({ column, value, includeHint?, excludeHint?, onPick })`.

- [ ] **Step 1: Write the failing pure tests**

Create `src/JiraUnpacked.Web/src/features/issues/cellFilter.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import type { ConditionValue, FilterLogic, FilterOperator } from '../../api/issues';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { addCellCondition, cellCondition, cellFilterHint, treeDepth } from './cellFilter';
import { newId, pruneTree, type EditorCondition, type EditorGroup } from './filterTree';

const cond = (field: string, op: FilterOperator, value?: ConditionValue, disabled = false): EditorCondition => ({
  id: newId(), field, op, ...(value !== undefined ? { value } : {}), ...(disabled ? { disabled: true } : {}),
});

const group = (logic: FilterLogic, conditions: EditorCondition[], groups: EditorGroup[] = [], disabled = false): EditorGroup => ({
  id: newId(), logic, conditions, groups, ...(disabled ? { disabled: true } : {}),
});

const STATUS_DONE = { field: 'status', op: 'Eq' as const, value: 'Done' };

describe('cellCondition', () => {
  it('is / is not a value, and is empty / is not empty for an empty cell', () => {
    expect(cellCondition('status', 'Done', 'include')).toEqual({ field: 'status', op: 'Eq', value: 'Done' });
    expect(cellCondition('status', 'Done', 'exclude')).toEqual({ field: 'status', op: 'Neq', value: 'Done' });
    expect(cellCondition('storyPoints', 3, 'include')).toEqual({ field: 'storyPoints', op: 'Eq', value: 3 });
    expect(cellCondition('assignee', null, 'include')).toEqual({ field: 'assignee', op: 'IsNull' });
    expect(cellCondition('assignee', null, 'exclude')).toEqual({ field: 'assignee', op: 'IsNotNull' });
  });
});

describe('addCellCondition', () => {
  it('appends to an All root', () => {
    const next = addCellCondition(group('And', [cond('priority', 'Eq', 'High')]), STATUS_DONE);
    expect(pruneTree(next)).toEqual({
      logic: 'And', conditions: [{ field: 'priority', op: 'Eq', value: 'High' }, STATUS_DONE],
    });
  });

  it('wraps an Any root that holds something, so the click narrows', () => {
    const root = group('Or', [cond('priority', 'Eq', 'High'), cond('priority', 'Eq', 'Low')]);
    const next = addCellCondition(root, STATUS_DONE);
    expect(next.groups[0]).toBe(root);
    expect(pruneTree(next)).toEqual({
      logic: 'And',
      conditions: [STATUS_DONE],
      groups: [{ logic: 'Or', conditions: [{ field: 'priority', op: 'Eq', value: 'High' }, { field: 'priority', op: 'Eq', value: 'Low' }] }],
    });
  });

  it('adds straight to an Any root whose parts are all switched off', () => {
    const next = addCellCondition(group('Or', [cond('priority', 'Eq', 'High', true)]), STATUS_DONE);
    expect(next.logic).toBe('Or');
    expect(next.conditions).toHaveLength(2);
  });

  it('wraps a switched-off root, which stays off inside', () => {
    const root = group('And', [cond('priority', 'Eq', 'High')], [], true);
    const next = addCellCondition(root, STATUS_DONE);
    expect(next.disabled).toBeUndefined();
    expect(next.groups[0]).toBe(root);
    expect(pruneTree(next)).toEqual({ logic: 'And', conditions: [STATUS_DONE] });
  });

  it('returns the same tree for a condition already switched on in the root, not for one switched off', () => {
    const on = group('And', [cond('status', 'Eq', 'Done')]);
    expect(addCellCondition(on, STATUS_DONE)).toBe(on);
    const off = group('And', [cond('status', 'Eq', 'Done', true)]);
    expect(addCellCondition(off, STATUS_DONE).conditions).toHaveLength(2);
  });
});

describe('cellFilterHint', () => {
  it('is undefined while the condition fits', () => {
    expect(cellFilterHint(group('And', [cond('status', 'Eq', 'Done')]), FILTER_CATALOG)).toBeUndefined();
  });

  it('names the condition limit', () => {
    const full = group('And', Array.from({ length: 20 }, (_, i) => cond('summary', 'Contains', `w${i}`)));
    expect(cellFilterHint(full, FILTER_CATALOG)).toBe('Limit: 20 conditions');
  });

  it('names the depth and group limits only when the root must be wrapped', () => {
    const deep = group('Or', [cond('status', 'Eq', 'Done')], [
      group('And', [cond('priority', 'Eq', 'High')], [group('And', [cond('team', 'IsNull')])]),
    ]);
    expect(treeDepth(deep)).toBe(3);
    expect(cellFilterHint(deep, FILTER_CATALOG)).toBe('Limit: 3 levels of groups');
    expect(cellFilterHint({ ...deep, logic: 'And' }, FILTER_CATALOG)).toBeUndefined();

    const wide = group('Or', [], Array.from({ length: 9 }, () => group('And', [cond('status', 'Eq', 'Done')])));
    expect(cellFilterHint(wide, FILTER_CATALOG)).toBe('Limit: 10 groups');
  });
});
```

- [ ] **Step 2: Run them to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/cellFilter.test.ts`
Expected: FAIL — `Failed to resolve import "./cellFilter"`.

- [ ] **Step 3: Write `cellFilter`**

Create `src/JiraUnpacked.Web/src/features/issues/cellFilter.ts`:

```ts
import type { FilterCatalog, FilterCondition, IssueListItem } from '../../api/issues';
import { countEnabled, newId, toWireCondition, type EditorCondition, type EditorGroup } from './filterTree';

/** A cell's value as a filter takes it: a name, a number (Points), the parent's Jira id — or null for an empty cell. */
export type CellValue = string | number | null;

export type CellFilterMode = 'include' | 'exclude';

/**
 * The columns whose cells offer "Filter to" / "Exclude", and each cell's value. Column ids are the
 * filter's own field names (see columns.ts), so the column IS the field.
 */
export const CELL_FILTER_VALUES: Readonly<Record<string, (item: IssueListItem) => CellValue>> = {
  status: (i) => i.status,
  statusCategory: (i) => i.statusCategory,
  issueType: (i) => i.issueType,
  priority: (i) => i.priority,
  assignee: (i) => i.assignee,
  sprint: (i) => i.sprint,
  team: (i) => i.team,
  storyPoints: (i) => i.storyPoints,
  parent: (i) => i.parentJiraIssueId,
};

/** "Filter to" a value is `is`, "Exclude" it `is not`; on an empty cell, is empty / is not empty. */
export function cellCondition(field: string, value: CellValue, mode: CellFilterMode): FilterCondition {
  if (value === null) return { field, op: mode === 'include' ? 'IsNull' : 'IsNotNull' };
  return { field, op: mode === 'include' ? 'Eq' : 'Neq', value };
}

/** Nesting depth, the root at 1, counting switched-off groups too: switched back on, they must still fit. */
export function treeDepth(g: EditorGroup): number {
  return 1 + g.groups.reduce((deepest, child) => Math.max(deepest, treeDepth(child)), 0);
}

/**
 * Adding straight to the root would WIDEN an Any root that already holds something, and change
 * nothing under a switched-off root — so then the root is wrapped first: a new All holding the old
 * root as a group, plus the new condition. A click on a cell always narrows the list.
 */
export function needsWrap(root: EditorGroup): boolean {
  if (root.disabled) return true;
  return root.logic === 'Or'
    && (root.conditions.some((c) => !c.disabled) || root.groups.some((g) => !g.disabled));
}

/** Why a cell filter cannot be added now — the builder's own limit hints — or undefined when it fits. */
export function cellFilterHint(root: EditorGroup, catalog: FilterCatalog): string | undefined {
  const counts = countEnabled(root);
  if (counts.conditions + 1 > catalog.maxConditions) return `Limit: ${catalog.maxConditions} conditions`;
  if (needsWrap(root)) {
    if (counts.groups + 1 > catalog.maxGroups) return `Limit: ${catalog.maxGroups} groups`;
    if (treeDepth(root) + 1 > catalog.maxDepth) return `Limit: ${catalog.maxDepth} levels of groups`;
  }
  return undefined;
}

const sameCondition = (a: FilterCondition, b: FilterCondition): boolean =>
  JSON.stringify(toWireCondition(a)) === JSON.stringify(toWireCondition(b));

/**
 * The tree with `condition` added to its root, wrapping first where needsWrap says so. The SAME
 * tree comes back when an identical condition is already switched on in the root, so a second
 * click adds nothing. Callers check cellFilterHint first.
 */
export function addCellCondition(root: EditorGroup, condition: FilterCondition): EditorGroup {
  const row: EditorCondition = { ...condition, id: newId() };
  if (needsWrap(root)) return { id: newId(), logic: 'And', conditions: [row], groups: [root] };
  if (root.conditions.some((c) => !c.disabled && sameCondition(c, condition))) return root;
  return { ...root, conditions: [...root.conditions, row] };
}
```

- [ ] **Step 4: Run the pure tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/cellFilter.test.ts`
Expected: PASS.

- [ ] **Step 5: Write the failing list tests**

Create `src/JiraUnpacked.Web/src/features/issues/CellFilter.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import IssueList from './IssueList';
import type { IssueListItem } from '../../api/issues';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { noSavedViews } from '../../test/viewsFixture';

const ROW: IssueListItem = {
  jiraIssueId: '10001', jiraKey: 'ACME-1', summary: 'Login fails', status: 'Open', statusCategory: 'To Do',
  issueType: 'Bug', priority: 'High', assignee: null, sprint: 'Sprint 1', team: 'Core', storyPoints: 3,
  parentJiraIssueId: '10000', jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-01-02T00:00:00Z',
};

type Body = { filter?: unknown; page?: number };

function setup(url = '/projects/p1/issues'): { bodies: Body[]; user: UserEvent } {
  const bodies: Body[] = [];
  server.use(
    http.get('/api/filter-catalog', () => HttpResponse.json(FILTER_CATALOG)),
    noSavedViews,
    http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })),
    http.post('/api/projects/p1/issues/query', async ({ request }) => {
      const body = (await request.json()) as Body;
      bodies.push(body);
      return HttpResponse.json({ items: [ROW], page: body.page ?? 1, pageSize: 50, totalCount: 120 });
    }),
  );
  const user = userEvent.setup({ delay: null });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { bodies, user };
}

const link = (filter: unknown, extra = '') =>
  `/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(filter))}${extra}`;

async function pick(user: UserEvent, cell: string, item: string) {
  await user.click(await screen.findByRole('button', { name: cell }));
  await user.click(screen.getByRole('button', { name: item }));
}

describe('click-to-filter', () => {
  it('filters to a cell’s value and shows the builder with it', async () => {
    const { bodies, user } = setup();

    await pick(user, 'Filter by Status Open', 'Filter to "Open"');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }],
    }));
    expect(screen.getByLabelText('Field for condition 1')).toHaveValue('status');
  });

  it('excludes a value, and an empty cell filters to empty or not empty', async () => {
    const { bodies, user } = setup();

    await pick(user, 'Filter by Status Open', 'Exclude "Open"');
    await pick(user, 'Filter by Assignee (Empty)', 'Filter to (Empty)');
    await pick(user, 'Filter by Assignee (Empty)', 'Exclude (Empty)');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And',
      conditions: [
        { field: 'status', op: 'Neq', value: 'Open' },
        { field: 'assignee', op: 'IsNull' },
        { field: 'assignee', op: 'IsNotNull' },
      ],
    }));
  });

  it('sends points as a number and parent as the parent’s id, and opens a collapsed builder', async () => {
    const { bodies, user } = setup(link(
      { logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }] },
      '&cols=jiraKey,status,storyPoints,parent',
    ));
    expect(await screen.findByRole('button', { name: 'Edit filter' })).toBeInTheDocument();

    await pick(user, 'Filter by Points 3', 'Filter to "3"');
    await pick(user, 'Filter by Parent 10000', 'Filter to "10000"');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And',
      conditions: [
        { field: 'summary', op: 'Contains', value: 'login' },
        { field: 'storyPoints', op: 'Eq', value: 3 },
        { field: 'parent', op: 'Eq', value: '10000' },
      ],
    }));
    expect(screen.getByRole('button', { name: 'Collapse' })).toBeInTheDocument();

    // Parent has no "is not": Exclude is offered, disabled, and says why.
    await user.click(screen.getByRole('button', { name: 'Filter by Parent 10000' }));
    const exclude = screen.getByRole('button', { name: 'Exclude "10000"' });
    expect(exclude).toBeDisabled();
    expect(exclude).toHaveAttribute('title', 'Not available for Parent');
  });

  it('wraps an Any root so a click always narrows', async () => {
    const any = {
      logic: 'Or',
      conditions: [{ field: 'priority', op: 'Eq', value: 'High' }, { field: 'priority', op: 'Eq', value: 'Low' }],
    };
    const { bodies, user } = setup(link(any));

    await pick(user, 'Filter by Status Open', 'Filter to "Open"');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }], groups: [any],
    }));
  });

  it('adds an identical condition only once', async () => {
    const { user } = setup(link({ logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }] }));

    await pick(user, 'Filter by Status Open', 'Filter to "Open"');

    expect(await screen.findByRole('button', { name: 'Collapse' })).toBeInTheDocument();
    expect(screen.getAllByLabelText(/^Field for condition/)).toHaveLength(1);
  });

  it('disables both items at the condition limit, with the usual hint', async () => {
    const full = {
      logic: 'And',
      conditions: Array.from({ length: 20 }, (_, i) => ({ field: 'summary', op: 'Contains', value: `w${i}` })),
    };
    const { user } = setup(link(full));

    await user.click(await screen.findByRole('button', { name: 'Filter by Status Open' }));

    for (const name of ['Filter to "Open"', 'Exclude "Open"']) {
      const item = screen.getByRole('button', { name });
      expect(item).toBeDisabled();
      expect(item).toHaveAttribute('title', 'Limit: 20 conditions');
    }
  });

  it('works from the keyboard and closes on Escape, focus back on the button', async () => {
    const { bodies, user } = setup();
    const trigger = await screen.findByRole('button', { name: 'Filter by Status Open' });
    trigger.focus();

    await user.keyboard('{Enter}');
    expect(screen.getByRole('group', { name: 'Filter by Status' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('group', { name: 'Filter by Status' })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();

    await user.keyboard('{Enter}');
    await user.tab();
    expect(screen.getByRole('button', { name: 'Filter to "Open"' })).toHaveFocus();
    await user.keyboard('{Enter}');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }],
    }));
  });

  it('starts the filtered list on page 1', async () => {
    const { bodies, user } = setup();
    await user.click(await screen.findByRole('button', { name: /next/i }));
    await waitFor(() => expect(bodies.at(-1)?.page).toBe(2));

    await pick(user, 'Filter by Status Open', 'Filter to "Open"');

    await waitFor(() => expect(bodies.at(-1)).toMatchObject({
      page: 1, filter: { logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }] },
    }));
  });
});
```

- [ ] **Step 6: Run them to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/CellFilter.test.tsx`
Expected: FAIL — `Unable to find role="button" and name "Filter by Status Open"`.

- [ ] **Step 7: Write the menu and wire it into the table**

Create `src/JiraUnpacked.Web/src/features/issues/CellFilterMenu.tsx`:

```tsx
import { useEffect, useId, useRef, useState } from 'react';
import type { CellFilterMode, CellValue } from './cellFilter';

interface CellFilterMenuProps {
  /** The column's header as the table shows it ("Status", "Points"). */
  column: string;
  value: CellValue;
  /** Why "Filter to" cannot be used now (a limit, or an operator the field lacks); undefined = it can. */
  includeHint?: string;
  excludeHint?: string;
  onPick: (mode: CellFilterMode) => void;
}

const ITEM_CLASS =
  'rounded px-2 py-1 text-left text-sm text-[var(--ju-text)] hover:bg-[var(--ju-surface-2)] disabled:cursor-not-allowed disabled:opacity-50';

/**
 * The ⋯ button in a filterable cell and its two choices. Visible on row hover and on keyboard
 * focus. A popover of plain buttons, not an ARIA menu (no arrow-key model to honour) — the same
 * choice the Views menu makes. Escape closes it and returns focus to the button; a click
 * elsewhere closes it.
 */
export function CellFilterMenu({ column, value, includeHint, excludeHint, onPick }: CellFilterMenuProps) {
  const [open, setOpen] = useState(false);
  const popoverId = useId();
  const boxRef = useRef<HTMLSpanElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const shown = value === null ? '(Empty)' : String(value);
  const quoted = value === null ? '(Empty)' : `"${shown}"`;

  useEffect(() => {
    if (!open) return undefined;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      setOpen(false);
      triggerRef.current?.focus();
    };
    const onPointer = (e: MouseEvent) => {
      if (!boxRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onPointer);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('mousedown', onPointer);
    };
  }, [open]);

  const pick = (mode: CellFilterMode) => {
    setOpen(false);
    onPick(mode);
  };

  return (
    <span ref={boxRef} className="relative inline-flex">
      <button
        ref={triggerRef}
        type="button"
        aria-label={`Filter by ${column} ${shown}`}
        aria-expanded={open}
        aria-controls={open ? popoverId : undefined}
        className="rounded px-1 text-[var(--ju-muted)] opacity-0 hover:text-[var(--ju-text)] focus-visible:opacity-100 group-hover:opacity-100 aria-expanded:opacity-100"
        onClick={() => setOpen((o) => !o)}
      >
        <span aria-hidden="true">⋯</span>
      </button>
      {open && (
        <span
          id={popoverId}
          role="group"
          aria-label={`Filter by ${column}`}
          className="absolute left-0 top-full z-10 mt-1 flex min-w-max flex-col rounded-md border border-[var(--ju-border)] bg-[var(--ju-surface)] p-1 shadow-sm"
        >
          <button
            type="button"
            className={ITEM_CLASS}
            disabled={includeHint !== undefined}
            title={includeHint}
            onClick={() => pick('include')}
          >
            Filter to {quoted}
          </button>
          <button
            type="button"
            className={ITEM_CLASS}
            disabled={excludeHint !== undefined}
            title={excludeHint}
            onClick={() => pick('exclude')}
          >
            Exclude {quoted}
          </button>
        </span>
      )}
    </span>
  );
}
```

In `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`:

Replace:

```tsx
import { ISSUE_COLUMNS, ALWAYS_ON } from './columns';
```

with:

```tsx
import { ISSUE_COLUMNS, ALWAYS_ON, type IssueColumn } from './columns';
import { isOperatorAllowed } from './filterCatalogHelpers';
import {
  CELL_FILTER_VALUES, addCellCondition, cellCondition, cellFilterHint, type CellFilterMode, type CellValue,
} from './cellFilter';
import { CellFilterMenu } from './CellFilterMenu';
```

Replace:

```tsx
  const count = data && !isPlaceholderData ? data.totalCount : undefined;
```

with:

```tsx
  const count = data && !isPlaceholderData ? data.totalCount : undefined;

  // Click-to-filter. One limit hint for every cell (the tree is the same for all of them); the
  // builder opens so the user sees what the click added. Page 1 follows from the tree changing.
  const cellHint = catalog ? cellFilterHint(tree, catalog) : undefined;
  const filterByCell = (field: string, value: CellValue, mode: CellFilterMode) => {
    setTree(addCellCondition(tree, cellCondition(field, value, mode)));
    setExpanded(true);
  };
  /** A filterable cell (a filter field this catalog still has) carries its ⋯ menu; any other is the plain render. */
  const renderCell = (col: IssueColumn, item: IssueListItem) => {
    const read = CELL_FILTER_VALUES[col.id];
    if (!catalog || !read || !knownFields?.has(col.id)) return col.render(item);
    const value = read(item);
    const hintFor = (mode: CellFilterMode) =>
      cellHint ?? (isOperatorAllowed(catalog, col.id, cellCondition(col.id, value, mode).op)
        ? undefined
        : `Not available for ${col.header}`);
    return (
      <span className="inline-flex items-center gap-1">
        {col.render(item)}
        <CellFilterMenu
          column={col.header}
          value={value}
          includeHint={hintFor('include')}
          excludeHint={hintFor('exclude')}
          onPick={(mode) => filterByCell(col.id, value, mode)}
        />
      </span>
    );
  };
```

Replace:

```tsx
                <TR key={item.jiraIssueId}>
                  {visibleColumns.map((col) => (
                    <TD key={col.id}>
                      {/* Key is the only cell with behaviour of its own: it links to the issue.
                          Everything else is the catalog's plain render. */}
```

with:

```tsx
                <TR key={item.jiraIssueId} className="group">
                  {visibleColumns.map((col) => (
                    <TD key={col.id}>
                      {/* Key links to the issue; a filterable column carries its ⋯ menu (renderCell);
                          everything else is the catalog's plain render. */}
```

Replace:

```tsx
                      ) : col.render(item)}
```

with:

```tsx
                      ) : renderCell(col, item)}
```

In `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx`, a filterable cell's accessible name now also includes its ⋯ button ("To Do Filter by Category To Do"), so replace:

```tsx
    expect(screen.getByRole('cell', { name: 'To Do' })).toBeInTheDocument();
```

with:

```tsx
    expect(screen.getByRole('cell', { name: /^To Do/ })).toBeInTheDocument();
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/CellFilter.test.tsx src/features/issues/cellFilter.test.ts && npm run typecheck && npm run lint && npx vitest run`
Expected: PASS (whole web suite).

- [ ] **Step 9: Commit**

```bash
git -C C:/jw-filters add src/JiraUnpacked.Web/src/features/issues/cellFilter.ts src/JiraUnpacked.Web/src/features/issues/cellFilter.test.ts src/JiraUnpacked.Web/src/features/issues/CellFilterMenu.tsx src/JiraUnpacked.Web/src/features/issues/CellFilter.test.tsx src/JiraUnpacked.Web/src/features/issues/IssueList.tsx src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx
git -C C:/jw-filters commit -m "feat(web): click-to-filter — filter to or exclude a cell's value; an Any root is wrapped so it narrows" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Final verification (after Task 9, before handing back)

- [ ] `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_readability dotnet test JiraUnpacked.sln -v q` → PASS (whole suite, integration included).
- [ ] `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test && npm run build` → PASS.
- [ ] `git -C C:/jw-filters diff origin/develop -- src/JiraUnpacked.Web/package.json src/JiraUnpacked.Web/package-lock.json '*.csproj'` → empty (no dependency changes; still exactly four runtime deps).
- [ ] `git -C C:/jw-filters diff origin/develop -- src | grep -E "^\+.*(IgnoreQueryFilters|FromSql)"` → no output.
- [ ] `git -C C:/jw-filters diff origin/develop -- src/JiraUnpacked.Web/src | grep -E "^\+.*localStorage"` → no output (the collapse toggle is component state).
- [ ] `git -C C:/jw-filters diff origin/develop -- src/JiraUnpacked.Web/src/ui` → empty (`src/ui` untouched; it still imports neither `api/` nor `auth/`).
- [ ] `git -C C:/jw-filters status` clean; `git -C C:/jw-filters log --oneline origin/develop..` shows the spec commit, this plan's commit and nine task commits. Nothing pushed.

---

## Self-Review

**Spec coverage.**
§1 Readable summary — `describeFilter` over the pruned tree, operator words from `OPERATOR_LABELS`, values listed with "… and N more" past five, (Empty) as "empty", smart labels, relative dates / periods with zone / Between dates, history (was, changed from/to/by/during, changed ≥ N times), nested groups parenthesised with the root's logic as the joining word, empty → "All issues", column headers as field names: Task 6. Summary line `Showing: …` with the count, Collapse / Edit filter toggle, collapsed = builder unmounted, component state only, expanded on a bare URL and collapsed when a filter or saved view is loaded on mount: Task 7.
§2 JQL — endpoint, body, response, tenant scope, 404, 400 parity via the compiler first, sort carried: Task 5. `project = KEY AND (tree) ORDER BY`: Task 1. url from the connection's BaseUrl, encoded: Task 5. Table rows — status/issueType/priority/statusCategory with Eq/Neq/In/NotIn and escaping: Task 1; assignee by account id with warning: Task 2; sprint ids and smart functions (active as open-and-not-future): Task 2; team cf[] + no-mapping warning: Task 2; storyPoints cf[] incl. Between + no-mapping warning: Task 2; (Empty) `is EMPTY` / `is not EMPTY` / `In` with (Empty) OR-ed: Tasks 1–2; summary ~ with one word-based warning: Task 1; jiraKey whole keys, partial → warning: Task 1; parent: Task 1; createdAt/updatedAt comparisons and Between with UTC dates: Task 3; InLast/NotInLast incl. months as 30 days + warning: Task 3; InPeriod functions with offsets, quarters as fixed dates with the server clock + warning, one zone/week-start warning: Task 3; history Was / Changed (FROM/TO/BY/DURING/AFTER) for the WAS/CHANGED fields, others and ChangeCount as warnings: Task 4. Omitted parts named, narrower/wider by the holding group, nothing left → `project = KEY ORDER BY …`: Task 1. Strings quoted/escaped, ids only from the database: Tasks 1, 5 (tenant isolation test). UI — Copy JQL (toast "JQL copied"), Open in Jira (new tab, noopener noreferrer), both with the current pruned filter, "⚠ N differences" opening the list: Task 8.
§3 Click-to-filter — filterable columns incl. statusCategory, storyPoints (number), parent (id); ⋯ button visible on hover and focus with `aria-label="Filter by <Column> <value>"`; Filter to / Exclude; empty cell → IsNull / IsNotNull; added to the root; Any root with enabled parts wrapped; limits disable the items with the usual hint; duplicates suppressed; page 1; builder expands: Task 9.
§4 Testing — translator unit tests for every table row incl. escaping, smart sprints, quarter range with a fixed clock, each warning, Or → narrower/wider: Tasks 1–4; endpoint integration: tenant isolation, 404, 400 parity, url encoding with the connection's base URL: Task 5; web: summary + collapse + initial rule (Task 7), Copy JQL with a mocked clipboard and Open in Jira with `window.open` asserted (Task 8), warnings list (Task 8), click-to-filter on each column type incl. empty cell, Or-root wrap, limits, duplicates, keyboard (Task 9), describeFilter (Task 6).

**Placeholder scan.** No TBD/TODO/"similar to". Every code step shows its code; every edit to an existing file quotes the exact text it replaces. Task 7 Step 6 names the one mechanical follow-up that depends on running the suite (adding `await expandFilter();` to any further test that mounts with a filter and then touches the builder) and states the exact line to add and that nothing else may change.

**Type consistency.** Server: `JqlTranslator.Translate(string projectKey, FilterGroup? filter, string? sortBy, bool sortDescending, JqlLookup lookup)` is what `JqlTestKit.Translate` (Tasks 1–4) and `IssueJqlService` (Task 5) call; `JqlLookup` positional order (AccountIds, SprintIds, TeamIds, TeamField, StoryPointsField) matches Task 2's `new JqlLookup(...)` and Task 5's `LoadLookupAsync`; `Clause.Of/Out`, `Run.Warn`, `ReadPick`, `Dictionary(field, op, literals, wantsEmpty, extra, whyNone)` (Task 1) are used unchanged by Tasks 2–4; `ReadRange`, `TryReadDate`, `Relative`, `Period` → `PeriodBounds(Start, End, EndInclusive)` (Task 3) are what Task 4's `Window` calls; each task's dispatch edit quotes the switch lines the previous task left. `IssueJqlRequest`/`IssueJqlView`/`JqlWarning` serialise to `{ filter, sortBy, sortDescending }` / `{ jql, url, warnings: [{ message }] }`, which `JqlRequest`/`JqlResult` (Task 8) mirror. Web: `describeFilter(filter: FilterGroup | undefined)` (Task 6) is what IssueList calls (Task 7); `setExpanded` and `BUILDER_ID` (Task 7) are what Task 9 uses; `JqlRequest`, `getIssueJql`, `queryKeys.issueJql` (Task 8) are defined before `JqlActions` uses them; `CellValue`, `CellFilterMode`, `CELL_FILTER_VALUES`, `cellCondition`, `cellFilterHint`, `addCellCondition` (Task 9) match the imports in IssueList and `CellFilterMenu`; the accessible names in the tests are the ones the components render.

**Review Focus.** Six lines above, each pinned by a named test in its owning task (1: Tasks 2, 5; 2: Tasks 1, 5; 3: Tasks 1, 4; 4: Task 8; 5: Task 7; 6: Task 9).
