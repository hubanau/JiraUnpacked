# Richer Filter Values — Relative Dates, Between, Sprint Smart Values, Status Category — Design

**Status:** approved 2026-09-23 (sub-project 3 of 5; roadmap in
`2026-09-23-filter-builder-v2-design.md`)
**Goal:** Filters that stay true over time: "updated in the last 14 days", "created this month",
"in the active sprint", "not done". A saved view using them means the same thing next month.

Everything here is evaluated **on the server at query time** (the translator, with the injected
`IClock`), never pre-computed by the SPA, so a saved view, a CSV export and an API caller all get the
same answer.

## 1. Dates: relative operators and Between

`FilterOperator` gains (appended): `Between`, `InLast`, `NotInLast`, `InPeriod`.

| Operator | Fields | Value | Meaning |
|---|---|---|---|
| `Between` | createdAt, updatedAt, storyPoints | `values: [from, to]` | `from ≤ x ≤ to`. Either bound may be `null` (open), not both. For dates, a date-only `to` (`2026-09-30`) includes that whole day (`x < to + 1 day`); a full timestamp is used as-is. Dates are UTC, like the existing `Lt/Gt` on dates. `from > to` → 400. |
| `InLast` | createdAt, updatedAt | `value: { "amount": 14, "unit": "days" }` | `x ≥ now − amount·unit` |
| `NotInLast` | createdAt, updatedAt | same | `x < now − amount·unit` ("more than 14 days ago") |
| `InPeriod` | createdAt, updatedAt | `value: { "period": "thisWeek", "timeZone": "Europe/Warsaw" }` | `start ≤ x < end` of that calendar period in that time zone |

- `unit` ∈ `hours, days, weeks, months`; `amount` integer 1…3650. Months use calendar subtraction
  (`AddMonths`).
- `period` ∈ `today, yesterday, thisWeek, lastWeek, thisMonth, lastMonth, thisQuarter,
  lastQuarter, thisYear, lastYear`. Weeks start Monday (ISO 8601).
- `timeZone` is an IANA id. The SPA fills it from the browser (`Intl.DateTimeFormat().resolvedOptions().timeZone`)
  when the condition is created, and it is saved with the view. Missing → UTC. Unknown id → 400.
  (The Jira account's zone is only known by calling Jira, which a query must not do.)
- Why the zone lives in the condition: it keeps a saved view's meaning stable regardless of which
  machine opens it, and needs no new request field threaded through query, export, metrics and
  gap-check endpoints.

`storyPoints` gains `Between` only (numbers; both bounds inclusive; decimal bounds).

## 2. Sprint smart values

Sprint dictionary values can also be **smart**: resolved from `Sprint.State` (`active`, `future`,
`closed`) at query time.

| Smart key | Label | Resolves to |
|---|---|---|
| `active` | Active sprint | sprints of this project with state `active` |
| `future` | Future sprints | state `future` |
| `open` | Open sprints (active + future) | `active` or `future` |
| `closed` | Closed sprints | `closed` |

Wire: inside `values` (for `In`/`NotIn`) or as `value` (for `Eq`/`Neq`), a smart value is a JSON
**object** `{ "smart": "active" }` — never a string, so it cannot collide with a sprint someone
named "active". Strings keep meaning sprint names; `null` keeps meaning (Empty). Smart values are
valid only on `sprint`; elsewhere → 400. Unknown key → 400. A smart value that resolves to no sprint
matches nothing (not an error: "no active sprint right now" is a real state).

Resolution is per project (`Sprint.JiraProjectId`), through the same tenant-scoped resolver that
resolves names, so it cannot reach another tenant's sprints.

`GET filter-values?field=sprint` adds `smart: [{ key, label, count }]` (count = issues whose sprint
resolves under that key, whole project, like the other counts). Other fields omit `smart`.

## 3. Status category field

New filter field **`statusCategory`** (type DictName-like, own type `StatusCategory`):
- Values: the `Status.StatusCategory` names used by this project's issues (Jira's
  `To Do` / `In Progress` / `Done`; whatever strings Jira sent are shown as-is).
- Operators: `Eq, Neq, In, NotIn, IsNull, IsNotNull` with sub-project 1's empty rule. Empty = the
  issue has no status, or its status has no category.
- Translation: names → the set of status ids (tenant-scoped, this project's connection) whose
  category is one of them → `StatusId in set`. No join in the issue query.
- `filter-values?field=statusCategory` returns the categories with counts + `emptyCount`, like any
  dictionary field.
- Also a **column** (`statusCategory`, header "Category") and a **sort** field, since the list is
  where the user sees why a row matched. The issue projection gains `StatusCategory`.

## 4. SPA

- Operator labels (append to the label map): `Between` between · `InLast` in the last ·
  `NotInLast` more than … ago · `InPeriod` in period.
- Value controls:
  - `Between` on dates: two date inputs (from / to), either may be blank; on storyPoints: two
    number inputs.
  - `InLast` / `NotInLast`: number input + unit select (hours / days / weeks / months); default
    `14 days`. `NotInLast` renders as "more than [14] [days] ago".
  - `InPeriod`: select with the ten periods in reading order (Today … Last year); `timeZone` set
    silently from the browser when the operator is chosen; shown as a small muted hint
    ("Europe/Warsaw") next to the select.
  - Sprint pickers (single and multi) show a **Smart** section above the names: the four smart
    values with counts; chips show the label (e.g. "Active sprint").
- The URL/tree model accepts object values (`{amount, unit}`, `{period, timeZone}`, `{smart}`)
  for these operators; completeness: Between needs at least one bound; InLast/NotInLast need an
  amount ≥ 1; InPeriod needs a period.
- `filterTree.ts` operator validation must include the new operators (it derives from the
  exhaustive label map since sub-project 1's final fix).

## 5. Testing

**Translator:** Between dates (inclusive date-only end, open bounds, from>to 400), Between decimals;
InLast/NotInLast for each unit with a fixed clock; InPeriod for every period incl. week starting
Monday, quarter boundaries, year boundary, a non-UTC zone where the local day differs from UTC,
missing zone = UTC, unknown zone 400; smart sprint values for each key incl. no-active-sprint →
matches nothing, smart on a non-sprint field 400, string "active" still means a sprint named
active; statusCategory Eq/Neq/In/NotIn/IsNull/IsNotNull with the empty rule. **Integration:**
filter-values smart counts and statusCategory counts; query endpoint end-to-end with relative
dates (fixed clock in the harness) and smart sprint; statusCategory column/sort. **Web:** each new
value control emits the right wire shape; completeness/pruning; smart section in both pickers;
URL round-trip of object values; labels.

## Out of scope

User-defined periods ("last 3 sprints"), fiscal calendars, per-project week start, status semantic
classes (the user's own mapping) as a filter field.
