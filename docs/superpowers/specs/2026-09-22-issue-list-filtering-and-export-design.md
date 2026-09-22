# Issue List: Filtering, Columns, Sorting and Export — Design

**Status:** approved 2026-09-22
**Goal:** Make the issue list usable for real work — pick values instead of typing them, combine
conditions with OR, choose and sort columns, and take the result away as CSV.

## What already exists

Two of the five asks need far less than they appear to.

**OR is already in the query layer.** `FilterGroup(FilterLogic Logic, Conditions, Groups)` supports
`And`/`Or` and nesting, guarded by `MaxGroups` and `MaxDepth`, and `IssueFilterRequest` accepts
either the flat `Conditions` list *or* a `Filter` tree. Its own comment records that the flat AND
shape is "still the one the SPA sends". Nothing in the translator changes; only the editor and the
request the SPA builds.

**Every column is already projected.** `IssueListItem` carries `JiraIssueId, JiraKey, Summary,
Status, IssueType, Priority, Assignee, Sprint, Team, StoryPoints, ParentJiraIssueId,
JiraCreatedAt, JiraUpdatedAt` — including Created and Parent, which the table does not render.
The column chooser is therefore presentation only.

The remaining three — value lists, sorting, export — need server work.

## 1. Value picker

New endpoint: `GET /api/projects/{projectId}/filter-values?field={name}`, `[Authorize]`,
returning `{ "field": "status", "values": ["Backlog", "In Progress", ...] }` sorted ordinally.

**Only values that occur in this project.** Sourced by selecting distinct display names from the
project's own issues joined to the dictionary table, not from the dictionary wholesale. A Jira
site's status list is site-wide; offering a status no PLAT issue has is a dead end, and the user
cannot tell an empty result from a wrong guess.

Served for `DictName` fields only — status, issueType, priority, assignee, sprint, team. `Decimal`,
`DateTime`, `Text` and `ParentRef` keep their free input; an unknown or non-dictionary field
answers **400**, listing the field, rather than an empty array that would read as "no values".

**Degradation is deliberate:** if the list fails to load the field falls back to the text box it has
today. A broken picker must not make an otherwise working filter unusable.

## 2. OR

One **All / Any** toggle over the condition set, mapping to `FilterGroup.Logic` — `All` sends
`And`, `Any` sends `Or`. The SPA switches from sending `Conditions` to sending `Filter`, which the
server already accepts.

**Not arbitrary nesting.** The server handles trees, but a nested-group editor is a large piece of
UI and "any of these" covers the overwhelming majority of real questions. `(A or B) and (C or D)`
stays expressible by the API and unreachable from the SPA until someone actually needs it.

## 3. Column chooser

Presentation only. A checkbox list over the columns `IssueListItem` already carries, defaulting to
the eight rendered today — Key, Summary, Status, Type, Assignee, Sprint, Points, Updated — with
the four it already fetches but never shows unchecked: **Priority, Team, Parent, Created**. Those
four are being paid for on every request today and thrown away.

Key is **always shown and cannot be unchecked** — it is the row's identity and its link to the
issue; a table of unlabelled rows is not a smaller table, it is a broken one.

## 4. Sorting

`IssueFilterRequest` gains `SortBy` (string, default `updatedAt`) and `SortDescending` (bool,
default true), resolved through a **closed sort catalog** mapping each name to a compiled selector,
exactly as `FilterFields` does for filtering. A sort name never reaches SQL as a string; an unknown
one is a 400, not a silent fallback.

Sortable, using **the same names `FilterFields` already uses** so one vocabulary covers filtering
and sorting: `jiraKey, summary, status, issueType, priority, assignee, sprint, team, storyPoints,
createdAt, updatedAt`. Two names for one field would be a bug waiting to happen in the URL, where
both appear side by side.

`jiraKey` remains the final tiebreaker on every sort so paging is stable — two rows with equal
sort values must not swap between page 1 and page 2.

**A cost worth stating plainly.** The existing index is `(EndUserId, JiraProjectId,
JiraUpdatedAt)`. Sorting by `updated` stays index-served. Sorting by anything else makes Postgres
sort the whole filtered set — ~92,000 rows on PLAT — for each page request. It will be correct and
it will not be instant. No new indexes are added in this work: which columns people actually sort
by is unknown, and indexing all eleven would tax every write to serve guesses. Measure first.

## 5. CSV export

New endpoint: `GET /api/projects/{projectId}/issues/export.csv`, `[Authorize]`, taking the same
filter, sort and column selection as the list, and returning `text/csv` with a
`Content-Disposition` filename of `{projectKey}-issues-{yyyyMMdd}.csv`.

**Streamed, never buffered.** Rows are written to the response as they are read, via
`IAsyncEnumerable` with no `ToListAsync`. Materialising 92,000 rows to build a string is exactly
the mistake removed from derivation this week, and it would be worse here because the response is
held for the length of the download.

**Formula injection is escaped.** A value beginning with `=`, `+`, `-`, `@`, tab or carriage return
is prefixed with a single quote before quoting. Excel and Sheets execute leading-`=` cells as
formulas, and a Jira summary is text a stranger can write — `=HYPERLINK(...)` in a summary would
otherwise run on the machine of whoever opens the export. Ordinary CSV quoting does not prevent
this; it is a separate step.

Export ignores paging (it is the whole filtered set) but is capped at **50,000 rows**, responding
`400` with the row count when the filter matches more. An unbounded export of a 92,000-issue
project is a request to hold a connection open for minutes; narrowing the filter is the honest
answer.

## URL as the state

Filter, sort and column selection live in the **query string**, not `localStorage`.

Two reasons, in order. The project's rule is that only `ju.username` and `ju.theme` may be
persisted to `localStorage`, and this work is not a reason to widen that. And a URL is
**shareable** — "PLAT bugs in this sprint by points" becomes a link, which is most of the value of
building the filter at all. It also survives reload and Back, which in-memory state does not.

## Testing

- **Value list is project-scoped**: a status present only in another project's issues does not
  appear; a tenant's list never contains another tenant's values.
- **Value list refuses non-dictionary fields** with 400 rather than an empty array.
- **OR actually ORs**: a two-condition `Any` filter returns the union, and the same conditions
  under `All` return the intersection — asserted against seeded rows, not just a 200.
- **Sort is a closed catalog**: an unknown sort name is 400; a known one orders correctly in both
  directions; `JiraKey` breaks ties so page 1 and page 2 do not overlap.
- **Export streams**: an export over a seeded 20,000-row project never materialises the set —
  asserted by the row count reaching the response while the tracker stays bounded.
- **Export escapes formulas**: a summary of `=1+1` arrives as `'=1+1`, and one containing a comma,
  a quote and a newline round-trips through a CSV parser unchanged.
- **Export respects the filter**: the exported row count equals the list's `TotalCount`.
- **Export refuses oversized sets** with 400 and the count.
- **Column choice is presentation only**: deselecting a column changes the table, not the request.

## Non-goals

- No nested group editor in the SPA.
- No new indexes; no schema change; no new tenant-owned table, so the FR-B2 gate is untouched.
- No change to the existing flat `Conditions` request shape, which stays accepted.
- No saved-view persistence — the URL is the saved view.
- Export is CSV only, not XLSX.

## Constraints preserved

- Filter and sort input never reaches a SQL string: both go through closed catalogs and Expression
  trees, with `EF.Functions.ILike` and escaped wildcards for text.
- All reads through the injected tenant-scoped `AppDbContext`; no `IgnoreQueryFilters`, no
  privileged factory, no `FromSql`.
- The SPA keeps its four runtime dependencies; CSV is produced server-side.
