# Issue Charts — "View issues as a chart" — Design

**Status:** draft 2026-10-25, builder-checked. Asked for by the user with three Jira screenshots ("View work items as
a chart": pick a column, see a bar / donut / table). Details decided by the controller under the
user's standing "don't ask, use your best judgment" instruction; each call is marked *(call)*.

**Goal:** On the issue list, answer "how is what I filtered distributed?" without exporting to a
spreadsheet: pick a field (Team, Assignee, Status, Sprint, …), pick what to measure (number of
issues or sum of story points), and see it as a **bar chart**, a **donut** or a **table** — always
for exactly the issues the list's filter matches.

## Why

The list answers "which issues"; Jira's chart panel answers "how many of them, where". Today the
only way to get "story points per team for this filter" is Export CSV + a pivot table. The filter
builder is now powerful (nested groups, history, relative dates), so the chart inherits all of it
for free if it is driven by the same filter.

## Goals

- A chart panel on the issue list, driven by the list's **current pruned filter** (the very object
  `POST /issues/query` gets), over **all** matching issues — not the visible page.
- **Group by** any of: Status, Category (statusCategory), Type, Priority, Assignee, Sprint, Team,
  Points (each distinct value), Created (by month), Updated (by month).
- **Measure**: Issues (count) or Story points (sum).
- **Three chart types**: horizontal bar, donut (pie with the total in the centre), table.
- Totals shown: the total of the measure, and — for Story points — how many matched issues have no
  points (they add 0 and would otherwise silently disappear).
- Click a bar / slice / table row → **filter to that value** (the click-to-filter behaviour that
  already exists for cells), so a chart is also a way to drill down.
- Chart choice is part of the URL, so a link to "PLAT, not done, story points by team as a donut"
  is shareable.

## Non-goals

- **Author / reporter / creator** as a group-by: the sync does not store them (the `Issue` entity
  has no reporter). Adding it is a sync + migration change; noted as a follow-up, not built here.
- Labels, components, fix versions, epics: not synced either.
- Two-dimensional charts (stacked bars, team × status), time series with cumulative lines,
  dashboards, saving a chart into a saved view, exporting the chart as an image/CSV.
- A charting library. The project's rule (see `features/insight/BarRow.tsx`) is CSS bars and
  hand-written SVG; this stays true.

## Anti-goals (would make it a failure even if it "works")

- The chart's total disagreeing with the list's "N issues" for the same filter.
- Grouping by an id or a row instead of a **name** (two dictionary rows that share a name must be
  one bar — same rule as `FilterValueService`).
- Pulling every matching issue into the browser to count it client-side (projects have ~100k
  issues; the grouping is one SQL GROUP BY on the server).
- A "Story points" chart that quietly drops unestimated issues without saying how many.

## 1. Server — `POST /api/projects/{projectId}/issues/chart`

Request:

```json
{ "filter": { …FilterGroup… }, "groupBy": "team" }
```

- `filter` optional (absent = whole project), same shape, same compiler, same 400s as
  `/issues/query`; `conditions` (flat) also accepted for parity *(call: reuse `IssueFilterRequest`'s
  filter fields so the two cannot drift)*.
- `groupBy` from a **closed allow-list** (a literal switch, like `FilterValueService` — no
  caller string reaches a query): `status, statusCategory, issueType, priority, assignee, sprint,
  team, storyPoints, createdAt, updatedAt`. Anything else → 400 naming the allowed values.
- **No `measure` in the request** *(call)*: the response carries both measures for every group,
  so the SPA switches Issues ↔ Story points instantly, re-sorting client-side, with no refetch.

Response:

```json
{
  "groupBy": "team",
  "totalIssues": 98012,
  "totalStoryPoints": 41230.5,
  "issuesWithoutPoints": 61200,
  "groups": [
    { "key": "mobile", "label": "mobile", "issues": 10596, "storyPoints": 4210.0 },
    { "key": null,     "label": null,     "issues": 55011, "storyPoints": 12000.0 }
  ]
}
```

- **Every group carries both `issues` and `storyPoints`** *(call)*. Cheap (one `GROUP BY` with
  `COUNT` and `SUM`), it lets the table show both columns, and the measure becomes a pure display
  choice.
- `key` is what a click-to-filter needs (the name for dictionary fields, the number for Points,
  `"2026-09"` for a month); `label` is what is displayed (same as key except months:
  `"Sep 2026"` is formatted in the SPA, so the server sends `label = key` for months too — *(call:
  formatting stays client-side, like every other date in the SPA)*).
- `key: null` = the (Empty) group (no team, unassigned, no sprint, no points, …). Always included
  when non-zero, labelled "(Empty)" / "Unassigned" (assignee) in the SPA.
- Grouped **by name**, joined the same way `FilterValueService` joins (statusCategory through the
  status, blank category = empty).
- Months are **UTC calendar months**: `date_trunc('month', x, 'UTC')` (or `x AT TIME ZONE 'UTC'`)
  — the columns are timestamptz, and a bare `date_trunc` would truncate in the session's zone and
  stop lining up with the UTC Between filter a click adds *(call: the list shows dates in
  the browser's zone, but bucketing by browser zone would make the server's answer depend on the
  caller; UTC matches how dates are stored; the off-by-hours at month edges is acceptable for a
  distribution chart)*. Months with no issues are not filled in *(call)*.
- `storyPoints` sum treats null as 0; `issuesWithoutPoints` = matched issues with null points
  (always returned; the SPA shows it only for the Story points measure).
- Ordering on the wire: months and Points ascending by key; every other field descending by
  issues, ties by label ascending. The SPA re-sorts non-key-ordered fields by the chosen measure
  (descending, ties by label).
- Truncation *(call)*: the grouped rows are small (one per distinct value), so they are read in
  full and trimmed in C#. A group is kept when it is in the **top 500 by issues OR the top 500 by
  story points** (so at most 1000 groups, and whichever measure is shown, its top N is exact).
  When anything is dropped the response sets `"truncated": true` and the dropped groups are summed
  into `"other": { "groups": g, "issues": n, "storyPoints": p }` so totals still add up. Months and
  Points are never truncated by measure — if they exceed the cap, the last (newest / largest) keys
  are kept *(call)*.
- Tenant scoping: existence check first (unknown/cross-tenant project → 404), reads through the
  tenant-scoped `AppDbContext`, like `IssueQueryService.QueryAsync`.
- Lives beside the other read services: `IIssueChartService` in Domain/Abstractions,
  `IssueChartService` in Infrastructure/Reporting, action on `IssuesController`.

## 2. SPA — the chart panel

**Entry point:** a **Chart** toggle button in the list's toolbar (next to Columns / Export CSV).
On, the page becomes two columns: the table on the left, a **chart panel** on the right
(~380 px, like Jira's); on narrow screens (< 1024 px) the panel stacks above the table *(call)*.
The panel has a close (×) button that turns it off.

**Panel contents (top to bottom):**

1. Title "Chart" and the close button.
2. **Group by** select — the fields above with the list's column headers ("Category", "Type",
   "Points", "Created (month)", "Updated (month)").
3. **Measure** segmented control — "Issues" / "Story points".
4. **Chart type** segmented control with three icon buttons (bar, donut, table), each with an
   `aria-label` and `aria-pressed`.
5. The chart, then a footer: "Total: 98,012 issues" or "Total: 41,230.5 points · 61,200 issues
   without points".

**Bar** — horizontal bars, label left, value right, sorted as the server sorts. Shows the **top 10**
groups; the rest are folded into one **Other** bar *(call: Jira shows ~6 + Other; 10 reads better
for teams/sprints)*. Built from the existing `BarRow` (extended or a sibling, the builder's
choice) so the bar idiom stays one component. Bars are buttons (click-to-filter); "Other" is not.

**Donut** — hand-written SVG ring, **top 8** slices + Other, the total (compact: "98K") in the
centre with the measure's word below ("Issues" / "Points"), slice labels outside or in a legend
below *(builder's choice; a legend is the accessible default)*. Slices use a categorical palette
added to `theme.css` as tokens with light and dark values (chosen with the dataviz skill's
method); "Other" and "(Empty)" use the neutral/muted colour. Slices are clickable except Other.
Values below ~3% get no in-ring label.

**Table** — every returned group: Value | Issues | Story points | % (of the chosen measure),
sortable by any column client-side, (Empty) shown as "(Empty)". Rows are click-to-filter (a
button in the Value cell). When `truncated`, a final "Other (N more values)" row.

**Click-to-filter:** clicking a group adds `field Eq value` (or `IsNull` for (Empty)) through the
existing `addCellCondition` (so it narrows, wraps an Or root, respects limits, no duplicates).
For month groups it adds `createdAt`/`updatedAt` **Between** `['2026-09-01', '2026-09-30']` —
date-only strings, which the server reads as UTC with both ends inclusive (a date-only `to` means
"through that day"), and which ConditionRow / describeFilter already render as "between 1 Sep 2026
and 30 Sep 2026". When the catalog's limit hint
applies, the click targets are disabled with the hint as a tooltip. Group by Points uses the
number. The chart then re-queries with the narrowed filter (and the list does too).

**States:** loading → a skeleton/spinner in the panel only (the table keeps working); error →
the API error title inside the panel; zero matching issues → "No issues match this filter".
While the filter is being edited the previous chart stays on screen (placeholderData), like the
table.

**Data fetching:** TanStack Query, key `queryKeys.issueChart(projectId, filter, groupBy)` —
**measure and chart type are not in the key** (the response has both measures; switching them is
instant, no refetch). Enabled only while the panel is open, and gated exactly like the list query
(`!catalogPending && !savedViews.holdQuery`) so it never runs on an unpruned filter.

**URL:** `chart=<groupBy>` (presence = panel open), `chartMeasure=points` (absent = issues),
`chartType=donut|table` (absent = bar). These are **not** list params: they are not stored in
saved views, do not make a view "modified", and do not stop the default view from opening on an
otherwise bare URL *(call)*. `useListState`'s writes must preserve them, and the chart's writes
must preserve the list's params. `useListState` builds every write on its own latest-params ref,
so a chart write through a separate `setSearchParams` could be overwritten by a list write in the
same tick: chart params are written **through a patch function exported from `useListState`**
(or a sibling that shares its ref), never directly. Unknown values in the URL fall back to the
defaults.

Default when the user first opens the panel: group by **Status**, measure Issues, type Bar
*(call: the catalog always lists `team`, so "Team when mapped" would need data the SPA does not
fetch; Status is meaningful in every project and Team is one click away)*.

## 3. Testing

- **Server unit/integration** (the existing Postgres integration harness): each groupBy arm incl.
  (Empty), name-merging of duplicate dictionary names, statusCategory through status with blank =
  empty, month bucketing in UTC at a month boundary, Points grouping, both measures and
  `issuesWithoutPoints`, ordering rules, truncation (top-500-by-either union, `other` making both
totals add up, a high-points/low-count group survives),
  filter parity (chart `totalIssues` == query `totalCount` for the same filter, incl. a history
  condition), 400s (bad groupBy, bad measure, invalid filter — same message as the query), 404
  for unknown and cross-tenant project, tenant isolation of dictionary names. Scope privileged
  reads by `JiraProjectId` (see the flaky-parallel-tests rule).
- **Web**: API client; panel open/close via the toolbar and ×; URL round-trip of the three params
  and that they survive list edits and are ignored by saved views/"modified"; each chart type
  renders the right groups with top-N + Other; measure switch re-sorts without refetch (MSW call
count);
  Story points footer with issues-without-points; click-to-filter from bar, slice and table row
  incl. (Empty) and a month (Between); limit hint disables clicks; empty/error/loading states;
  keyboard access and aria labels on the chart-type buttons and clickable marks.
- Manual check in the running app (local Docker) against PLAT, comparing one chart with Jira's
  own chart for the same JQL (Copy JQL makes that easy).

## Left to the builder

Component split and file names; whether `BarRow` is extended or wrapped; donut label placement;
exact palette values (via the dataviz skill, validated for contrast in both themes); compact number
formatting helper (reuse `features/insight/format.ts` where it fits); the SQL shape as long as it
is one grouped query (plus at most one for `issuesWithoutPoints`/totals).

## Follow-ups (not in this spec)

- Sync the reporter (and creator) so "Author" can be a group-by.
- Two-dimensional charts (e.g. points per team split by status category).
- Remember the chart in saved views.
