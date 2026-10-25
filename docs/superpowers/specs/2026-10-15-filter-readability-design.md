# Filter Readability — Summary, JQL / Open in Jira, Click-to-Filter — Design

**Status:** approved 2026-09-23 as part of the filtering roadmap (sub-project 5 of 5; roadmap in
`2026-09-23-filter-builder-v2-design.md`); details decided by the controller under the user's
"don't ask, use your best judgment" instruction.
**Goal:** A filter with nested groups, relative dates, smart sprints and history conditions is
powerful but hard to read back. Make it **readable** (one sentence), **portable** (the equivalent
JQL, and a link that opens it in Jira), and **quick to build** (filter from a value you can see in
the table).

## 1. Readable summary (SPA only)

`describeFilter(tree, catalog): string` renders the **pruned** tree (what is actually sent:
disabled and unfinished parts are left out) as one sentence, e.g.

> status is any of Backlog, To Do **and** (assignee is Alice **or** priority is none of Low)
> **and** Sprint changed ≥ 3 times in the last 30 days

- Operator wording reuses `OPERATOR_LABELS`; values are listed (more than 5 → "Backlog, To Do,
  … and 4 more"); (Empty) reads "empty"; smart values read their label ("Active sprint");
  relative dates read "in the last 14 days" / "in period this week (Europe/Warsaw)" / "between
  1 Sep 2026 and 30 Sep 2026"; history reads "status was Blocked", "Sprint changed from A to B by
  Alice during …", "Sprint changed ≥ 3 times".
- Nested groups are parenthesised; the root's All/Any is the joining word.
- Empty pruned tree → "All issues".
- Field names use the catalog/column headers ("Category", "Type", "Points"), not wire names.

UI: a **summary line** sits above the builder: `Showing: <sentence>` with a **Collapse / Edit
filter** toggle. Collapsed shows only the summary line (the builder unmounts); the toggle is
component state (not URL, not localStorage). The builder starts **expanded** when the URL has no
filter and **collapsed** when a filter or saved view is loaded on mount, so a shared link reads as
a sentence first. The summary line also shows the result count when known ("· 42 issues").

## 2. JQL and Open in Jira (server)

`POST /api/projects/{projectId}/issues/jql` with `{ filter }` (the same `FilterGroup` the query
takes) → `{ jql, url, warnings: [{ message }] }`. Tenant-scoped; 404 for unknown project; invalid
filter → the same 400 as the query (it is validated by the same compiler first).

- `jql = project = <KEY> AND (<translated tree>) ORDER BY <sort>` — the request also carries the
  list's `sortBy`/`sortDescending` so the Jira view sorts the same way where JQL can.
- `url = <connection BaseUrl>/issues/?jql=<url-encoded jql>`.
- Translation per field (disabled parts skipped; groups → parentheses with AND/OR):

| Filter | JQL |
|---|---|
| status / issueType / priority / statusCategory Eq, Neq, In, NotIn | `status = "X"`, `!=`, `in (...)`, `not in (...)` on `status` / `issuetype` / `priority` / `statusCategory` (names quoted, `"` and `\` escaped) |
| assignee | by **account id** (`JiraUser.JiraAccountId` resolved from the display name, tenant-scoped); a name with no account → warning, part omitted |
| sprint | by **Jira sprint id** (`Sprint.JiraSprintId`); smart: `open` → `sprint in openSprints()`, `future` → `sprint in futureSprints()`, `closed` → `sprint in closedSprints()`, `active` → `(sprint in openSprints() AND sprint not in futureSprints())` |
| team | `cf[<id>]` from the project's Team `FieldMapping`, by `Team.JiraTeamId`; no mapping → warning, omitted |
| storyPoints | `cf[<id>]` from the StoryPoints mapping; comparisons and Between map directly; no mapping → warning |
| (Empty) | `is EMPTY` / `is not EMPTY`; In with (Empty) → `(f in (...) OR f is EMPTY)`; Neq/NotIn already exclude empty in JQL, matching our rule |
| summary Contains / NotContains / StartsWith | `summary ~ "x"` / `NOT summary ~ "x"` / `summary ~ "x*"` + one warning: JQL text search is word-based, not substring |
| jiraKey | `key = X` / `key in (...)` / `key not in (...)`; Contains/StartsWith → warning, omitted |
| parent | `parent = <jira id>` / `parent is EMPTY` |
| createdAt / updatedAt comparisons, Between | `created >= "2026-09-01"` etc. (UTC dates, `"yyyy-MM-dd HH:mm"` when a time is present) |
| InLast / NotInLast | `created >= -14d` / `created < -14d` (h, d, w; months → `-<30·n>d` + warning "months approximated as 30 days") |
| InPeriod | `startOfDay()`, `startOfWeek()`, `startOfMonth()`, `startOfYear()` with offsets (`startOfMonth(-1)` … `endOfMonth(-1)`); quarters → explicit date range computed with the server clock + warning; one warning: Jira evaluates periods in the Jira user's zone and locale week start |
| history Was | `<field> WAS IN (...)` for JQL's WAS fields (status, assignee by account id, priority, resolution, fix version, reporter); others → warning, omitted |
| history Changed | `<field> CHANGED [FROM ...] [TO ...] [BY <accountId>] [DURING ("d1", "d2") / AFTER -30d]` for JQL's CHANGED fields; others → warning, omitted |
| history ChangeCount | always warning ("JQL cannot count changes"), omitted |

- **Omitted parts change meaning**, so the warning says what was left out, and when an omitted
  part sits inside an Or group the warning says the JQL is **narrower or wider** accordingly.
  If every part is omitted, `jql` is just `project = KEY ORDER BY ...` with the warnings.
- Strings are always quoted and escaped; nothing from the request reaches JQL unescaped; account
  ids and sprint ids come from the database, never from the request.

UI: in the toolbar, **Copy JQL** (copies to clipboard; toast "JQL copied") and **Open in Jira**
(new tab, `rel="noopener noreferrer"`). Both call the endpoint with the current pruned filter. When
there are warnings, a small "⚠ N differences" button next to them opens a list of the warnings.

## 3. Click-to-filter from the table (SPA only)

Cells of **filterable** columns (status, issueType, priority, assignee, sprint, team,
statusCategory, storyPoints, parent) get a small **⋯ menu button** (visible on row hover and on
keyboard focus; `aria-label="Filter by <Column> <value>"`) with two items:

- **Filter to "<value>"** → adds `field Eq value` (empty cell → `IsNull`; storyPoints → number;
  parent → the parent's Jira id).
- **Exclude "<value>"** → adds `field Neq value` (empty cell → `IsNotNull`).

The condition is added to the **root**. If the root is `Any` (Or) and already has enabled parts,
the root is wrapped first: new root `All` containing the old root as a group plus the new
condition — so clicking always **narrows** the list. If wrapping would exceed the depth or group
limit, or the condition limit is reached, the items are disabled with the usual limit hint. An
identical enabled condition already in the root is not added twice. Page resets to 1 (as any filter
change does); the builder expands if collapsed so the user sees what was added.

## 4. Testing

- **describeFilter** unit tests: every operator family incl. (Empty), smart, relative dates,
  history, nested groups, value truncation, disabled/unfinished omitted, empty → "All issues".
- **JQL translator** unit tests (pure, no DB) for every row of the table incl. escaping
  (`"`, `\`), smart sprints, quarter range with a fixed clock, every warning case, omitted part
  inside Or → narrower/wider wording; integration tests for the endpoint: tenant isolation
  (another tenant's account ids / sprint ids / mappings never used), 404, 400 parity with the
  query, url encoding with the connection's base URL.
- **Web**: summary line + collapse/expand + initial state rule; Copy JQL (clipboard mocked) and
  Open in Jira (window.open asserted, noopener); warnings list; click-to-filter on each column
  type incl. empty cell, Or-root wrapping, limits, duplicate suppression, keyboard access.

## Out of scope

Importing JQL into a filter; editing the summary sentence; per-user Jira locale/week-start
detection; translating custom fields other than the mapped Team and Story Points.
