# Module D — Sync-engine storage schema (design)

**Status:** Approved (2026-06-29)
**Epic:** #16 — Phase 5 / Module D (Sync / ETL engine)
**This spec:** the *first* of the Module D sub-specs. Module D is decomposed by **layer**:

1. **Spec 1 (this doc) — storage schema:** all sync-engine tables, tenant-isolated, no behavior.
2. **Spec 2 — extract:** scheduler + Jira issue-search client + incremental pull + raw landing + `SyncRuns` recording + basic 429 backoff.
3. **Spec 3 — transform:** raw → modeled derivation (dictionaries, changelog history, hierarchy resolution, normalization, re-derivation).
4. *(optional Spec 4)* resilience/observability hardening + retention.

The schema is built first so the data model is locked before the engine that fills it, and so all tenant-isolation (RLS) lands in one migration + one gate extension.

---

## 1. Scope & non-goals

**In scope:** EF Core entities, `IEntityTypeConfiguration`s, DbSets, EF global query filters, one schema migration, one RLS migration, and an FR-B2 gate-test extension — for the full Module D storage model. **No behavior**: no scheduler, no Jira fetch, no derivation.

**Out of scope (later specs):** issue-search client methods; the scheduler; ingestion/derivation/normalization logic; reporting; retention enforcement. Free-text fields outside the fixed set (e.g. description) are not stored — only `Summary` is kept inline (FR-D6).

## 2. Decisions locked in brainstorming

- **Raw landing grain:** `RawIssueSnapshots` stores the raw payload as Postgres **jsonb**, **latest-per-issue** (`UNIQUE (EndUserId, JiraIssueId)`, upserted each sync). Re-derivation (FR-D5) reads the current raw; growth bounded by issue count.
- **Dictionary grain (mixed, matches Jira's real scoping):**
  - Per **connection**: `Statuses`, `IssueTypes`, `Priorities`, `JiraUsers` — `UNIQUE (EndUserId, JiraConnectionId, JiraId)`.
  - Per **project**: `Sprints`, `Teams` — `UNIQUE (EndUserId, JiraProjectId, JiraId)`.
- **Hierarchy (FR-D4): both** — `Issues.ParentJiraIssueId` (string, canonical, robust to ingest order) **plus** a nullable self-FK `Issues.ParentIssueId → Issues.Id` resolved in a second derivation pass (Spec 3).

## 3. Tables (columns; every table carries `EndUserId` plus a grain key — `JiraProjectId` or `JiraConnectionId` — as noted per table)

Every entity has a surrogate `Id` (Guid) PK and implements `ITenantOwned` (`Guid EndUserId`).

### Landing zone
- **`RawIssueSnapshots`** (Id, EndUserId, JiraProjectId, JiraIssueId, Payload `jsonb`, FetchedAt)
  - `UNIQUE (EndUserId, JiraIssueId)`. FK JiraProjectId → JiraProjects (cascade).

### Dictionaries — per connection (carry EndUserId + **JiraConnectionId**)
- **`Statuses`** (Id, EndUserId, JiraConnectionId, JiraStatusId, Name, StatusCategory) — `UNIQUE (EndUserId, JiraConnectionId, JiraStatusId)`.
- **`IssueTypes`** (Id, EndUserId, JiraConnectionId, JiraIssueTypeId, Name) — `UNIQUE (…, JiraIssueTypeId)`.
- **`Priorities`** (Id, EndUserId, JiraConnectionId, JiraPriorityId, Name) — `UNIQUE (…, JiraPriorityId)`.
- **`JiraUsers`** (Id, EndUserId, JiraConnectionId, JiraAccountId, DisplayName) — `UNIQUE (…, JiraAccountId)`.
- FK JiraConnectionId → JiraConnections (cascade).

### Dictionaries — per project (carry EndUserId + JiraProjectId)
- **`Sprints`** (Id, EndUserId, JiraProjectId, JiraSprintId, Name, State, StartDate?, EndDate?) — `UNIQUE (EndUserId, JiraProjectId, JiraSprintId)`.
- **`Teams`** (Id, EndUserId, JiraProjectId, JiraTeamId, Name) — `UNIQUE (EndUserId, JiraProjectId, JiraTeamId)`.
- FK JiraProjectId → JiraProjects (cascade).

### Modeled core
- **`Issues`** (Id, EndUserId, JiraProjectId, JiraIssueId, JiraKey,
  IssueTypeId? → IssueTypes, StatusId? → Statuses, PriorityId? → Priorities, AssigneeId? → JiraUsers,
  SprintId? → Sprints, TeamId? → Teams,
  StoryPoints `decimal?`,
  ParentJiraIssueId `string?`, ParentIssueId `Guid?` → Issues.Id (self, `OnDelete: Restrict`/no-action to avoid cascade cycles),
  Summary `text` (inline),
  JiraCreatedAt, JiraUpdatedAt, LastSyncedAt)
  - `UNIQUE (EndUserId, JiraIssueId)`. FK JiraProjectId → JiraProjects (cascade). Dictionary FKs are plain `Guid?` to the dict surrogate `Id` with **`OnDelete: NoAction`** (a dictionary-row delete must never cascade-delete issues); grain is enforced by each dict's unique key and resolved at derivation time (Spec 3), not by the FK.

### Change history (append-only, idempotent)
- **`IssueChangeHistory`** (Id, EndUserId, JiraProjectId, JiraIssueId, Field, FromValue `text?`, ToValue `text?`, Author `string?`, ChangedAt, JiraHistoryId)
  - `UNIQUE (EndUserId, JiraIssueId, JiraHistoryId, Field)` — append-only *and* idempotent on re-pull. FK JiraProjectId → JiraProjects (cascade).

### Sync operations
- **`SyncRuns`** (Id, EndUserId, JiraProjectId, StartedAt, FinishedAt?, Status, IssuesProcessed, Error `text?`, NextRunAt?)
  - `Status` enum **{Running, Succeeded, Failed, PartiallyFailed}** stored as **string** (`.HasConversion<string>()`). FK JiraProjectId → JiraProjects (cascade).

## 4. Tenant isolation (keeps the FR-B2 gate green)

- **Layer 1:** every new entity gets `HasQueryFilter(x => x.EndUserId == _currentTenantId)` in `AppDbContext`, identical to the existing pattern.
- **Layer 2:** one `AddSyncEngineRls` migration loops over all new tables emitting `GRANT … TO jiraunpacked_app`, `ENABLE` + `FORCE ROW LEVEL SECURITY`, and `CREATE POLICY tenant_isolation … USING/WITH CHECK ("EndUserId" = NULLIF(current_setting('app.current_tenant', true), '')::uuid)` — mirroring `AddTenantRls` / `AddSemanticLayerRls`.
- **Gate extension:** `TenantIsolationGateTests` gains a fact proving RLS on `Issues` (representative modeled table) via a raw `jiraunpacked_app` connection with the GUC set to tenant B → A's row count 0, B's own 1. No existing assertion weakened.

## 5. Indexing (FR perf: index the fastest-growing tables)

- `EndUserId` index on every table (RLS predicate).
- The unique keys in §3.
- `Issues (EndUserId, JiraProjectId)`, `Issues (EndUserId, ParentJiraIssueId)`, and indexes on the dictionary FK columns.
- `IssueChangeHistory (EndUserId, JiraProjectId, JiraIssueId)` and `(JiraIssueId, Field, ChangedAt)`.
- `SyncRuns (EndUserId, JiraProjectId, StartedAt)`.

## 6. Testing

- **Model tests** (no DB, read `.Model`) à la `SemanticLayerModelTests`: all entities present; unique indexes on the grain keys; enum (`SyncRuns.Status`) stored as string; jsonb column type on `RawIssueSnapshots.Payload`; self-FK on `Issues`.
- **Gate test** (DB-backed): RLS isolation proven on `Issues`.
- **Drift check** clean; **Release build** clean.
- No behavioral tests (no behavior in this spec).

## 7. Success criteria

- All 12 tables migrated with the correct grains and unique keys.
- Every new table tenant-isolated at both layers; the FR-B2 gate covers `Issues`.
- Full solution suite green; no EF migration drift; Release build 0 warnings/0 errors.

## 8. Notes / follow-ups for later specs

- Spec 2 (extract) adds `IJiraApiClient.SearchIssuesAsync` (jql + `updated >= lastSync` + paging + `expand=changelog`) and the scheduler; it upserts `RawIssueSnapshots` and records `SyncRuns`.
- Spec 3 (transform) derives `Issues`/dictionaries/`IssueChangeHistory` from raw, resolves `ParentIssueId`, applies normalization-by-cardinality, and supports re-derivation without re-pull (FR-D5).
- Retention (REQUIREMENTS open item) is deferred; revisit in the optional resilience/observability spec.
