# Module D — Sync engine: Extract → Land (design)

**Status:** Approved (2026-07-01)
**Epic:** #16 — Phase 5 / Module D (Sync / ETL engine)
**This spec:** Spec **2 of 3** of Module D. Consumes Spec 1's schema (`RawIssueSnapshots`, `SyncRuns`) and Module C config (projects, JQL queries, sync interval, encrypted connection token).

- ✅ Spec 1 — storage schema (merged).
- **Spec 2 (this doc) — extract → land:** scheduler + incremental Jira pull + raw landing + `SyncRun` recording.
- Spec 3 — transform: raw → modeled derivation (dictionaries, changelog history, hierarchy, normalization, re-derivation).

Covers **FR-D1** (scheduled sync), **FR-D2** (incremental & idempotent), **FR-D5 (raw half)** (persist raw payload), and the **basic parts of FR-D7** (resilience: 429 backoff, per-project run status). Modeled derivation, changelog history tables, hierarchy, and normalization are **Spec 3**.

---

## 1. Scope & non-goals

**In scope:** ambient tenant context (unify HTTP + background); a Quartz.NET in-memory tick with a due-project finder; a per-project extract orchestrator; an `IJiraApiClient` search extension; idempotent upsert into `RawIssueSnapshots`; the `SyncRun` lifecycle; a manual "sync now" endpoint; a per-project sync-status read endpoint; basic 429 backoff.

**Out of scope (Spec 3):** any write to `Issues`, the dictionary tables, or `IssueChangeHistory`; normalization; hierarchy resolution; re-derivation. Spec 2 writes only `RawIssueSnapshots` and `SyncRuns`. No new tables (reuses Spec 1's schema); the only new dependency is Quartz.NET.

## 2. Decisions locked in brainstorming

- **Background tenant context: ambient AsyncLocal.** A single `AmbientCurrentTenant : ICurrentTenant` backed by `AsyncLocal<Guid?>` with `IDisposable BeginScope(Guid endUserId)`; HTTP sets it via middleware from the JWT `endUserId` claim; background sets it via `BeginScope(project.EndUserId)`. Replaces `HttpContextCurrentTenant`.
- **Scheduler: Quartz.NET in-memory, due-poller model.** One `SyncTickJob` on a 1-minute simple trigger; the job finds due projects and runs the orchestrator per project. No per-project trigger bookkeeping.
- **"Sync now" runs inline** (synchronous within the HTTP request's tenant scope) — deterministic and WireMock-testable.
- **Cross-tenant due-finder read** via `IgnoreQueryFilters` on scheduling metadata only (project id + EndUserId + interval) — the single deliberate cross-tenant read; all issue reads/writes stay tenant-scoped.

## 3. Tenant context refactor (touches the FR-B2-gate-protected HTTP path)

- `src/JiraUnpacked.Domain/Abstractions/ICurrentTenant.cs` unchanged (`Guid? EndUserId`).
- New `ITenantScope { IDisposable BeginScope(Guid endUserId); }` (Domain.Abstractions).
- New `AmbientCurrentTenant : ICurrentTenant, ITenantScope` (Infrastructure.Tenancy): `AsyncLocal<Guid?>`; `EndUserId => _current.Value`; `BeginScope` pushes the value and returns a disposable that restores the prior value. Registered **singleton**, exposed as both `ICurrentTenant` and `ITenantScope`.
- New `TenantScopeMiddleware` (Api): after `UseAuthentication`/`UseAuthorization`, before `MapControllers`; if the request carries an `endUserId` claim, wrap the pipeline in `using scope.BeginScope(id)`; otherwise no scope (EndUserId stays null → `Guid.Empty` filter → deny-by-default).
- **Remove** `HttpContextCurrentTenant`; drop its `ICurrentTenant` registration in favor of `AmbientCurrentTenant`.
- **Ordering invariant:** `BeginScope` must run before `AppDbContext` is constructed in that flow (its ctor snapshots `EndUserId`). Middleware satisfies this for HTTP; the orchestrator satisfies it for background (scope opened before resolving `AppDbContext`).
- The full suite + the FR-B2 gate re-verify HTTP isolation after the swap.

## 4. Scheduler + due-finder

- Add Quartz.NET; register `AddQuartz` with `SyncTickJob` + a `SimpleSchedule` repeating every 1 minute; `AddQuartzHostedService`.
- `SyncTickJob` is thin: resolve `IDueProjectFinder`, get due projects, and for each call `ISyncOrchestrator.RunProjectAsync(dueProject, ct)`. Catches/logs per-project errors so one failure doesn't stop the loop.
- `IDueProjectFinder.GetDueProjectsAsync(ct)` → `IReadOnlyList<DueProject>` where `DueProject(Guid ProjectId, Guid EndUserId, int SyncIntervalMinutes)`. A project is due when: it has ≥1 JQL query configured, has no `Running` run in flight, and (no `Succeeded` run yet OR `lastSucceeded.StartedAt + interval ≤ now`). Implemented via a dedicated read using `IgnoreQueryFilters` (scheduling metadata only — never issue payloads), on the privileged/owner connection.

## 5. Per-project extract orchestrator

`ISyncOrchestrator.RunProjectAsync(DueProject project, CancellationToken ct)` (also invoked by the manual endpoint with the current project):

1. `using _tenantScope.BeginScope(project.EndUserId)` — establishes EF filter + RLS GUC.
2. Guard: if a `Running` run already exists for the project → skip/409.
3. Insert `SyncRun { Status = Running, StartedAt = now }`.
4. Watermark = the project's most recent `Succeeded` run's `StartedAt`; if none, no `updated` bound (full pull).
5. Load the project's connection, `Unprotect` the token → `JiraCredentials` (in-memory only).
6. For each configured JQL query: build `"(" + jql + ") AND updated >= '<watermark>'"` (omit the `AND` on first pull), then loop `SearchIssuesPageAsync` on `nextPageToken` until null; upsert each returned issue into `RawIssueSnapshots` by `(EndUserId, JiraIssueId)` (insert or update `Payload` + `FetchedAt`), counting processed issues.
7. On success: `SyncRun` → `Succeeded`, `FinishedAt`, `IssuesProcessed`, `NextRunAt = now + interval`.
8. On failure (any exception, incl. exhausted 429 retries): `SyncRun` → `Failed`, `Error` (safe message, no token), `FinishedAt`, `NextRunAt = now + interval`. Watermark unchanged → next run re-pulls from the same point; upsert makes re-pull idempotent.

## 6. Jira client extension

`IJiraApiClient.SearchIssuesPageAsync(JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct)` → `SearchPage(IReadOnlyList<RawIssue> Issues, string? NextPageToken)` where `RawIssue(string JiraIssueId, string RawJson)`.
- Calls Jira's token-paginated enhanced search `POST /rest/api/3/search/jql` with body `{ jql, maxResults, nextPageToken?, expand: ["changelog"] }`.
- Maps each issue element to `RawIssue(id, <the issue element's raw JSON>)`; returns the response `nextPageToken` (null when the last page).
- Reuses the existing `Build`/`EnsureOk` error mapping. **429 handling:** honor `Retry-After` (seconds) with a bounded number of retries; on exhaustion throw `JiraApiException(RateLimited)`.

## 7. API endpoints (all `[Authorize]`, tenant-scoped)

- `POST /api/projects/{projectId}/sync` → loads the tenant-scoped project (404 if not found/cross-tenant); if a `Running` run exists → 409; else runs `RunProjectAsync` inline to completion and returns **200** with the completed `SyncRun` summary (status, issuesProcessed, error, nextRunAt).
- `GET /api/projects/{projectId}/sync-runs` → recent `SyncRun`s for the project (id, status, startedAt, finishedAt, issuesProcessed, error, nextRunAt), newest first, paged/capped. FR-D7 observability.

## 8. Error handling & resilience

- Invalid/unreachable Jira mid-run → run marked `Failed` with a safe message; the tick continues other projects.
- 429 → `Retry-After` backoff, bounded; then `Failed` (retried next tick).
- Overlap guard prevents concurrent runs of the same project (tick vs manual, or a slow run).
- Idempotency: raw upsert keyed on `(EndUserId, JiraIssueId)`; only successful runs advance the watermark.
- The token is decrypted only in memory for the Jira calls; never logged, never stored in `SyncRun.Error` or any response.

## 9. Testing (WireMock.Net)

- **Client:** `SearchIssuesPageAsync` — paging via `nextPageToken`, the `updated >=` clause present in the posted JQL, `expand=changelog`, and 429→`Retry-After`→retry.
- **Orchestrator (through the `POST …/sync` endpoint, WireMock-backed):** issues land in `RawIssueSnapshots`; re-running is idempotent (no duplicate rows, payload refreshed); a `SyncRun` is recorded `Succeeded` with the right `IssuesProcessed` and `NextRunAt`; a Jira failure yields a `Failed` run and no watermark advance; the second run's posted JQL includes `updated >= <first run's StartedAt>`.
- **Due-finder:** seed runs at various ages/states; assert due vs not-due and the in-flight-`Running` exclusion.
- **Tenant isolation:** tenant A's manual sync lands rows only under A; B's `sync-runs`/data cannot see A's (EF filter + RLS).
- **Scheduler wiring:** a thin smoke test that `SyncTickJob` is registered and resolvable (no reliance on the timer firing).
- Full suite green; **no migration drift** (no schema change).

## 10. Success criteria

- A configured project's query runs on the Quartz tick **and** on demand via `POST …/sync`.
- Issues land in `RawIssueSnapshots` idempotently (re-sync updates, never duplicates).
- Every run is recorded with status / rows / error / next-run; a failed or interrupted run re-pulls from the last good watermark.
- 429s back off and resume.
- Every data read/write is tenant-scoped (isolation test green; FR-B2 gate still green after the ambient-tenant refactor); the only cross-tenant read is the scheduling-metadata due-finder.
- Manual trigger + status endpoints work; full suite green; no drift.

## 11. Notes / follow-ups for Spec 3 (transform)

- Spec 3 reads `RawIssueSnapshots` to derive `Issues`, dictionaries, and `IssueChangeHistory`; pages the dedicated changelog endpoint for truncated histories; resolves `ParentIssueId`; applies normalization; supports re-derivation without re-pull.
- Retention (REQUIREMENTS open item) still deferred.
