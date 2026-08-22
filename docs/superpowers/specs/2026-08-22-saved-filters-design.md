# Saved filters

**Status:** Approved (2026-08-22)
**Epic:** Post-UI backend follow-ups — **3 of 4**.
**Depends on:** cross-field OR and nested filter groups (spec 1 of 4) — what gets saved is that shape, which is why this lands after it rather than before.

Every filter a user composes today dies with the page. This adds per-project named filters. It is the only follow-up in the epic that introduces a **new tenant-owned table**, so it is the only one that touches the FR-B2 isolation gate.

---

## 1. Scope & non-goals

**In scope:** a `SavedFilters` table with both isolation layers, five CRUD endpoints, validation at save time, and an FR-B2 gate extension covering the new table.

**Out of scope:**
- **Executing a saved filter by reference.** §3 — this is a deliberate decision, not an omission.
- **Sharing filters between users.** Every row is tenant-owned; cross-tenant sharing would need a whole permissions model.
- **UI.** Backend only.
- **Filters scoped to a connection rather than a project.** Field values (status names, assignees) are project-meaningful; a connection-wide filter would be a different feature.

## 2. Decisions locked in

- **Both isolation layers, no exceptions.** `SavedFilter` implements `ITenantOwned`, gets an EF global query filter, gets a Postgres RLS policy in its own migration, and **the FR-B2 gate grows a case for it**. That gate is the project's standing proof that isolation is not aspirational, and a new tenant table that is not in it is a hole in the proof.
- **Erasure is automatic.** `AppDbContext` already applies the `EndUserId -> Users` cascade to every `ITenantOwned` type centrally, precisely so the seventeenth such table inherits it rather than relying on someone remembering. Nothing to add; there is a test.
- **A saved filter must actually filter.** Saving an empty definition is rejected. "A saved filter that matches everything" is indistinguishable from having no filter, and naming it makes it look like a real constraint.
- **Enum values are stored as names, not ordinals.** §5.
- **No new dependency.**

## 3. Storage only: a saved filter is not executable by reference

`POST /issues/query` and the report endpoints do **not** gain a `savedFilterId`. A client reads the definition and posts it, exactly as it posts an ad-hoc one.

This is the security-relevant decision in the spec. The whole safety property of FR-E4 is that user filter input becomes an Expression tree over a closed catalog and never a SQL string. Adding an execution-by-reference path would mean a *stored* blob feeding the translator through a second route — a route whose input is no longer the request the caller just made. Keeping saved filters inert storage means:

- there is exactly one code path from filter input to predicate, the one that is already tested,
- a stored definition is validated on the way **in** and compiled by the same translator on the way **out**, and
- nothing new can be reached by writing to the table directly.

The cost is one extra round trip for the client. That is a good trade.

## 4. Shape and endpoints

| Method | Route | Result |
|---|---|---|
| GET | `/api/projects/{projectId:guid}/filters` | the project's saved filters, newest first |
| POST | `/api/projects/{projectId:guid}/filters` | **201** with the created filter |
| GET | `/api/projects/{projectId:guid}/filters/{filterId:guid}` | one filter |
| PUT | `/api/projects/{projectId:guid}/filters/{filterId:guid}` | **200** with the updated filter |
| DELETE | `/api/projects/{projectId:guid}/filters/{filterId:guid}` | **204** |

A saved filter carries `{ id, name, conditions?, filter?, createdAt, updatedAt }` — the same `conditions`/`filter` pair every other endpoint takes, so a client never translates between shapes.

Unknown or cross-tenant project, or unknown filter id: **404**, no existence leak. The list is ordered `CreatedAt DESC, Id`, matching every other ordered read in this codebase.

## 5. Validation, and why enums are stored by name

**At save time the definition goes through the same compiler the query endpoints use.** Anything the query would reject — unknown field, disallowed operator, type mismatch, empty group, a guard breach, `conditions` and `filter` together — is a **400** at save, with the same message. A filter that cannot run is not saved and then discovered later.

Dictionary names are resolved during that validation. A name that stops existing afterwards (a renamed status) resolves to no Id and the filter matches nothing — the same honest behaviour an ad-hoc filter has, not an error.

Three further guards:

- **Name:** required, trimmed, 1–100 characters. Duplicate name within the project → **409**.
- **Count:** at most 100 saved filters per project → **409**.
- **Size:** the serialized definition is capped at 8 KB → **400**. The translator's guards bound the *number* of conditions but nothing bounds the *length* of a `Contains` value, so without this a single filter could persist megabytes.

**Enums are serialized by name.** `FilterOperator` and `FilterLogic` are stored as `"Contains"`, `"Or"` — never as ordinals. Storing ordinals would make the enum's declaration order load-bearing for data at rest: reordering it, which nothing else forbids, would silently reinterpret every saved filter into a different one. `FilterCatalogService` already depends on that order for *display*, which is exactly the sort of adjacent dependency that makes reordering look harmless.

## 6. Testing

- **FR-B2 gate extension:** tenant B cannot see A's saved filter through RLS, proven at the database with no EF in the path — the same shape as the existing cases.
- **Erasure:** deleting the owning user removes the row, proving the central `ITenantOwned` cascade covered the new table.
- Cross-tenant read/update/delete each return **404**, not 403 and not someone else's data.
- **Round trip:** a nested `Or`-of-`And` definition saved and re-read is byte-equivalent, including operator names — asserted on the raw stored JSON, so an ordinal-serialising regression fails here rather than years later.
- Save-time validation rejects each class the query rejects, with the same message.
- Duplicate name → 409; the 101st filter → 409; a 9 KB definition → 400.
- An empty definition is rejected.

## 7. Success criteria

A user can name, list, update and delete filters per project; a saved filter is validated when saved rather than when run; the new table has both isolation layers, an FR-B2 gate case, and automatic erasure; there is still exactly one path from filter input to predicate.

## 8. Notes / follow-ups

Remaining in this epic: httpOnly-cookie auth.
