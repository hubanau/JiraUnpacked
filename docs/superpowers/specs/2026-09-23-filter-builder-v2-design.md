# Filter Builder v2: Groups, Not In, Explicit Empty, Multi-Select Picker — Design

**Status:** approved 2026-09-23
**Goal:** Let a user express real questions in the issue-list filter: AND/OR applied to *chosen*
conditions (not the whole list), exclusion lists, and "empty" as something you ask for rather than
something that leaks into results.

## Roadmap this spec opens

The 2026-09-23 filtering request was decomposed into five sub-projects, released in order. Each
has its own spec, plan and PR. This spec is **#1**; the later ones build on the tree format it
fixes.

| # | Sub-project | Contents |
|---|---|---|
| 1 | **Filter builder v2** (this spec) | nested AND/OR groups (3 levels), `NotIn`, `Neq` without empties, explicit Empty on every nullable field, multi-select picker with counts, disable/duplicate |
| 2 | Saved views | filter + columns + sort; toolbar Views dropdown; "modified" marker; starred default view |
| 3 | Richer values | relative dates + `Between`, sprint smart values, status category field |
| 4 | History conditions | was X, changed during period, changed by person, number of changes of a field |
| 5 | Readability | readable summary, copy as JQL / open in Jira, click-to-filter from the table |

## What already exists

- **The query layer already nests.** `FilterGroup(Logic, Conditions, Groups)` is recursive and
  guarded (`MaxConditions 20`, `MaxInValues 100`, `MaxGroups 10`, `MaxDepth 3`, root = depth 1),
  counted over the whole tree. The SPA only ever sends one flat group. Grouping is therefore mostly
  an editor change.
- **Distinct values exist** (`GET /filter-values`), for dictionary fields, drawn from this
  project's issues. Only the `Eq` row uses it; `In` is free text, which is how a user ends up with
  `Backlog` twice.
- Only `IssueList` renders `FilterBuilder`. Metrics and gap-checks share the translator
  (`IIssueFilterCompiler`) but not the editor.

## 1. Meaning of "empty" — one rule

**Empty matches only when you ask for it.**

| Operator | Empty field matches? | Change |
|---|---|---|
| `Eq`, `In` | only if (Empty) is one of the chosen values | new: (Empty) in `In` |
| `Neq`, `NotIn` | never | **`Neq` changes** — today dict `Neq` includes null, and decimal `Neq` includes null through EF's C# null semantics |
| `IsNull` ("is empty") | yes | now offered on every nullable field |
| `IsNotNull` ("is not empty") | no | new |
| `Lt/Lte/Gt/Gte/Contains/StartsWith/NotContains` | never | `NotContains` new; others already guard null |

Nullable fields: `status, issueType, priority, assignee, sprint, team, storyPoints, summary,
parent`. `jiraKey, createdAt, updatedAt` are never empty and get no empty operators.

`Neq` behaviour changes for metrics and gap-check subjects too, because they share the translator.
That is intended: one meaning of "is not" across the product. Saved filters written before this
change keep their JSON but may return fewer rows (issues with no value drop out of `Neq`).

### Wire representation

- **`In` / `NotIn`:** a JSON `null` inside `values` stands for (Empty).
  `{"field":"sprint","op":"In","values":["S1",null]}` = sprint is S1 **or** has no sprint.
  In `NotIn`, a `null` is accepted and redundant (NotIn already excludes empty).
  A list consisting only of `null` is valid (`In [null]` ≡ `IsNull`).
- **`Eq` / `Neq` with (Empty)** never reach the wire: the editor normalises them to `IsNull` /
  `IsNotNull` before sending. A `value` of JSON null on `Eq`/`Neq` stays a 400 as today.

## 2. Operators and fields

`FilterOperator` gains `NotIn`, `IsNotNull`, `NotContains` (appended — enums are serialised by
name, stored by name in `SavedFilters`, so ordinals do not matter but appending keeps diffs quiet).

| Field | Type | Operators after this spec |
|---|---|---|
| status, issueType, priority, assignee, sprint, team | DictName | Eq, Neq, In, NotIn, IsNull, IsNotNull |
| storyPoints | Decimal | Eq, Neq, Lt, Lte, Gt, Gte, IsNull, IsNotNull |
| createdAt, updatedAt | DateTime | Lt, Lte, Gt, Gte |
| summary | Text | Contains, NotContains, StartsWith, IsNull, IsNotNull |
| jiraKey | Text | Eq, In, NotIn, Contains, StartsWith |
| parent | ParentRef | Eq, IsNull, IsNotNull |

`jiraKey In/NotIn` takes a list of keys (free-text chips; paste of a comma/space/newline separated
list splits into chips). `NotContains` = `summary IS NOT NULL AND NOT ILIKE %x%`.

Labels shown in the editor (the wire keeps enum names):
`Eq` is · `Neq` is not · `In` is any of · `NotIn` is none of · `IsNull` is empty ·
`IsNotNull` is not empty · `Lt` < · `Lte` ≤ · `Gt` > · `Gte` ≥ · `Contains` contains ·
`NotContains` does not contain · `StartsWith` starts with.

## 3. Disabled nodes

`FilterCondition` and `FilterGroup` gain an optional `bool? Disabled`. The translator **skips**
disabled nodes entirely: they are not validated, not counted against the limits, and a group whose
every child is disabled is treated as if it were absent. If the **root** ends up with nothing
enabled, the result is "no filter" (match all) — not the empty-group 400, because the user
deliberately switched everything off. A non-root group written with no children at all is still a
400 as today.

The flag exists on the server (not only in the SPA) so that sub-project 2 can store a view with
disabled parts and the server-side validation of saved definitions accepts it.

## 4. Value counts

`GET /api/projects/{id}/filter-values?field=` response changes from
`{ field, values: string[] }` to

```json
{ "field": "status", "values": [ { "name": "Backlog", "count": 42 } ], "emptyCount": 7 }
```

Counts are over the whole project (not faceted by the current filter): cheap, stable while the
user edits, and honest about what exists. Still capped at 1000 values, still dictionary fields
only. The only consumer is the SPA, which changes in the same PR.

## 5. CSV export

`GET .../issues/export.csv?filter=` currently parses `filter` as a `FilterCondition[]` plus a
`logic` param. It now accepts either shape: a JSON **object** is a `FilterGroup` (and `logic` is
ignored); a JSON **array** keeps today's behaviour. Detected by the first non-whitespace char.

## 6. Editor (SPA)

**State model.** The editor holds one `FilterGroup` tree (the wire type, plus `disabled`). Each
node carries a client-only `id` (for React keys and focus), stripped before sending.

**`GroupEditor` (recursive)** replaces the body of `FilterBuilder`:

```
Match [All ▾]                                        [⎘ duplicate] [⏻ disable] [✕]
  [⏻] status  is any of  [Backlog ✕][To Do ✕] [▾]              [⎘] [✕]
  ┌ Match [Any ▾] ────────────────────────── [⎘] [⏻] [✕] ┐
  │ [⏻] assignee is Alice                          [⎘] [✕]│
  │ [⏻] priority is none of [Low ✕] [▾]            [⎘] [✕]│
  │ [+ condition] [+ group]                               │
  └───────────────────────────────────────────────────────┘
[+ condition] [+ group]   [Clear all]
```

- Root has no remove/duplicate; "Clear all" resets it to an empty `And` root.
- `+ group` is hidden at depth 3 (root = 1). Adding a group seeds it with one blank condition.
- Limits come from the catalog. At a limit, the add buttons are disabled with a `title` hint
  ("Limit: 20 conditions"). Disabled nodes do not count, matching the server.
- Disabled rows/groups render at reduced opacity with the toggle pressed (`aria-pressed`).
- Duplicate inserts a deep copy (new ids) directly after the original; blocked at limits.
- Nested groups are visually indented with a left border in the theme's accent colour; the
  match select sits on the group's header line.

**Value controls by operator:**

| Field type | Eq / Neq | In / NotIn |
|---|---|---|
| DictName | single-select picker: (Empty) first, then values with counts; choosing (Empty) switches the operator to is empty / is not empty | **multi-select popover**: search box, checkbox list with counts, (Empty) first; chips show the selection; duplicates impossible |
| jiraKey | text | chip input, paste splits on `[,\s]+`, case-normalised to upper, de-duplicated |
| Decimal | number | — |
| DateTime | date | — |

If `filter-values` fails, the picker falls back to today's free-text input (chips for In/NotIn).

**Sending.** Before a query or export, the tree is **pruned**: disabled nodes removed, incomplete
conditions removed (no value / empty list), groups left empty removed; `Eq`/`Neq` + Empty
normalised to `IsNull`/`IsNotNull`; ids stripped. An empty pruned root sends no filter.

**URL.** `filter=` holds the unpruned tree JSON (disabled parts survive a reload/share) with ids
stripped. Reading: a JSON object → tree; a JSON array → legacy flat list wrapped in a group whose
logic comes from the legacy `logic` param; malformed → empty. Writing always emits the tree and
drops the `logic` param. An empty tree removes `filter`.

## 7. Error handling

- Server validation errors (400 ProblemDetails) already surface under the builder; unchanged.
- A saved/legacy URL referencing a field no longer in the catalog shows that row with the field
  select in an invalid state and it is excluded by pruning (not sent).

## 8. Testing

**Translator unit tests** (`IssueFilterTranslatorTests`, `IssueFilterGroupTests`):
NotIn on dict (excludes empty), In with `null` (includes empty), In `[null]`, NotIn with `null`,
Neq dict excludes empty, Neq decimal excludes empty, IsNotNull on dict/decimal/text/parent,
NotContains excludes null summary and escapes wildcards, jiraKey In/NotIn, disabled condition
skipped and not counted (21 conditions with one disabled passes), disabled group skipped, all-
disabled root = match all, >100 In values still rejected, operators rejected where not allowed.

**Integration:** filter-values returns counts + emptyCount; export.csv with a tree `filter`;
query endpoint with nested groups + NotIn against seeded data; catalog lists new operators.

**Web:** GroupEditor add/remove/duplicate/disable at each depth and at limits; picker multi-select
+ search + (Empty) + no duplicates + fallback; Eq (Empty) → is empty; pruning; URL round-trip for
tree, legacy array + logic, malformed.

## Out of scope

Saved views (#2), relative dates / Between / sprint smart values / status category (#3), history
conditions (#4), readable summary / JQL / click-to-filter (#5). Drag-and-drop reordering.
