# Saved Views — Design

**Status:** approved 2026-09-23 (sub-project 2 of 5; roadmap in
`2026-09-23-filter-builder-v2-design.md`)
**Goal:** Set up the issue list once (filter, columns, sort), save it under a name, and come back to
it with one click. One view per project may be the default and opens automatically.

Decisions already taken with the user: a view stores the **whole view** (filter + columns + sort);
views are **private** (per user, per project); they live in a **toolbar dropdown**; one view can be
**starred as default**.

## What already exists

`SavedFilters` (entity `SavedFilter`, table with RLS, FR-B2 gate case, CRUD at
`/api/projects/{projectId}/filters[/{filterId}]`) stores a name and `DefinitionJson` jsonb holding
`{ conditions?, filter? }`, validated through `IIssueFilterCompiler` on write, max 100 per project,
max 8 KiB definition, name ≤ 100 chars and unique per project (409 on clash). No UI calls it.
Projects are tenant-owned, so "per project" is already "per user".

We **extend this table** rather than add a new one: it is unused, already isolated, and a view is a
filter plus two presentation settings. The HTTP route stays `/filters` (renaming a working route
buys nothing); the SPA calls the thing a *view*.

## 1. Data

- `DefinitionJson` gains optional `columns: string[]`, `sortBy: string`, `sortDescending: bool`
  next to `conditions`/`filter`. No schema change for these: absent = "not part of this view"
  (the list keeps its defaults).
- New column `IsDefault boolean NOT NULL DEFAULT false`, with a **partial unique index**
  `(JiraProjectId) WHERE "IsDefault"` — at most one default per project, enforced by the database.
  One migration; the table's RLS policy is unchanged (same table, same `EndUserId`).

## 2. API

`SavedFilterView` gains `Columns`, `SortBy`, `SortDescending`, `IsDefault`.
`SaveFilterRequest` gains `Columns`, `SortBy`, `SortDescending`, `IsDefault` (all optional;
`IsDefault` null on update = unchanged, on create = false).

Validation (400 with a message, as today):
- `sortBy` must be a known sort field (`SortFields.IsKnown`).
- `columns`: at most 20 entries, each a known field name from the filter catalog
  (`FilterFields.All` — the column ids are the same vocabulary), no duplicates.
  Unknown → 400 (the SPA only sends ids it renders).
- `filter` still goes through the compiler. Disabled parts are allowed (sub-project 1 made the
  compiler skip them).

Default handling: saving with `IsDefault = true` clears the flag on the project's other views
**first** (`ExecuteUpdateAsync` inside the same transaction), then writes this one — the partial
unique index is checked per row, so the order matters. A concurrent race that still trips the index
surfaces as the existing 409 path. `IsDefault = false` on the current default just clears it.

List order: default first, then by name (case-insensitive) — the dropdown shows them in that order.

## 3. SPA

### Toolbar

Left of `Columns` / `Export CSV`:

```
[Views: My backlog • modified ▾] [Save]
```

- The button label is `Views` when no view is loaded, `Views: <name>` when one is, with
  ` • modified` when the current list state differs from it.
- `Save` appears only when a view is loaded **and** modified: it overwrites that view (PUT) with the
  current state, keeping its name and default flag.
- The dropdown menu (a popover, keyboard accessible, Escape closes):
  - one item per view, ★ before the default; choosing one **applies** it
  - separator
  - `Save as new view…` — name field + Save; creates a view from the current state and loads it
  - when a view is loaded: `Rename…`, `Set as default` / `Remove default`, `Delete…`
    (delete asks for confirmation using the existing `ConfirmDialog`)
- Name input: required, trimmed, ≤ 100 chars; a 409 shows "A view with this name already exists";
  the per-project cap shows the server's message.

### URL and state

- A loaded view is the URL param `view=<id>`.
- **Applying** a view navigates (push, so Back returns to what you had) to the issue-list URL built
  from the view: `filter` (tree), `sortBy`, `sortDesc`, `cols` exactly as `useListState` writes
  them, plus `view=<id>`. Anything absent in the view uses the list default. Page resets to 1.
- **Modified** = the current `filter` (as `toUrlJson`), sort and columns, normalised the way
  `useListState` normalises them (defaults ≡ absent), differ from the loaded view's. Comparing
  URL-normalised strings keeps it exact and cheap.
- Editing after loading keeps `view=<id>` (so `• modified` shows); `Clear all` keeps it too.
- A `view` id that is not in the list (deleted elsewhere, or someone else's link) is dropped from
  the URL silently; the rest of the URL still applies.
- **What is saved**: the tree with incomplete conditions and emptied groups removed but disabled
  parts kept, ids stripped (a new `toSavable` helper beside `pruneTree`); an empty tree saves no
  filter. Columns and sort are saved as currently shown.

### Default view

When the issue list mounts with **no list state in the URL** (no `filter`, `sortBy`, `sortDesc`,
`cols`, `view`, legacy `logic`) and the views list contains a default, the default is applied with
**replace** (no extra history entry). This happens once per mount: `Clear all` or removing params
later does not re-apply it. The issue query waits for the views list (like it waits for the
catalog) only in that no-state case, so the first request is the default view's, not a flash of the
unfiltered list; if the views request fails, the list proceeds unfiltered.

## 4. Error handling

- Views list fails to load → the dropdown shows "Views unavailable" and the list works as today.
- Save/rename/delete failures show the server's ProblemDetails message in the menu, the menu stays
  open with the user's input intact.
- A view whose filter references a field the catalog no longer has: applied as-is; the row shows its
  invalid state (sub-project 1) and pruning leaves it out of the query.

## 5. Testing

- **Server:** create/update/get round-trip columns + sort + disabled parts; unknown sort field and
  unknown column → 400; duplicate column → 400; > 20 columns → 400; setting default clears the
  previous default; at most one default per project (direct insert of a second default violates
  the index); `IsDefault` null on update leaves it unchanged; list order default-first then name;
  cross-tenant isolation unchanged (FR-B2 gate still green); migration applies from zero.
- **Web:** apply a view (URL gets filter/sort/cols/view; Back returns); modified marker appears on
  edit and disappears when edited back; Save overwrites; Save as new with 409 message; rename;
  set/remove default; delete with confirm; unknown `view` id dropped; default view applied on a bare
  URL with replace and only once; query waits for views only in the bare-URL case; views failure
  degrades.

## Out of scope

Sharing views with other users; ordering views by hand; views for the metrics/gap-check pages.
