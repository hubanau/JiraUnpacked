# Module D — Sync engine: Transform (raw → modeled) design

**Status:** Approved (2026-07-06)
**Epic:** #16 — Phase 5 / Module D (Sync / ETL engine)
**This spec:** Spec **3 of 3** of Module D — the last piece of the sync engine.

- ✅ Spec 1 — storage schema (merged).
- ✅ Spec 2 — extract → land (merged): scheduler + incremental pull → `RawIssueSnapshots` + `SyncRuns`.
- **Spec 3 (this doc) — transform:** derive the modeled tables from raw.

Covers **FR-D3** (change history from the changelog), **FR-D4** (epic→story hierarchy), **FR-D5 modeled half** (raw→modeled + re-derivation without re-pull), **FR-D6** (normalization by cardinality / dictionaries).

---

## 1. Scope & non-goals

**In scope:** a `RawChangelog` raw store (one migration); a Jira changelog-paging client method; a sync-time **changelog-ingest** step; the Jira-free **modeled derivation** (field-mapped `Issues` + dictionary upsert/FK resolution + `IssueChangeHistory` + epic→story hierarchy); a **re-derive** trigger; wiring the derivation into the existing `SyncOrchestrator`.

**Out of scope:** reporting/metrics and the active/done classification (Module E — `StatusSemantics` is a report-time concern, not consumed here); any change to extract's raw-issue landing (Spec 2).

## 2. Decisions locked in brainstorming

- **Everything is re-derivable.** The full paged changelog is persisted as raw (`RawChangelog`), so re-derivation rebuilds `Issues`, dictionaries, **and** `IssueChangeHistory` from raw with **no Jira calls**. Jira is touched only once, at sync time, to populate `RawChangelog`.
- **`RawChangelog` is a new dedicated table** (latest-per-issue jsonb, tenant-owned + RLS), separate from the immutable `RawIssueSnapshots` search snapshot.
- The derivation is a Jira-free pure function of raw + `FieldMappings`, invoked both **after extract** (in the sync) and **on re-derive**.

## 3. New raw store — `RawChangelog` (the only schema change)

`RawChangelog` (Id, EndUserId, JiraProjectId, JiraIssueId, Payload `jsonb`, FetchedAt), `UNIQUE(EndUserId, JiraIssueId)`; `ITenantOwned`; EF global query filter; Postgres RLS (ENABLE + FORCE + `tenant_isolation` policy + GRANT to `jiraunpacked_app`), in one migration mirroring `AddSyncEngineRls`. FK `JiraProjectId → JiraProjects` (cascade). Indexes: `EndUserId`; the unique key. The FR-B2 gate test extends to prove RLS on `RawChangelog`.

The same migration adds a **`SyncRun.Kind`** column: enum `SyncRunKind { Sync, Rederive }` stored as string (default `Sync`), so `/sync-runs` distinguishes a re-derive from a sync.

## 4. Jira client — changelog paging

`IJiraApiClient.GetIssueChangelogPageAsync(JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct)` → `ChangelogPage(IReadOnlyList<string> ValuesJson, int Total, bool IsLast)` where each `ValuesJson` element is one changelog-history entry's raw JSON (`JsonElement.GetRawText()`). Calls `GET /rest/api/3/issue/{id}/changelog?startAt=&maxResults=` (Jira's classic `startAt`/`maxResults`/`total`/`isLast` paging). Reuses `Build`/`SendAsync`/`EnsureOk` and the existing 429 backoff. Token in memory only. WireMock-tested.

*(The ingest step assembles the collected entries into a single full-changelog JSON array stored in `RawChangelog.Payload`; the modeled `IssueChangeHistory` is parsed from that raw in the derivation step, not here.)*

## 5. Changelog ingest (sync-time — the only Jira-touching step)

For each raw issue just landed by extract:
- Read the **embedded** changelog from the `RawIssueSnapshots` payload (`changelog.histories` + `changelog.total`).
- If complete (`histories.length >= total`) → use it as-is.
- If **truncated** → page `GetIssueChangelogPageAsync` from `startAt = 0` until `isLast`, assembling the full history.
- Upsert the assembled full-changelog JSON into `RawChangelog` by `(EndUserId, JiraIssueId)` (insert or replace `Payload` + `FetchedAt`).

## 6. Modeled derivation (Jira-free — runs at sync **and** re-derive)

`IDerivationService.DeriveProjectAsync(Guid projectId, CancellationToken)` reads only raw (`RawIssueSnapshots` + `RawChangelog`) + config:
- **Field mapping:** load the project's `FieldMappings` (role → JiraFieldId). Per raw issue, read `fields[mappedFieldId]` for `Sprint`/`StoryPoints`/`Team`/`Assignee`/`Status`; read standard `fields.issuetype`/`fields.priority`. (Roles come from `FieldRole { Team, Assignee, Sprint, StoryPoints, Status }`; type/priority are standard fields.)
- **Dictionary upsert + FK resolution (FR-D6):** upsert per-connection dicts (`Statuses`/`IssueTypes`/`Priorities`/`JiraUsers` — keyed by Jira id within the connection) and per-project dicts (`Sprints`/`Teams`); resolve each `Issue` FK to the dict surrogate `Id`. Idempotent via the grain unique keys.
- **Issue upsert** by `(EndUserId, JiraIssueId)`: `JiraKey`, the six dict FKs, `StoryPoints`, `Summary`, `ParentJiraIssueId`, `JiraCreatedAt`/`JiraUpdatedAt`, `LastSyncedAt`.
- **Change history (FR-D3):** parse each `RawChangelog.Payload` → append-only, idempotent insert into `IssueChangeHistory` keyed on `(EndUserId, JiraIssueId, JiraHistoryId, Field)` — `(field, from, to, author, timestamp)` per changelog item.

## 7. Hierarchy second pass (FR-D4)

After all Issues for the project are upserted, resolve `Issue.ParentIssueId` (self-FK) by matching each non-null `ParentJiraIssueId` to a sibling `Issue.JiraIssueId` in the same tenant/project; parents not (yet) ingested stay null (re-affirmed on the next derive).

## 8. Trigger & wiring

- **`SyncOrchestrator` (Spec 2):** after a successful extract, in the same tenant scope, run **changelog-ingest → `DeriveProjectAsync`**; the `SyncRun` (Kind = `Sync`) now reflects the full extract+transform pipeline. A derivation failure marks the run `Failed` (raw is already landed, so a later re-derive can recover without re-pulling).
- **`POST /api/projects/{projectId}/rederive`** (inline, `[Authorize]`, tenant-scoped, like sync-now): runs `DeriveProjectAsync` **only**, from existing raw — no Jira, no token; records a `SyncRun` with Kind = `Rederive`. 404 cross-tenant; 409 if a run is in flight.

## 9. Error handling & idempotency

- All modeled writes are upserts keyed on stable Jira ids; re-running (sync or re-derive) converges to the same state. `IssueChangeHistory` is append-only + idempotent (unique key).
- A changelog-ingest failure (Jira) fails the sync run but leaves raw intact; the modeled derivation can still be re-run later from whatever raw exists.
- Token decrypted only in memory for changelog paging; never logged/returned/stored in `SyncRun.Error`.
- Every read/write tenant-scoped (EF filter + RLS); `RawChangelog` is added to both layers; the gate covers it.

## 10. Testing (WireMock + DB)

- **Client:** `GetIssueChangelogPageAsync` — paging via `startAt`/`isLast`, `total`, 429 reuse.
- **Ingest:** embedded-complete → no dedicated call made; truncated → dedicated endpoint paged and `RawChangelog` upserted; idempotent re-run.
- **Derivation (through `POST /sync`, WireMock):** `Issues` populated with correct field-mapped values + resolved dict FKs; `IssueChangeHistory` built + idempotent (re-sync doesn't duplicate); epic→story `ParentIssueId` resolved; standard type/priority dicts populated.
- **Re-derivation:** after a sync, change a `FieldMapping`, call `POST /rederive`, assert the modeled `Issue` reflects the new mapping AND **no Jira call was made** on `/rederive` (WireMock log empty for that window); Kind = `Rederive` in `/sync-runs`.
- **Tenant isolation:** `RawChangelog` + modeled rows isolated (EF filter + RLS); the FR-B2 gate extended to `RawChangelog`.
- Full suite green; **one migration**; drift clean after it.

## 11. Success criteria

- A sync produces fully modeled data (Issues + dictionaries + change-history + hierarchy) derived from raw; the complete changelog is persisted to `RawChangelog`.
- Re-derive rebuilds all modeled tables from raw with **zero** Jira calls; everything idempotent.
- All new/derived data is tenant-scoped (gate covers `RawChangelog`); `SyncRun.Kind` distinguishes sync vs re-derive.
- FR-D3 / FR-D4 / FR-D5 (modeled + re-derivation) / FR-D6 satisfied. This completes Module D; Module E (reporting) is next.

## 12. Notes

- `StatusSemantics` (active/done) is consumed by Module E reports, not by this transform.
- Retention (REQUIREMENTS open item) remains deferred.
- This spec depends on Spec 2's `SyncOrchestrator`; if the "Module D extract follow-ups" branch (generic `SyncRun.Error`, remove `AddHttpContextAccessor`) merges first, rebase onto it.
