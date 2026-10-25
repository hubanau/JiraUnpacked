# Issue Charts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A chart panel on the issue list — group the filtered issues by a field, measure issues or story points, show as bar / donut / table, click to drill down.

**Architecture:** One new read endpoint `POST /api/projects/{id}/issues/chart` reusing the shared filter compiler and grouping by name in one SQL `GROUP BY`; the SPA adds three URL params owned by `useListState`, pure helpers for sort/top-N/click conditions, three hand-drawn chart components and a panel beside the table.

**Tech Stack:** ASP.NET Core 9 + EF Core/Npgsql, xUnit integration harness (real Postgres); React 19, TanStack Query 5, Tailwind 4, Vitest + Testing Library + MSW.

**Spec:** `docs/superpowers/specs/2026-10-25-issue-charts-design.md`

## Global Constraints

- No charting library; bars are CSS (`BarRow` idiom), the donut is hand-written SVG.
- `groupBy` allow-list, exactly: `status, statusCategory, issueType, priority, assignee, sprint, team, storyPoints, createdAt, updatedAt`. No caller string reaches SQL.
- No `measure` in the request; every group carries `issues` and `storyPoints`.
- Group by **name**; (Empty) is `key: null`; statusCategory through the status, blank = empty.
- Months: UTC, `date_trunc('month', x, 'UTC')` semantics, key `"yyyy-MM"`.
- Truncation: keep groups in top 500 by issues ∪ top 500 by story points; dropped → `other {groups, issues, storyPoints}`, `truncated: true`. Months/Points: keep the last (largest) 500 keys.
- Tenant: existence check first → 404; reads through tenant-scoped `AppDbContext`.
- URL params `chart` (groupBy; presence = open), `chartMeasure=points` (absent = issues), `chartType=donut|table` (absent = bar); NOT in `LIST_PARAMS`; written through `useListState`'s `patch`.
- Default on open: groupBy `status`, measure issues, type bar.
- Bar: top 10 + Other. Donut: top 8 + Other, no in-ring label under 3%. Table: all groups.
- Month click → `Between` `['yyyy-MM-01', 'yyyy-MM-<last day>']` (date-only, inclusive).
- localStorage is off-limits (only ju.username / ju.theme).
- Tests run with `JU_TEST_DATABASE` set to a worktree-specific DB (e.g. `ju_test_charts`).

## Review Focus

1. **Chart total ≠ list count** for the same filter (history condition, Or groups) → parity test in Task 1.
2. **Two dictionary rows sharing a name** (sprints/teams across boards) → one group, summed; Task 1 test.
3. **Month boundary** issue at `2026-09-30T23:30Z` lands in Sep, and its click filter matches it → Task 1 (bucket) + Task 4 (Between values).
4. **Filter edited while panel open** → previous chart stays (placeholderData), no flash of empty; Task 6 test.
5. **Clicking (Empty)** for assignee/team → `IsNull`, not `Eq null`; Task 4 test.

---

### Task 1: Chart endpoint (server)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IIssueChartService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/IssueChartService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/ChartGroupTrimmer.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` (register scoped, next to `IFilterValueService`)
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs` (ctor + action)
- Test: `tests/JiraUnpacked.Tests/Reporting/ChartGroupTrimmerTests.cs`, `tests/JiraUnpacked.Tests/Integration/IssueChartTests.cs`

**Interfaces:**
- Produces (Domain):
  ```csharp
  public sealed record IssueChartRequest(IReadOnlyList<FilterCondition>? Conditions, FilterGroup? Filter, string? GroupBy);
  public sealed record ChartGroup(string? Key, int Issues, decimal StoryPoints);
  public sealed record ChartOther(int Groups, int Issues, decimal StoryPoints);
  public sealed record IssueChart(string GroupBy, int TotalIssues, decimal TotalStoryPoints,
      int IssuesWithoutPoints, IReadOnlyList<ChartGroup> Groups, bool Truncated, ChartOther? Other);
  /// Chart + null Error → 200; null Chart + Error → 400; both null → 404.
  public sealed record IssueChartResult(IssueChart? Chart, string? Error);
  public interface IIssueChartService { Task<IssueChartResult> BuildAsync(Guid projectId, IssueChartRequest request, CancellationToken ct = default); }
  ```
  JSON (web defaults, camelCase): `{ groupBy, totalIssues, totalStoryPoints, issuesWithoutPoints, groups: [{ key, issues, storyPoints }], truncated, other }`. (`label` from the spec is dropped: it always equals `key`; the SPA formats.)
- Produces: `ChartGroupTrimmer.Trim(IReadOnlyList<ChartGroup> all, bool keyOrdered, int cap = 500) → (IReadOnlyList<ChartGroup> Kept, ChartOther? Other)`.

- [ ] **Step 1: Unit tests for the trimmer** (`ChartGroupTrimmerTests`)
  - `Keeps_everything_under_the_cap` — 3 groups, cap 500 → all kept, `Other` null.
  - `Keeps_top_by_either_measure` — cap 2; groups A(10 issues, 0 pts), B(9, 0), C(1, 100), D(1, 1) → kept {A, B, C}; Other = (1 group, 1 issue, 1 pt).
  - `Key_ordered_keeps_the_largest_keys` — keyOrdered, cap 2, keys "2026-01","2026-02","2026-03" → kept 02, 03; Other has 01's sums.
  - `Kept_order_is_input_order` — output preserves input order.
- [ ] **Step 2:** Run `dotnet test --filter ChartGroupTrimmerTests` → FAIL (type missing).
- [ ] **Step 3:** Implement `ChartGroupTrimmer.Trim` (pure, in-memory). Run → PASS.
- [ ] **Step 4: Integration tests** (`IssueChartTests`, pattern of `FilterValuesTests`: `MetricsTestData.AuthedTenantAsync`, `SeedProjectAsync`, `SeedIssueAsync` with `storyPoints`/`teamId`, `SeedTeamAsync`; extend `SeedIssueAsync` with optional `DateTime? updatedAt = null` only if needed — a separate SQL helper in the test file is fine). POST `/api/projects/{id}/issues/chart`.
  - `Groups_by_status_with_counts_points_and_empty` — ToDo×2 (3, null pts), Done×1 (5), none×1 (2) → groups in order To Do(2, 3), Done(1, 5), null(1, 2) — issues desc, ties by key asc, null last; `totalIssues 4`, `totalStoryPoints 10`, `issuesWithoutPoints 1`.
  - `Same_named_teams_are_one_group` — two teams named "mobile" → one group with both issues.
  - `Status_category_blank_is_empty` — reuse `StatusCategoryTests`' seeding approach.
  - `Months_bucket_in_utc` — createdAt `2026-09-30T23:30Z` and `2026-10-01T00:30Z` → keys `"2026-09"`, `"2026-10"`, ascending.
  - `Story_points_groups_by_value_ascending_with_null_first_or_last` — pick: null **last** for key-ordered fields; assert.
  - `Total_matches_the_query_count_for_the_same_filter` — a filter with an Or group and a history `ChangeCount` condition (see `HistoryConditionTests` seeding); chart `totalIssues` == `/issues/query` `totalCount`.
  - `Bad_groupBy_is_400_listing_allowed_fields` (theory: `"summary"`, `"nonsense"`, `""`, null) — body contains `'summary'`-style quote and the word `status`.
  - `Invalid_filter_is_the_same_400_as_the_query` — same title text from both endpoints.
  - `Unknown_project_is_404` and `Another_tenants_project_is_404`.
  - Every seeded read is scoped to the test's own project (no global counts).
- [ ] **Step 5:** Run → FAIL (404 route).
- [ ] **Step 6:** Implement `IssueChartService.BuildAsync`: existence check; `_filter.CompileAsync(new ReportFilter(req.Conditions, req.Filter))` → error passthrough; groupBy switch over literals producing `IQueryable<(string? Key, decimal? Points)>` via LEFT JOINs (`GroupJoin`/`DefaultIfEmpty` or correlated subselect like `ToListItem`) so empty is a null key; then `GroupBy(Key).Select(Key, Count, Sum(Points ?? 0))`. Months: `EF.Functions` has no 3-arg date_trunc → use `x.JiraCreatedAt.Year`/`.Month` in a UTC-session-independent way: group by `new { Y = EF.Functions.DateTrunc... }` is NOT available; use `DateTime` parts after `AT TIME ZONE 'UTC'` — simplest correct choice: group by `i.JiraCreatedAt.Year, i.JiraCreatedAt.Month` (Npgsql translates `.Year` on a UTC timestamptz using `AT TIME ZONE 'UTC'`) and format `yyyy-MM` in C#. Verify with the boundary test. Totals: one more aggregate query (count, sum, count where null). Order: key-ordered fields asc by key (null last), others issues desc, key asc, null last. Trim with `ChartGroupTrimmer`. Error message for bad groupBy: `"'{groupBy}' cannot be charted. Group by one of: status, statusCategory, issueType, priority, assignee, sprint, team, storyPoints, createdAt, updatedAt."`
- [ ] **Step 7:** Controller action `[HttpPost("api/projects/{projectId:guid}/issues/chart")] Chart(Guid projectId, IssueChartRequest request, ct)` mapping the result like `Jql`. Register DI. Run the two test classes → PASS. Run whole `dotnet test` → PASS.
- [ ] **Step 8:** Commit `feat(charts): POST /issues/chart — groups the filtered issues by a field`.

### Task 2: API client, query key, MSW handler

**Files:**
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts`, `src/JiraUnpacked.Web/src/api/queryKeys.ts`, `src/JiraUnpacked.Web/src/test/handlers.ts`
- Test: `src/JiraUnpacked.Web/src/api/issues.test.ts`

**Interfaces:**
- Produces:
  ```ts
  export type ChartGroupBy = 'status'|'statusCategory'|'issueType'|'priority'|'assignee'|'sprint'|'team'|'storyPoints'|'createdAt'|'updatedAt';
  export interface ChartGroup { key: string | null; issues: number; storyPoints: number }
  export interface IssueChart { groupBy: ChartGroupBy; totalIssues: number; totalStoryPoints: number;
    issuesWithoutPoints: number; groups: ChartGroup[]; truncated: boolean;
    other: { groups: number; issues: number; storyPoints: number } | null }
  export interface IssueChartRequest { filter?: FilterGroup; groupBy: ChartGroupBy }
  export const getIssueChart = (projectId: string, request: IssueChartRequest) => apiFetch<IssueChart>(…/issues/chart, POST)
  queryKeys.issueChart(projectId, request) → ['projects', projectId, 'chart', JSON.stringify(request)]
  ```
- Default MSW handler for `POST */api/projects/:id/issues/chart` returning a small fixture (export it as `chartFixture` from `src/test/chartFixture.ts`): status groups To Do 5/8pts, In Progress 3/13, Done 2/0, null 1/0.

- [ ] Step 1: test `getIssueChart posts filter and groupBy` (MSW captures body) → FAIL → implement → PASS.
- [ ] Step 2: Commit `feat(charts): chart API client`.

### Task 3: Chart URL state in `useListState`

**Files:** Modify `src/JiraUnpacked.Web/src/features/issues/useListState.ts`; Test `useListState.test.tsx`

**Interfaces:**
- Produces on the hook's return:
  ```ts
  chart: ChartState | null;           // null = panel closed
  setChart: (next: ChartState | null) => void;
  export type ChartMeasure = 'issues' | 'points';
  export type ChartType = 'bar' | 'donut' | 'table';
  export interface ChartState { groupBy: ChartGroupBy; measure: ChartMeasure; type: ChartType }
  export const DEFAULT_CHART: ChartState = { groupBy: 'status', measure: 'issues', type: 'bar' };
  ```
  Parsing: `chart` not in the allow-list → `'status'`; `chartMeasure` ≠ `points` → issues; `chartType` ∉ {donut, table} → bar. Writing: defaults as absent params, through `patch`. `setChart(null)` removes all three.

- [ ] Tests: round trip; unknown values fall back; `setChart` then `setTree` in the same tick keeps both (the ref race); `hasListState` false for `?chart=team` alone; `applyParams` (opening a view) keeps `chart*`; `setTree` keeps `chart*`. FAIL → implement → PASS.
- [ ] Commit `feat(charts): chart choice lives in the URL`.

### Task 4: Pure chart helpers

**Files:** Create `src/JiraUnpacked.Web/src/features/issues/charts/chartData.ts`; Test `chartData.test.ts`

**Interfaces:**
- Produces:
  ```ts
  export const CHART_FIELDS: { id: ChartGroupBy; label: string }[]; // labels: Status, Category, Type, Priority, Assignee, Sprint, Team, Points, Created (month), Updated (month)
  export const isKeyOrdered = (g: ChartGroupBy) => boolean;         // storyPoints, createdAt, updatedAt
  export function measureOf(g: {issues:number; storyPoints:number}, m: ChartMeasure): number;
  export function sortGroups(groups: ChartGroup[], groupBy: ChartGroupBy, m: ChartMeasure): ChartGroup[]; // key-ordered: unchanged; else measure desc, key asc, null last
  export interface ChartSlice { key: string | null; label: string; value: number; group: ChartGroup | null } // group null = Other
  export function topN(chart: IssueChart, m: ChartMeasure, n: number): ChartSlice[]; // sorted, first n, rest + server `other` folded into one "Other" slice (omitted when 0); for key-ordered fields no folding happens (all groups, in key order) unless > n*3 — then fold the oldest/smallest into "Other"  *(call: timelines keep their order)*
  export function groupLabel(groupBy: ChartGroupBy, key: string | null): string; // null → "Unassigned" for assignee, else "(Empty)"; month "2026-09" → "Sep 2026"; points → number as is
  export function chartCondition(groupBy: ChartGroupBy, key: string | null): FilterCondition; // null → IsNull; storyPoints → Eq Number(key); month → Between ['yyyy-MM-01','yyyy-MM-<last>']; else Eq key
  export function formatCompact(n: number): string; // 98012 → "98K", 5284 → "5.3K", 41230.5 → "41K", 950 → "950", 12.5 → "12.5"
  ```
- [ ] Tests with exact values: `chartCondition('createdAt','2024-02')` → Between `['2024-02-01','2024-02-29']`; `chartCondition('assignee', null)` → `{field:'assignee', op:'IsNull'}`; `chartCondition('storyPoints','5')` → Eq 5; `topN` with 12 groups n=10 → 11 slices, last "Other" = sum of 2 + server other; sort by points reorders; formatCompact cases above; `groupLabel('createdAt','2026-09')` → `"Sep 2026"`.
- [ ] Commit `feat(charts): grouping, top-N and click-to-filter helpers`.

### Task 5: Palette and the three chart views

**Files:**
- Modify: `src/JiraUnpacked.Web/src/theme.css` — `--ju-chart-1` … `--ju-chart-8` and `--ju-chart-other`, light + dark (load the **dataviz** skill first; validate contrast against `--ju-surface` in both themes).
- Create: `src/JiraUnpacked.Web/src/features/issues/charts/ChartBars.tsx`, `ChartDonut.tsx`, `ChartTable.tsx`; Test `charts/ChartViews.test.tsx`

**Interfaces:**
- Consumes Task 4. Each view: `({ chart, groupBy, measure, onPick, pickHint }: ChartViewProps)` where
  ```ts
  export interface ChartViewProps { chart: IssueChart; groupBy: ChartGroupBy; measure: ChartMeasure;
    onPick: (key: string | null) => void; pickHint?: string /* set = clicks disabled, shown as title */ }
  ```
- Bars: `topN(…,10)`; each non-Other row a `<button aria-label="Filter to <label>: <value>">` containing the bar (reuse `barPercent`; `BarRow` may gain an optional `onClick`/`as button` prop or a sibling component).
- Donut: SVG `viewBox 0 0 200 200`, ring via arc paths, `topN(…,8)`, slice colours `--ju-chart-{i+1}`, Other/(Empty) `--ju-chart-other`, centre `formatCompact(total)` + "Issues"/"Points"; in-ring value only when ≥ 3%; legend below with a button per slice (the click target; paths are `aria-hidden`).
- Table: all groups + Other row when truncated; columns Value | Issues | Story points | %; header buttons sort client-side (local state, default = server/measure order); Value cell is the click button.

- [ ] Tests: bars render 11 rows for 12 groups and "Other" is not a button; clicking "To Do" calls `onPick('To Do')`; `pickHint` disables buttons and sets `title`; donut legend lists top 8 + Other and centre shows `11` / "Issues" for the fixture; switching measure prop re-orders (In Progress first by points); table shows every group, % column sums to ~100, sorting by Issues toggles order; (Empty) row reads "(Empty)".
- [ ] Commit `feat(charts): bar, donut and table views`.

### Task 6: Chart panel on the issue list

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/charts/ChartPanel.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Test: `src/JiraUnpacked.Web/src/features/issues/charts/ChartPanel.test.tsx` (+ one wiring test in `IssueList.test.tsx`)

**Interfaces:**
- `ChartPanel({ projectId, filter, enabled, chart, onChange, onClose, onPick, pickHint })`: `filter` = the pruned filter (undefined = none), `enabled` = `!catalogPending && !savedViews.holdQuery`, `chart: ChartState`, `onChange(ChartState)`, `onPick(condition: FilterCondition)`.
- Query: `useQuery({ queryKey: queryKeys.issueChart(projectId, req), queryFn, enabled, placeholderData: prev => prev })` with `req = filter ? { filter, groupBy } : { groupBy }`.
- Controls: `<Select>` "Group by" from `CHART_FIELDS`; segmented `Issues | Story points` (`aria-pressed`); three type buttons with `aria-label` "Bar chart" / "Donut chart" / "Table" and `aria-pressed`; close button `aria-label="Close chart"`.
- Footer copy: `Total: 98,012 issues` / `Total: 41,230.5 points · 61,200 issues without points` (`toLocaleString()`).
- States: spinner `aria-label="Loading chart"`; error → `ApiError.title` in `role="alert"`; `totalIssues === 0` → "No issues match this filter".
- IssueList: toolbar `Button` "Chart" (`aria-pressed`) toggles `setChart(chart ? null : DEFAULT_CHART)`; when open, wrap table area and panel in `grid lg:grid-cols-[1fr_380px] gap-4` (panel stacks first below `lg`); `onPick` → `setTree(addCellCondition(tree, cond)); setExpanded(true)`; `pickHint` = `cellHint`.

- [ ] Tests: opening via toolbar writes `chart` to URL and renders bars; × closes and clears params; switching measure makes **no** extra chart request (MSW counter); changing group-by refetches with the new `groupBy`; footer copy for points; clicking a bar adds the condition and the list re-queries with it; editing the filter keeps the previous chart on screen until the new one arrives; error and empty states; panel not fetched when closed.
- [ ] Run `npm test`, `npm run typecheck`, `npm run lint` → all pass.
- [ ] Commit `feat(charts): chart panel beside the issue list`.

### Task 7: Verify end to end

- [ ] `dotnet test` (with `JU_TEST_DATABASE=ju_test_charts`) and `npm test` / `npm run build` green.
- [ ] Run the app (local Docker per README, or Vite dev against the running API) on PLAT: open Chart, compare Team counts with the list's count for the same filter; try donut/table, points measure, a month click; check dark theme and < 1024 px layout.
- [ ] Whole-branch review (superpowers:requesting-code-review), fix findings, push, PR into `develop`.
