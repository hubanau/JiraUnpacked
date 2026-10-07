# History Conditions — Was, Changed, Number of Changes — Design

**Status:** approved 2026-09-23 as part of the filtering roadmap (sub-project 4 of 5; roadmap in
`2026-09-23-filter-builder-v2-design.md`); details decided by the controller under the user's
"don't ask, use your best judgment" instruction.
**Goal:** Filter issues by what happened to them, not only by what they look like now:
"status was Blocked", "sprint changed during the last 30 days", "assignee changed by Alice",
and — explicitly requested — **"number of changes of a field"**, e.g. "Sprint changed ≥ 3 times".
JQL has WAS and CHANGED but cannot count; this is the part only JiraUnpacked can answer.

## What already exists

`IssueChangeHistory` (tenant-owned, RLS) holds one row per changed field per Jira changelog entry:
`JiraProjectId, JiraIssueId, JiraHistoryId, Field, FromValue, ToValue, Author, ChangedAt`.
`Field` is the name Jira reports (`status`, `assignee`, `Sprint`, `labels`, `Story Points`, custom
field names…) — every field, not only mapped ones. `FromValue`/`ToValue` are Jira's display strings
(`fromString`/`toString`); multi-value fields (Sprint, labels, Fix Version, Component…) arrive as one
`", "`-joined string. Indexes: `(EndUserId, JiraProjectId, JiraIssueId)`,
`(JiraIssueId, Field, ChangedAt)`, `(EndUserId, JiraProjectId, Field, ChangedAt)`. PLAT has ~2.1M
rows over ~93k issues. The field-churn report (`FieldChurnQueryService`) already aggregates it on
the fly and measured ~125 ms at 1M rows — no denormalised counters, same principle here.

## 1. Model

A new pseudo-field **`history`** (field type `History`) with three new operators (appended to
`FilterOperator`): `Was`, `Changed`, `ChangeCount`. The value is a JSON object:

| Operator | Value | Matches an issue when |
|---|---|---|
| `Was` | `{ "historyField": "status", "values": ["Blocked", null] }` | some changelog row for that field has a `FromValue` or `ToValue` matching one of the values, **or** (for the five current-value fields below) the issue's current value matches |
| `Changed` | `{ "historyField": "Sprint", "from"?: [..], "to"?: [..], "by"?: [..], "during"?: Window }` | some changelog row for that field satisfies every part given (`from` matches FromValue, `to` matches ToValue, `by` matches Author, ChangedAt in window). No parts = changed at all |
| `ChangeCount` | `{ "historyField": "Sprint", "compare": "Gte", "count": 3, "during"?: Window }` | the number of changelog rows for that field (in the window, if given) compares to `count`. Issues with no rows count as 0, so `Eq 0` / `Lt 1` find never-changed issues |

- `historyField`: required, non-blank, ≤ 100 chars, matched **exactly** (case-sensitive), as Jira
  names it. A name no row uses simply matches nothing (it is data, not a closed catalog), except
  that `ChangeCount` with `Eq 0` then matches everything — correct by definition.
- Value matching (`values`, `from`, `to`): a string matches a stored value if it is **equal** or is
  one **element** of a `", "`-joined list (`v`, `v, …`, `…, v`, `…, v, …`), so "Sprint was PLAT 101"
  works for multi-sprint entries. `null` in a list means (Empty): a null or empty stored value.
  Strings are matched exactly (no case folding), lists ≤ 100 entries, each ≤ 255 chars.
- `by`: author display names, exact match; `null` not allowed.
- `compare` ∈ `Eq, Neq, Gt, Gte, Lt, Lte`; `count` integer 0…10000.
- **Window** (`during`), one of:
  - `{ "kind": "inLast", "amount": 30, "unit": "days" }` — same rules as `InLast`
  - `{ "kind": "inPeriod", "period": "lastMonth", "timeZone": "Europe/Warsaw" }` — same as `InPeriod`
  - `{ "kind": "between", "from": "2026-09-01", "to": "2026-09-30" }` — same as date `Between`
    (either bound open, date-only `to` includes the whole day)
  Evaluated with the translator's `IClock`, reusing `RelativeDates`.
- **Current value for `Was`**: an issue that never changed a field still "was" its current value.
  For `historyField` ∈ `status, assignee, priority, issuetype, Sprint` (Jira's changelog names for
  the columns the issue table has), `Was` also matches the issue's current value through the same
  dictionary-name resolution the dictionary filters use (null in `values` ↔ current value empty).
  Other fields rely on history rows only (documented limitation: a never-changed custom field
  with its initial value set at creation is invisible to `Was`).
- Empty rule (sub-project 1) is unaffected: these operators answer about history, not emptiness.
- Guards: history conditions count as conditions **and** are additionally capped at **5 enabled
  history conditions per filter** (each is a correlated subquery). Exposed in the catalog as
  `maxHistoryConditions`.

## 2. Translation

The translator gets the tenant-scoped `IssueChangeHistory` source from the compiler (the same
injected `AppDbContext` that already serves dictionary lookups) and builds **correlated
subqueries**, never materialising history in memory:

- `Was` / `Changed`: `histories.Any(h => h.JiraProjectId == i.JiraProjectId && h.JiraIssueId ==
  i.JiraIssueId && h.Field == f && <parts>)` (plus the current-value OR for `Was`).
- `ChangeCount`: `histories.Count(h => <same correlation> && <window>) <op> n`.
- List-element matching is built from `==`, `StartsWith`, `EndsWith`, `Contains` on parameters —
  EF/Npgsql translate them to escaped `LIKE`; no regex, no user string in SQL text.
- Window bounds and the clock-derived cutoff are EF **parameters**, not constants (sub-project 3's
  `Parameter` helper), so the query cache is not churned.

**Performance gate:** before release, the generated SQL for each operator is run with
`EXPLAIN (ANALYZE, BUFFERS)` against the local dev database's largest project (read-only, via the
reader role) and must finish under **2 s** for a whole-project list query (page 1, count included).
If `ChangeCount` misses it, add the composite index `(JiraProjectId, JiraIssueId, Field, ChangedAt)`
in a migration — measured first, added only if needed.

## 3. Value lists

- `GET /api/projects/{id}/history-fields` → `{ fields: [{ name, count }] }`: distinct `Field` with
  row counts for this project, most-changed first, capped at 200.
- `GET /api/projects/{id}/history-values?field=<historyField>&kind=value|author` →
  `{ values: [{ name, count }], emptyCount }`: for `value`, distinct elements of From/To values
  (lists split on `", "` in memory after a grouped, capped read), for `author`, distinct authors;
  sorted by name, capped at 1000. Unknown field → empty list (not 400).
Both tenant-scoped, 404 for unknown project, like `filter-values`.

## 4. SPA

- The field select gets a last entry **"History of a field…"** (`history`). Operators:
  `Was` "was", `Changed` "changed", `ChangeCount` "number of changes".
- The row shows a **History field** select (from `history-fields`, with counts; typed fallback if
  the list fails), then per operator:
  - **was**: multi-value picker over `history-values?kind=value` (+ (Empty)).
  - **changed**: optional From (multi), To (multi), By (multi, authors), During (window).
  - **number of changes**: compare select (`=`, `≠`, `>`, `≥`, `<`, `≤`) + count input (0–10000,
    default `≥ 2`) + optional During.
  - **During** control: `any time` / `in the last [N] [unit]` / `in period [period]` /
    `between [from] [to]` — reusing sub-project 3's controls and zone capture.
  The row wraps onto a second line under the field/operator selects for these controls.
- Completeness: `historyField` required; `Was` needs ≥ 1 value; `ChangeCount` needs an integer
  count 0–10000 and a compare; `Changed` is complete once the field is chosen; a `during` that is
  incomplete makes the condition incomplete.
- Limits: the editor counts enabled history conditions against `maxHistoryConditions` (add buttons
  for a history condition are not separate, so the field select disables "History of a field…"
  at the cap with a hint).
- URL / saved views: the object value round-trips canonically (fixed key order) through
  `readCondition`, `toSavable` and `isModified`.

## 5. Testing

Translator unit tests (in-memory fakes where the subquery shape allows, otherwise integration):
each operator; list-element matching (equal, first, middle, last element; no partial-word match:
"PLAT 10" must not match "PLAT 101"); (Empty) in values/from/to; `by`; each window kind; ChangeCount
with every compare incl. `Eq 0` matching never-changed issues; current-value `Was` for the five
mapped fields; history cap; validation 400s (missing field, bad compare, count out of range,
null in `by`, oversized lists). Integration: end-to-end list query and export with each operator on
seeded history; tenant isolation (another tenant's history neither matches nor counts); the two
value-list endpoints incl. split lists and caps. Performance gate (§2) recorded in the PR.
Web: each control emits the right wire shape; completeness; window control; history-field
fallback; cap; URL and saved-view round-trip.

## Out of scope

Comparing to the previous value ("story points increased"), time-in-status filters ("in Blocked >
3 days"), a "Changes" column in the list, history of fields the changelog does not report.
