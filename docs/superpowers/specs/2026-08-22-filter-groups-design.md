# Cross-field OR and nested filter groups

**Status:** Approved (2026-08-22)
**Epic:** Post-UI backend follow-ups — **1 of 4**.
**Depends on:** nothing. **Depended on by:** filtered metrics/gap-checks, saved filters — both consume this shape, so it lands first.

FR-E4 shipped a flat AND: `{ conditions: [...] }`, every condition joined with `AndAlso`. That answers "In Progress **and** assigned to Alice" and cannot answer "In Progress **or** In Review", which is the first thing anyone asks a filter for. This spec adds boolean grouping.

---

## 1. Scope & non-goals

**In scope:** a recursive `FilterGroup` with `And`/`Or` logic, bounded in depth and size, translated by the same Expression-tree builder; the four new guards surfaced on `GET /api/filter-catalog`.

**Out of scope:**
- **A nested-group UI.** The SPA's `FilterBuilder` keeps sending flat `conditions`, which keeps working unchanged. Building a tree editor is a Web UI item, not a backend one.
- **`Not` / negation.** Every field that needs it already has `Neq` or `IsNull`. A group-level `Not` inverts the unassigned-row semantics that `Neq` was deliberately written to preserve, and nothing has asked for it.
- **Cross-project filters.** The project scope stays in the route.

## 2. Decisions locked in

- **The injection boundary does not move.** Grouping changes only how already-built predicates are *combined* (`Expression.OrElse` beside the existing `Expression.AndAlso`). No new user string reaches a member access, a SQL fragment, or a format string. Every leaf still goes through the same closed catalog and the same `Expression.Constant` values.
- **`conditions` keeps working, unchanged and untouched.** It is what the shipped SPA sends. It stays the flat-AND path with identical semantics.
- **Both together is an error, not a merge.** A request carrying a non-empty `conditions` *and* a `filter` gets **400**. Silently AND-ing them would make the request mean something the client did not write.
- **No new dependency, no migration, no new table** — so the FR-B2 gate is untouched.

## 3. The shape

```jsonc
{ "filter": { "logic": "Or",
              "conditions": [ { "field": "status", "op": "Eq", "value": "In Progress" } ],
              "groups":     [ { "logic": "And", "conditions": [ ... ] } ] },
  "page": 1, "pageSize": 50 }
```

A group holds conditions, nested groups, or both; all of its parts — leaves and subgroups alike — are joined by its own `logic`. `filter` absent (or `conditions` absent/empty) means unfiltered, exactly as today.

## 4. An empty group is a validation error

This is the one semantic trap worth naming. An empty `And` group is vacuously **true** — it matches every issue. An empty `Or` group is vacuously **false** — it matches none. Same client bug, opposite blast radius, and neither is what anyone meant to write.

So **every group must contain at least one condition or nested group, including the root**, and an empty one returns 400 rather than silently resolving to all-or-nothing. Omitting `filter` entirely is how a caller says "no filter" — that remains unambiguous because it is the absence of a group, not an empty one.

## 5. Guards

Four limits, all enforced over the whole tree rather than per node — a per-node limit is trivially defeated by adding nodes:

| Guard | Value | Counted over |
|---|---|---|
| `maxConditions` | 20 | every leaf condition in the tree |
| `maxGroups` | 10 | every group node, root included |
| `maxDepth` | 3 | root is depth 1 |
| `maxInValues` | 100 | per `In` condition (unchanged) |

`GET /api/filter-catalog` gains `maxDepth` and `maxGroups` beside the two it already serves, for the same reason it serves the others: a client that has to guess a limit will hand-maintain a copy of it, and that copy will drift.

## 6. Testing

- **Pure unit tests, no database** — the translator is unchanged in that respect. Depth, group-count and total-condition limits each rejected at the boundary (n allowed, n+1 refused); an empty group at the root and an empty group nested; `And`-of-`Or` and `Or`-of-`And` producing the intended predicate.
- **The load-bearing integration test:** an `Or` across two *different* fields returns the union — a set strictly larger than either arm alone and strictly smaller than the project. Asserting exact seeded keys, so the test cannot pass on an accidental "match everything".
- **Regression:** every existing `conditions` test still passes untouched; `conditions` + `filter` together is 400.
- **Injection stays inert:** the existing `IssueFilterSecurityTests` case is repeated *inside an `Or` group* — combining predicates must not open a path that a single predicate closed.
- The FR-B2 gate stays untouched and green.

## 7. Success criteria

A user can express "status is In Progress **or** In Review, **and** assignee is Alice"; the four guards each reject at their boundary with a message naming what was exceeded; an empty group is refused rather than guessed at; `conditions` behaves exactly as before.

## 8. Notes / follow-ups

Remaining in this epic: filtered metrics and gap-checks; saved filters; httpOnly-cookie auth.
