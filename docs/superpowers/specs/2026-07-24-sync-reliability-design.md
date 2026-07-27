# Phase 7 — Hardening: Sync reliability design

**Status:** Approved (2026-07-24)
**Epic:** Phase 7 (Hardening)
**This spec:** Spec **2 of 3** of Phase 7.

- Spec 1 — Tenant data lifecycle (GDPR erasure + retention): **shipped** to main.
- **Spec 2 (this doc) — Sync reliability:** correctness and efficiency hardening of the sync path.
- Spec 3 — Security posture: rate-limiting breadth, HSTS/redirect, token-handling audit.

No new user-facing features. This spec fixes a data-corruption bug, a concurrency race, and an N+1 pattern in the ingest path, and closes one carried-over test-architecture item.

---

## 1. Why this spec exists (verified findings)

All confirmed against the codebase, not taken from notes:

1. **Cross-project issue-id collision (data corruption) — the priority.** Four unique indexes and their lookups assume a Jira issue id is unique per *tenant*. It is only unique per *Jira site*, and a tenant can hold multiple `JiraConnection`s (projects hang off connections via `JiraProject.JiraConnectionId`). Issue id `10001` exists on every Jira site.
2. **`AlreadyRunning` TOCTOU.** `SyncOrchestrator.RunProjectAsync` (`SyncOrchestrator.cs:33`) and `RunRederiveAsync` (`:130`) both query `SyncRuns` for a `Running` row and then insert one. Two concurrent calls both see none and both insert. `SyncRunConfiguration` has no index on `Status`, so nothing prevents it.
3. **N+1 reads and per-row round-trips.** Extract (`SyncOrchestrator.cs:68-89`) issues one `FirstOrDefaultAsync` per issue. Ingest (`ChangelogIngestService.cs:22-37`) issues one per snapshot **and** calls `SaveChangesAsync` inside the loop. Derivation (`DerivationService.cs:47`) issues one per snapshot.

### 1.1 The collision, concretely

A tenant connects to `acme.atlassian.net` (project A) and `other.atlassian.net` (project B); both sites have issue `10001`. Syncing B, the extract lookup `RawIssueSnapshots.FirstOrDefault(x => x.JiraIssueId == "10001")` matches **A's** row (same `EndUserId`, same `JiraIssueId`). It overwrites A's payload with B's data while leaving `JiraProjectId` pointing at A. A's data is silently corrupted; B never gets its own row; derivation then produces wrong `Issues` for A and none for B. `DerivationService.cs:47` is the worst instance — it attaches B's derived fields onto A's `Issue` entity.

The unique indexes encode the same false assumption, so they cannot simply be left as-is: once the lookups are scoped per project (§3), a second project's insert of issue `10001` would hit a duplicate-key failure on the tenant-wide unique index. **Index and lookup are two halves of one fix and must change together.**

## 2. Decisions locked in brainstorming

- **The collision fix is the priority item of this spec**, not a follow-up.
- **Efficiency scope: batch lookups per page/project and hoist `SaveChanges` out of the ingest loop.** No move to `ON CONFLICT`/`ExecuteUpdate` bulk upserts — YAGNI at this scale, and the riskiest possible rewrite of the most correctness-sensitive path.
- **TOCTOU fix: a partial unique index + catching the violation** (Approach A). Advisory locks and `SELECT … FOR UPDATE` were rejected — they leave the invariant invisible in the schema, need raw SQL in the write path, or guard the wrong table.
- **One carried-over item is in scope: the retention-fixture decoupling.** The sprint take-last heuristic, the empty-embedded-changelog defensive edge, and `RemoveRange → ExecuteDelete` remain deferred.

## 3. The collision fix (Task 1)

**Widen four unique indexes.** Each is strictly more permissive (uniqueness on the narrower key implies uniqueness on the wider one), so existing rows always satisfy the new index and **no data migration is required**.

| Table | from | to |
|---|---|---|
| `RawIssueSnapshots` | `(EndUserId, JiraIssueId)` | `(EndUserId, JiraProjectId, JiraIssueId)` |
| `RawChangelogs` | `(EndUserId, JiraIssueId)` | `(EndUserId, JiraProjectId, JiraIssueId)` |
| `Issues` | `(EndUserId, JiraIssueId)` | `(EndUserId, JiraProjectId, JiraIssueId)` |
| `IssueChangeHistory` | `(EndUserId, JiraIssueId, JiraHistoryId, Field)` | `(EndUserId, JiraProjectId, JiraIssueId, JiraHistoryId, Field)` |

**Scope three lookups** to add `x.JiraProjectId == projectId`:
- `SyncOrchestrator.cs:70` (extract, `RawIssueSnapshots`)
- `ChangelogIngestService.cs:27` (ingest, `RawChangelogs`)
- `DerivationService.cs:47` (derivation, `Issues`)

The fourth lookup — the change-history dedup HashSet at `DerivationService.History.cs:14` — is **already** project-scoped, so only its unique index widens.

**These land together in one task.** Scoping without widening turns silent corruption into a hard duplicate-key sync failure; widening without scoping leaves `FirstOrDefault` matching across projects and picking one nondeterministically. Neither is a valid intermediate.

**The proof (fails on `main` today):** one tenant, two connections, both projects containing issue `10001`; sync both; assert two distinct `RawIssueSnapshots` / `Issues` rows exist, each with the correct `JiraProjectId` and its own payload. On the current schema this corrupts project A; after the fix the projects are independent. This test is the spec's centrepiece.

## 4. The `AlreadyRunning` TOCTOU (Task 2)

**A partial unique index** enforces the invariant in the database:

```sql
CREATE UNIQUE INDEX "IX_SyncRuns_OneRunningPerProject"
  ON "SyncRuns" ("JiraProjectId") WHERE "Status" = 'Running';
```

At most one `Running` row per project. The existing pre-check in both methods **stays** as a cheap fast path (returns `AlreadyRunning` without attempting the insert in the common case); the index is the guarantee for the race.

**Catch the violation precisely.** A lost race throws `DbUpdateException` wrapping `Npgsql.PostgresException`. Translate to `AlreadyRunning` **only** when `SqlState == "23505"` **and** `ConstraintName == "IX_SyncRuns_OneRunningPerProject"`. Matching `23505` alone would misreport an unrelated unique violation (including a §3 widened index) as "already running"; any other exception propagates unchanged so real errors still fail loudly. Both call sites share one private helper.

**The string-conversion caveat.** `SyncRun.Status` is stored via `HasConversion<string>()`, so the index filter is `WHERE "Status" = 'Running'` — the converted value. If storage ever changed to `int`, the filter would silently match nothing and the guarantee would vanish with every test still green. A test therefore inserts a second `Running` row **directly via raw SQL** and asserts the database rejects it with `23505` — proving the constraint bites at the DB level, independent of the app's catch logic. A second test drives two concurrent `RunProjectAsync` calls and asserts exactly one starts.

**A stuck-run hazard the index sharpens (accepted, not solved here).** A run row left `Running` — e.g. the process crashes mid-sync — now permanently blocks that project: the partial index rejects every new `Running` row, and the retention purge (Spec 1) never deletes `Running` rows. This risk already exists today through the pre-check (a stuck `Running` row already makes every call return `AlreadyRunning`); the index only makes the block airtight rather than racy. A stuck-run reaper (mark runs `Running` past some age as `Failed`) is **out of scope** for this spec and recorded as a follow-up — solving it here would mean guessing a timeout policy, which is its own design question.

## 5. Batching (Task 3)

The scoping edits from §3 and the N+1 removal are the same lines: replace each per-row `FirstOrDefaultAsync` with one project-scoped query into a `Dictionary<string, T>` keyed by `JiraIssueId`, then look up in memory.

- **Extract** (`SyncOrchestrator.cs:68-89`): per page, load existing snapshots for the page's issue ids in one query (`… && pageIds.Contains(x.JiraIssueId)`). Keep the existing per-page `SaveChanges` — a page is a natural batch that keeps memory bounded.
- **Ingest** (`ChangelogIngestService.cs:22-37`): one query for the project's existing changelogs before the loop, and **hoist `SaveChangesAsync` out of the loop** to a single call after it. This is the larger win, since ingest currently saves once per snapshot.
- **Derivation** (`DerivationService.cs:47`): one query for the project's existing issues into a dictionary, replacing the per-snapshot lookup.

**Deliberately untouched:** the dict-resolver caches (`ResolveType/Status/Priority/User/Sprint/Team` already cache by jira-id within a run); the extract per-page save cadence; and any bulk-upsert rewrite.

**Boundary note:** ingest's `AssembleHistoriesJsonAsync` calls Jira per snapshot when the embedded changelog is incomplete. Those network calls stay per-issue by necessity — this spec removes redundant *database* round-trips only, not Jira calls.

## 6. The retention-fixture decoupling (Task 4)

`SyncRunRetentionTests.Purge_leaves_recent_runs_alone` asserts an exact count that now implicitly depends on the shared `TenantDeletionTestData.SeedFullGraphAsync` fixture's 2020 `SyncRuns` row always being purge-eligible. Give `SeedFullGraphAsync` an optional `bool seedSyncRun = true` parameter; the retention tests call it with `false` and seed their own runs, removing the cross-fixture coupling. Pure test-architecture change; every assertion stays, and the erasure-completeness gate (which relies on all 16 tables being seeded) keeps the default `true`.

## 7. Isolation & constraints

- All production reads/writes stay on the injected tenant-scoped `AppDbContext`. No `IgnoreQueryFilters`, privileged factory, or `FromSql` is added to any production path; the retention job's metadata read (Spec 1) remains the one sanctioned privileged read, and there is still no privileged writer.
- **No new tenant table** — index changes only — so the FR-B2 gate is untouched.
- Tokens never logged or stored in `SyncRun.Error` (the failure path keeps the existing curated-message handling).
- The incremental watermark keeps filtering `Kind == Sync && Status == Succeeded`; none of these changes touch that query.

## 8. Testing

- **Collision (centrepiece):** two connections, one tenant, colliding issue id → two independent rows after the fix; fails on `main`.
- **TOCTOU:** raw-SQL duplicate `Running` row rejected with `23505`; two concurrent `RunProjectAsync` → exactly one starts.
- **Batching = behaviour preservation:** the existing sync/ingest/derivation suites stay green (correct output is unchanged; the win is round-trips, and asserting query counts would be brittle). One added ingest test confirms a re-sync still upserts without duplicate rows after the save is hoisted.
- **Retention decoupling:** `SyncRunRetentionTests` pass with the fixture seeding no `SyncRuns` row; the erasure-completeness gate still seeds all 16 tables.
- No migration drift; full suite green; Release build clean.

## 9. Success criteria

- A tenant with two Jira connections syncs both projects without cross-project corruption — proven by a test that fails before the fix.
- Concurrent sync requests for one project produce at most one `Running` run, enforced by the database.
- Ingest no longer saves once per snapshot; per-row lookups in extract/ingest/derivation become per-page/per-project batched reads.
- The FR-B2 gate is untouched and green; two index-only migrations; no data migration; no drift.

## 10. Notes / follow-ups

- Still deferred: the sprint take-last heuristic (commented as deliberate in `DerivationService`), the empty-embedded-changelog defensive edge in `ChangelogIngestService`, and `SyncRunRetentionJob`'s `RemoveRange → ExecuteDelete`. None is a correctness risk at current scale.
- **New follow-up surfaced by §4:** a stuck-`Running`-run reaper. The partial index makes a crashed sync a hard, permanent block on the project until the row is cleared by hand. Worth a small maintenance job (mark `Running` runs older than N minutes as `Failed`) in a later spec; it needs a timeout-policy decision, so it is not bundled here.
- Spec 3 (security posture) is the last Phase 7 spec: rate-limiting breadth beyond the six auth actions (`POST /sync` and `/rederive` are currently unguarded and call out to Jira), HSTS/HTTPS-redirect posture, and a token-handling audit.
