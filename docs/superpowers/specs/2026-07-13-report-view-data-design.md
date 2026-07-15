# Module E — Reporting: View loaded data (FR-E1) design

**Status:** Approved (2026-07-13)
**Epic:** Phase 6 / Module E (Reporting & filters)
**This spec:** Spec **1 of 3** of Module E.

- **Spec 1 (this doc) — View loaded data (FR-E1):** paginated, tenant-scoped browse of a project's ingested issues + single-issue detail with change history.
- Spec 2 — Curated metrics (FR-E2): story counts, average time-in-progress, throughput, status aging (a status-timeline engine over `IssueChangeHistory` + `StatusSemantics`).
- Spec 3 — Curated gap-checks (FR-E3): flagship "epics not done where all child stories are done."

Consumes the modeled data from Module D (`Issues` + dictionaries + `IssueChangeHistory` + epic→story hierarchy). **Read-only.** FR-E4 (generic filter builder) is out of scope for v1.

---

## 1. Scope & non-goals

**In scope:** an `IIssueQueryService` + two `[Authorize]`, tenant-scoped read endpoints — a paginated issue list with resolved dictionary display names, and a single-issue detail with parent/children + change history; one small ordering-index migration.

**Out of scope:** metrics (Spec 2); gap-checks (Spec 3); the generic filter builder and any server-side search/filtering beyond the default order (FR-E4); any write path; new tenant tables (only an index is added).

## 2. Decisions locked in brainstorming

- **Offset pagination** (`page` 1-based + `pageSize`, default 50, max 100) — matches the "pagination on all reads" NFR without the complexity of keyset for v1.
- **No server-side filters/search** in v1 — a default order only; FR-E4 owns filtering.
- **One index migration** — `Issues(EndUserId, JiraProjectId, JiraUpdatedAt)` — so the project-scoped, update-ordered page is served from an index (honors "reports must not table-scan").

## 3. Endpoints (all `[Authorize]`, tenant-scoped)

- **`GET /api/projects/{projectId}/issues?page=&pageSize=`** → `200` `PagedResult<IssueListItem>`; `404` if the project is not the caller's (tenant filter/RLS). `page` defaults to 1, `pageSize` defaults to 50 and is capped at 100. Order: `JiraUpdatedAt DESC, JiraKey ASC` (stable).
- **`GET /api/projects/{projectId}/issues/{jiraIssueId}`** → `200` `IssueDetail`; `404` if the issue isn't in the caller's project.

## 4. DTOs (API contracts)

- `PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)`.
- `IssueListItem(string JiraIssueId, string JiraKey, string? Summary, string? Status, string? IssueType, string? Priority, string? Assignee, string? Sprint, string? Team, decimal? StoryPoints, string? ParentJiraIssueId, DateTime JiraCreatedAt, DateTime JiraUpdatedAt)` — the dictionary fields are the resolved **names** (null when the FK is null / dict row absent).
- `IssueDetail(IssueListItem Issue, IssueRef? Parent, IReadOnlyList<IssueRef> Children, IReadOnlyList<ChangeHistoryEntry> ChangeHistory)`.
- `IssueRef(string JiraIssueId, string JiraKey, string? Summary)`.
- `ChangeHistoryEntry(string Field, string? FromValue, string? ToValue, string? Author, DateTime ChangedAt)`.

## 5. Read model & FK resolution

`IIssueQueryService` over the tenant-scoped `AppDbContext`:
- **List:** a LINQ projection over `_db.Issues.Where(i => i.JiraProjectId == projectId)` with **left-joins** to the dictionary tables to resolve names (`Status.Name`, `IssueType.Name`, `Priority.Name`, `JiraUser.DisplayName` for assignee, `Sprint.Name`, `Team.Name`). The `Issue`→dict FKs were configured with `HasForeignKey` and **no navigation property**, so resolution is an explicit left-join projection (nullable dict → null name) — no entity nav-props, no schema change. `TotalCount` from a `CountAsync` on the same predicate; the page via `OrderBy(...).Skip((page-1)*pageSize).Take(pageSize)`.
- **Detail:** the same projection for the one issue (by `projectId` + `jiraIssueId`); `Parent` resolved by matching `Issue.ParentJiraIssueId` to a sibling `Issue.JiraIssueId` in the project; `Children` = issues whose `ParentJiraIssueId == this.JiraIssueId`; `ChangeHistory` = `_db.IssueChangeHistory.Where(h => h.JiraIssueId == jiraIssueId).OrderBy(h => h.ChangedAt)` projected to `ChangeHistoryEntry`.
- **Project existence:** the service first loads the project tenant-scoped (`_db.JiraProjects.FirstOrDefault(p => p.Id == projectId)`); null → the controller returns 404. Cross-tenant project ids are filtered out (return null → 404), so no existence leak.

## 6. Index migration

`AddIssueUpdatedIndex`: `CREATE INDEX ... ON "Issues" ("EndUserId", "JiraProjectId", "JiraUpdatedAt")`. EF configuration adds `b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraUpdatedAt })` to `IssueConfiguration`. No new table, no RLS change (Issues is already isolated). Drift stays clean after the migration.

## 7. Error handling & isolation

- Unknown/cross-tenant project → 404; unknown issue in a valid project → 404. No existence leak (tenant filter + RLS both deny).
- Every read is tenant-scoped through the injected `AppDbContext`; no `IgnoreQueryFilters`, no privileged factory.
- `pageSize` clamped to `[1, 100]`; `page` clamped to `>= 1`. Empty project → an empty page with `TotalCount = 0` (200, not 404, when the project exists).

## 8. Testing (integration; no WireMock — pure DB reads)

Seed a project with issues + dictionaries + change history via `POST /sync` (WireMock only for the sync setup) OR directly via raw SQL, then assert:
- **List:** resolved names appear (status/type/priority/assignee/sprint/team), pagination works (`TotalCount`, page slicing, `pageSize` cap), default order `JiraUpdatedAt DESC`.
- **Detail:** the issue + its `changeHistory` (ordered) + resolved `parent` + `children` (epic→story).
- **Tenant isolation:** tenant B's list/detail cannot see tenant A's issues (empty/404).
- **Unknown project → 404; empty project → empty page (200).**
- **No migration drift** after the index migration.

Scope reads in tests to the test's own `JiraProjectId` (parallel tests share the DB and reuse fixed Jira ids). Full suite green.

## 9. Success criteria

- An End User lists a project's ingested issues (paginated, human-readable names) and drills into one issue with its change history + hierarchy.
- All reads tenant-scoped (isolation test green); the list is index-served (ordering index present; no table-scan).
- One migration (the ordering index); no drift; Release build clean.

## 10. Notes / follow-ups

- Spec 2 (metrics) builds the status-timeline engine over `IssueChangeHistory` + `StatusSemantics` (active/done) and reuses this read layer's tenant-scoped patterns + DTO style.
- FR-E4 (generic, user-composable filters) remains out of v1; if lightweight list filtering is wanted sooner, it can extend the list endpoint's query, but it is not part of this spec.
