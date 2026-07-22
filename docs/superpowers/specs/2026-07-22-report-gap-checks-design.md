# Module E — Reporting: Curated gap-checks (FR-E3) design

**Status:** Approved (2026-07-22)
**Epic:** Phase 6 / Module E (Reporting & filters)
**This spec:** Spec **3 of 3** of Module E — the final one.

- Spec 1 — View loaded data (FR-E1): **shipped** (paginated issue list + issue detail).
- Spec 2 — Curated metrics (FR-E2): **shipped** (status-timeline engine; story counts, cycle time, throughput, status aging).
- **Spec 3 (this doc) — Curated gap-checks (FR-E3):** a small library of correctness checks over the epic→story hierarchy, flagship being "epics not done where all child stories are done".

Consumes Module D's modeled data and Module C's per-project `StatusSemantics`. **Read-only.** FR-E4 (generic filter builder) remains out of scope for v1.

---

## 1. Scope & non-goals

**In scope:** an `IGapCheck` registry with two checks; a `GapCheckRunner` that loads project data once and runs checks as pure functions; two `[Authorize]` tenant-scoped read endpoints (catalog + run-one); an `IssueTypeClass` classification column on the existing `IssueTypes` dictionary with two connection-scoped configuration endpoints; one migration (the column).

**Out of scope:** the generic filter builder (FR-E4); any write path to issue data; **any new tenant table**; new indexes; additional checks beyond the two below.

## 2. Decisions locked in brainstorming

- **Epics are identified by a configured issue-type classification**, not by a hardcoded `"Epic"` string and not structurally. The classification lives as a `Class` column on the existing `IssueTypes` dictionary table, in the style of `StatusSemantics`.
- **Two checks in v1:** the FR-E3 flagship (parent not Done, all children Done) and its inverse (parent Done, some child still open).
- **A small registry** (`IGapCheck` implementations resolved from DI by id), not fixed methods — this is the "library of correctness checks" the requirement asks for, and adding a check later is one class plus one registration.
- **Catalog + run-one-by-id** endpoints; no run-all summary in v1.
- **Shared context, pure checks:** `GapCheckRunner` performs one tenant-scoped load into a `GapCheckContext`; each check is a pure, synchronous function over it. This mirrors Spec 2's loader-plus-pure-engine split, which made the engine unit-testable with no database, and keeps the isolation surface to a single auditable place instead of one per check.
- **A `NotConfigured` result status.** When no issue type is classified `Epic`, the checks return `NotConfigured` with zero violations rather than a bare empty list — "not set up" must never masquerade as "all clean".
- **Current status only.** Unlike Spec 2, gap-checks never read `IssueChangeHistory` and never call `StatusTimelineEngine.Build`; they reuse only the pure `Classify`. `Done` still resolves through the per-project mapping at query time.

## 3. Epic classification

### 3.1 Storage

A new enum and a column on the existing dictionary entity:

```csharp
public enum IssueTypeClass { Epic, Other }   // JiraUnpacked.Domain.Enums
```

`IssueType` gains `public IssueTypeClass Class { get; set; }`, configured exactly as `StatusSemantic.Class` is:

```csharp
b.Property(x => x.Class).IsRequired().HasConversion<string>().HasMaxLength(16);
```

Migration `AddIssueTypeClass` adds the column with default `'Other'` for existing rows. **No new table**, therefore no RLS policy and **the FR-B2 tenant-isolation gate test is untouched**.

Deliberately a two-value enum rather than a speculative `{ Epic, Story, SubTask, Other }`: only `Epic` is needed, and because the value is stored as a string, adding members later is a non-breaking change.

### 3.2 Sync safety (verified, not assumed)

`DerivationService.ResolveTypeAsync` upserts `IssueTypes` by `(JiraConnectionId, JiraIssueTypeId)` and, on an existing row, assigns **only** `row.Name`. A user's epic classification therefore survives every sync and re-derive.

This is the failure mode that would make the whole feature quietly useless, so it gets an explicit regression test (§7): classify a type, run a re-derive, assert the classification is still `Epic`.

### 3.3 Configuration endpoints — a deliberate break from convention

Every existing mapping endpoint is project-scoped (`api/projects/{id}/status-semantics`, `/field-mappings`). But `IssueTypes` is keyed by `JiraConnectionId` and shared across all projects on that connection. A project-scoped route would tell the caller they are configuring one project while actually changing every project on the connection, so these endpoints live under connections, where the data actually lives:

- **`GET /api/connections/{connectionId:guid}/issue-types`** → the connection's issue types with their current `Class`.
- **`PUT /api/connections/{connectionId:guid}/issue-types`** → bulk-set classes, mirroring the existing bulk `PUT status-semantics` shape: a list of `{ jiraIssueTypeId, class }`. An unknown `jiraIssueTypeId` → **400**; unknown or cross-tenant connection → **404**.

**PUT semantics are additive, not replace-all:** only the issue types named in the request body are updated; any type not listed keeps its current `Class`. Clearing a classification is done by explicitly sending it with `"Other"`. (A replace-all body would mean an omitted type silently loses its `Epic` classification.)

**More than one issue type may be classified `Epic`** — a Jira site can legitimately have several epic-like types. `HasEpicTypeConfigured` is true when *any* type on the connection is `Epic`, and the checks treat every such type as an epic.

The inconsistency with the project-scoped mapping routes is accepted deliberately: silently mislabeled scope in an API is worse than an inconsistent route prefix.

## 4. The check model

```csharp
public sealed record GapCheckInfo(string Id, string Name, string Description);
public enum GapCheckStatus { Ok, NotConfigured }
public sealed record GapCheckOutcome(GapCheckStatus Status, IReadOnlyList<GapCheckViolation> Violations);

public interface IGapCheck
{
    GapCheckInfo Info { get; }
    GapCheckOutcome Run(GapCheckContext context);   // pure, synchronous, no DB
}
```

`GapCheckContext`, built once per request by `GapCheckRunner`, carries:
- `IReadOnlyList<GapCheckIssue> Issues` — `GapCheckIssue(string JiraIssueId, string JiraKey, string? Summary, string? Status, StatusClass Class, string? IssueType, bool IsEpicType, string? ParentJiraIssueId)`
- `ILookup<string, GapCheckIssue> ChildrenByParent` — keyed on `ParentJiraIssueId`
- `bool HasEpicTypeConfigured`

Checks are registered as `IGapCheck` in DI and resolved by `Info.Id`.

## 5. The two checks

Both return `GapCheckStatus.NotConfigured` with zero violations when `HasEpicTypeConfigured` is false.

**Check 1 — `open-epic-all-children-done`** (the FR-E3 flagship).
For each issue where `IsEpicType` and current `Class != Done`: gather `ChildrenByParent[JiraIssueId]`. If there is **at least one child** and **every** child's `Class == Done` → violation. Evidence listed: all children.

> The at-least-one-child guard is load-bearing. Without it, "all children are done" is vacuously true for a childless epic and every one of them gets flagged.

**Check 2 — `done-epic-open-children`** (the inverse).
For each issue where `IsEpicType` and current `Class == Done`: if **any** child's `Class != Done` → violation. Evidence listed: only the non-Done children, since those are what needs attention.

A check itself returns the full violation list in its `GapCheckOutcome`; **`GapCheckRunner` owns ordering and pagination**, sorting by `JiraKey` ascending and slicing with the clamps used in Specs 1–2 (`page < 1 → 1`; `pageSize < 1 → 50`; `pageSize > 100 → 100`) before wrapping the outcome into the API's `GapCheckResult`. Keeping paging in the runner means a new check never has to reimplement it.

The `Children` on a violation are **not** paginated — they are the evidence for that finding and are returned whole.

When a check returns `NotConfigured`, the response still carries a well-formed `PagedResult` with an empty `Items` list and `TotalCount = 0`; the `Status` field is what distinguishes it from a clean result.

A status name absent from `StatusSemantics` classifies as `Other` (never `Done`), consistent with Spec 2.

## 6. Checks API

Both `[Authorize]`, tenant-scoped, read-only.

- **`GET /api/projects/{projectId:guid}/checks`** → the catalog, `IReadOnlyList<GapCheckInfo>`. **404** for an unknown or cross-tenant project.
- **`GET /api/projects/{projectId:guid}/checks/{checkId}?page=&pageSize=`** → `GapCheckResult`. **404** for an unknown or cross-tenant project, or an unknown `checkId`.

DTOs in `src/JiraUnpacked.Domain/Abstractions/GapCheckViews.cs`, reusing Spec 1's `PagedResult<T>`:

```csharp
public sealed record GapCheckChild(
    string JiraIssueId, string JiraKey, string? Summary, string? Status, string Class);

public sealed record GapCheckViolation(
    string JiraIssueId, string JiraKey, string? Summary,
    string? Status, string Class, string? IssueType,
    IReadOnlyList<GapCheckChild> Children);

public sealed record GapCheckResult(
    GapCheckInfo Check, string Status, PagedResult<GapCheckViolation> Violations);
```

`Status` and `Class` serialize as their enum names (`"Ok"` / `"NotConfigured"`; `"Active"` / `"Done"` / `"Other"`), matching Spec 2's convention. `Status` and `IssueType` on a violation are nullable because an issue may have no status or type FK, consistent with Spec 1's `IssueListItem.Status`.

## 7. Read model, isolation, indexing

`GapCheckRunner` performs one tenant-scoped load through the injected `AppDbContext`:
1. the tenant-scoped project existence check → 404;
2. the project's `StatusSemantics` → a `Dictionary<string, StatusClass>` built with **`StringComparer.OrdinalIgnoreCase`** (the engine's case-insensitive classification depends on the caller supplying that comparer — a default dictionary silently makes classification case-sensitive with no test catching it);
3. the project's issues, projected to `GapCheckIssue` with status name, issue-type name, and `IsEpicType` resolved via correlated subqueries — no navigation properties, Spec 1's projection style.

Children are grouped **in memory** into one `ILookup`, so parent→children resolution is a single pass with **no N+1 query**, regardless of how many checks run.

- **No `IgnoreQueryFilters`, no privileged/superuser factory, no `FromSql`, no `.Include(...)`** in production code.
- All five tables touched (`JiraProject`, `StatusSemantic`, `Status`, `IssueType`, `Issue`) already carry both isolation layers — Layer 1 query filters in `AppDbContext` and Layer 2 Postgres RLS from Module C/D.
- **No new tenant table, so the FR-B2 gate test is untouched and stays green.**
- **No new index.** `Issues(EndUserId, JiraProjectId)` serves the project load; the existing `Issues(EndUserId, ParentJiraIssueId)` remains available. This is the only Module E spec that adds no index.

## 8. Testing

**Unit (no database)** — each check as a pure function over a hand-built `GapCheckContext`:
- flagship flags an open epic whose children are all Done;
- flagship does **not** flag a childless epic (the vacuous-truth guard);
- flagship does not flag when any child is still open;
- inverse flags a Done epic with an open child, listing only the open ones;
- both return `NotConfigured` when no issue type is classified `Epic`;
- a status absent from the mapping classifies `Other`, never `Done`.

**Integration** via `JiraUnpackedApiFactory`, seeding by raw SQL **scoped to each test's own `JiraProjectId`** (parallel tests share one Postgres and reuse fixed Jira ids), asserting exact violations:
- the catalog lists both checks;
- each check's violations against seeded data;
- unknown `checkId` → 404; cross-tenant project → 404 on both routes;
- empty project → `Ok` with zero violations at **200**, not 404;
- the `NotConfigured` path when no type is classified `Epic`;
- the configuration endpoints: bulk PUT round-trips, unknown issue-type id → 400, cross-tenant connection → 404.

**Two regression tests protecting the design's load-bearing assumptions:**
1. **Classification survives a re-derive** — classify a type `Epic`, `POST /rederive`, assert it is still `Epic` (§3.2).
2. **Re-classifying a status flips a violation with no Jira call** — flip a `StatusSemantic`, assert a violation appears/disappears, with **no WireMock instantiated anywhere in the file**.

## 9. Success criteria

- An End User classifies an issue type as `Epic`, then lists the curated checks and runs them, seeing violating epics with the children that justify each finding — all from stored data, with **no Jira calls**.
- Re-classifying a status changes check results on the next request; classifications survive syncs.
- All reads tenant-scoped; the FR-B2 gate unchanged and green; no new tenant table; no new index.
- One migration (the `Class` column); no drift; Release build clean.

## 10. Notes / follow-ups

- Module E closes with this spec. FR-E4 (user-composable filters) remains deferred; Phase 7 (Hardening) is next.
- Further checks are cheap to add against the registry — "statuses in use but missing from `StatusSemantics`" (which would surface the config gap that silently skews Spec 2's metrics) and "orphaned parent links" (`ParentJiraIssueId` set but unresolved) were both considered and deliberately deferred out of v1.
- If a run-all summary view is wanted later, the runner already loads context once, so a summary endpoint is a thin addition over the existing registry.
