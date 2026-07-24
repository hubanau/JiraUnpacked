# Jira Unpacked — Requirements

A self-hosted, multi-tenant Jira data warehouse with a reporting and semantic layer.
It periodically pulls issue data from users' Jira Cloud instances, stores it normalized
with full field-change history, and exposes filters and reports for gap analysis and
delivery metrics.

> Conceptually a self-hosted cousin of eazyBI / Atlassian Analytics / the Jira
> connectors in Fivetran & Airbyte. Patterns below borrow deliberately from those tools.

---

## 1. Technology stack (minimal, Claude-compatible)

| Concern | Choice |
|---|---|
| Backend | ASP.NET Core (Web API) |
| ORM / migrations | EF Core |
| Database | AWS RDS — **PostgreSQL** (preferred; enables Row-Level Security) or SQL Server |
| Background sync | Quartz.NET or Hangfire (in-process scheduler) |
| Tests | xUnit (unit + integration) |
| Source / tickets / CI-CD | GitHub (repo + Issues + Actions) |
| Dev tooling | Visual Studio + Claude Code |
| Environments | **test** + **production** (no separate staging for v1) |
| Secrets | AWS Secrets Manager / Parameter Store, per-environment paths |
| Token encryption | AWS KMS envelope encryption |

---

## 2. Locked decisions

1. **Admins are platform operators.** The tenant boundary is the **End User** — each End User is an isolated tenant; Admins manage the system, not a shared org.
2. **Admins are seeded** via config/migration. No admin self-registration.
3. **Jira Cloud only** (API-token + email Basic auth). No Server/Data Center support.
4. **Fixed set of standard + user-mapped fields only.** No arbitrary custom-field ingestion in v1.
5. **Curated library of reports/gap-checks first.** Generic user-composable query builder is a later phase.
6. **Polling, hourly minimum.** No webhooks / near-real-time in v1.

---

## 3. Functional requirements

Acceptance criteria are written Given / When / Then. IDs are stable references for Issues/commits.

### Module A — Identity & access

**FR-A1 — Email-first registration (both roles)**
Account creation order: email → username → strong password → email verification.
- *Given* a valid invite path, *when* a user submits email, username, and a password meeting the strength policy, *then* the account is created in `PendingVerification` state and a verification code is emailed.
- *Given* a `PendingVerification` account, *when* the correct, unexpired code is submitted, *then* the account becomes `Active`; registration cannot complete otherwise.
- Verification codes expire and are rate-limited; resend is throttled.

**FR-A2 — No open registration; entry path = role**
- End Users may register **only** with a valid invite code. There is no public role selector.
- *Given* an invalid, expired, or already-used invite code, *when* registration is attempted, *then* it is rejected before any account is created.

**FR-A3 — Admin seeding**
- The admin(s) are seeded via config/migration at deploy time.

**FR-A4 — Invite codes (Admin)**
- *Given* an Admin, *when* they create an invite code, *then* a single-use, expiring code is generated and shareable.
- Used or expired codes cannot be reused.

**FR-A5 — Block End User (Admin)**
- *Given* an Admin, *when* they block an End User, *then* that user can no longer authenticate or trigger syncs, and their scheduled syncs are paused. Block is reversible.

**FR-A6 — Strong password policy**
- Enforced minimum length + complexity; stored only as an Argon2/bcrypt hash; never logged or returned.

### Module B — Tenancy & data model

**FR-B1 — Shared-schema multi-tenancy**
- Every tenant-scoped table carries an `EndUserId` discriminator (and `JiraProjectId` where applicable).

**FR-B2 — Central isolation enforcement**
- Tenant filtering is enforced by a global EF Core query filter on `EndUserId`, never by hand-written `WHERE` clauses per query.
- On PostgreSQL, Row-Level Security is added as defense-in-depth.
- *Acceptance:* an integration test proves tenant X cannot read tenant Y's rows through any read path.

### Module C — Jira connection & configuration

**FR-C1 — Connection model**
Credentials are per Jira **instance**, not per project. Model is **Connection (base URL + email + API token) → one or more Projects**.
- *Given* an End User, *when* they add a connection with valid credentials, *then* it is validated against Jira and the token is stored KMS-encrypted.

**FR-C2 — Projects under a connection**
- An End User can register several Jira Projects per connection.

**FR-C3 — Queries per project**
- An End User can attach one or more JQL queries to each project.

**FR-C4 — Sync frequency per project**
- Configurable per project; minimum interval = hourly.

**FR-C5 — Field semantics mapping (the keystone)**
Per project, the End User maps which field means team / assignee / sprint / story points / status, and which statuses are **active** vs **done**.
- All "done"/"active" logic in reports reads this mapping; nothing is hardcoded.
- *Acceptance:* changing the mapping changes report results without re-pulling from Jira (see FR-D5).

### Module D — Sync / ETL engine

**FR-D1 — Scheduled sync**
- *Given* a project with a query, frequency, and field mapping configured, *when* the schedule fires, *then* the engine runs the query, ingests issues, and records a sync run.

**FR-D2 — Incremental & idempotent**
- JQL is filtered by `updated >= lastSyncTime`; upserts are keyed on the Jira **issue ID** (stable), not the issue **key** (mutable).

**FR-D3 — Change history**
- Field-change history is taken from the Jira **changelog** (not snapshot diffing) and stored append-only as `(issue_id, field, from, to, author, timestamp)`.
- For issues with long histories, the dedicated changelog endpoint is paged (the embedded changelog is truncated).

**FR-D4 — Hierarchy**
- Epic → story parent links are stored explicitly (required by gap-checks like "epic not done but all child stories done").

**FR-D5 — Raw + modeled two-layer storage**
- Each sync persists the **raw** issue payload (landing zone), from which **normalized** tables are derived.
- *Given* a changed field mapping or normalization rule, *when* re-derivation runs, *then* modeled tables rebuild from raw **without** re-pulling from Jira.

**FR-D6 — Normalization by cardinality**
- Low-cardinality categoricals (status, type, priority, user, sprint, team) go to dictionary tables referenced by FK; high-cardinality free text stays inline. (Length is a fallback proxy, not the primary rule.)

**FR-D7 — Resilience**
- Backoff + resume on Jira rate limits; partial-failure tolerant; each project surfaces last-run / next-run / error state.

### Module E — Reporting & filters

**FR-E1 — View loaded data**
- An End User can browse the issues ingested for each project.

**FR-E2 — Predefined reports (curated)**
- Story counts, average time-in-progress, throughput, status aging — all computed via the change history (D3) and the status mapping (C5).

**FR-E3 — Curated gap-checks**
- A library of correctness checks, flagship: **epics not done where all child stories are done**. "Done" resolves through the per-project mapping.

**FR-E4 — Generic filter builder** *(later phase)*
- User-composable boolean filters. Out of scope for v1.

---

## 4. Non-functional requirements

**Security**
- Tenant isolation is the highest-priority risk (FR-B2).
- Jira tokens KMS-encrypted at rest; never logged or returned in any API response.
- Passwords Argon2/bcrypt-hashed.
- Rate-limiting on login and verification-code endpoints.
- TLS everywhere.

**Privacy / data lifecycle (GDPR — EU/Poland)**
- Storing third parties' business data. Retention policy:
  - **Raw stores** (`RawIssueSnapshots`, `RawChangelog`) — retained indefinitely, never time-purged: they are the source for re-derivation, so ageing them out would destroy the ability to recompute history after a mapping change.
  - **Modeled data** (`Issues`, `IssueChangeHistory`, dictionaries, semantics, mappings) — retained indefinitely; it is the product.
  - **`SyncRun` history** — purged after `Retention:SyncRunDays` (default 90), except the newest successful incremental run per project (the sync watermark's anchor) and any run still `Running`.
- Hard delete of all tenant data on account removal: deleting a `User` cascades to every tenant-owned table, verified by the deletion-completeness gate. Erasure is immediate and irreversible; a non-identifying `DeletedAccounts` tombstone (ids + timestamp only) records that it happened.

**Reliability**
- Syncs idempotent and resumable; Jira downtime and rate limits handled gracefully; failures visible per project.

**Performance**
- Pagination on all reads; deliberate indexing on normalized + history tables (fastest-growing); report queries must not table-scan.

**Observability**
- Per-project sync status: last run, next run, error state, row counts.

**Cost / footprint**
- Single small RDS; in-process scheduler; consistent with the minimal-stack goal.

**Environment separation**
- `ASPNETCORE_ENVIRONMENT` drives config overlays; separate RDS database per environment; per-environment secret paths; CI deploys test on merge to `develop`, prod on merge to `main` behind manual approval (GitHub Environments scope prod secrets away from the test job).

---

## 5. First-cut data model

Tenant-scoped tables carry `EndUserId`; data tables also carry `JiraProjectId`.

**Identity**
- `Users` (Id, Email, Username, PasswordHash, Role, Status, CreatedAt)
- `InviteCodes` (Id, Code, CreatedByAdminId, ExpiresAt, UsedByUserId, UsedAt)
- `EmailVerifications` (Id, UserId, CodeHash, ExpiresAt, ConsumedAt)

**Jira configuration / semantic layer**
- `JiraConnections` (Id, EndUserId, BaseUrl, Email, ApiTokenEncrypted)
- `JiraProjects` (Id, EndUserId, JiraConnectionId, JiraProjectKey, SyncFrequency)
- `JiraQueries` (Id, EndUserId, JiraProjectId, Jql)
- `FieldMappings` (Id, EndUserId, JiraProjectId, Role[team/assignee/sprint/storypoints/status], JiraFieldId)
- `StatusSemantics` (Id, EndUserId, JiraProjectId, StatusId, Class[active/done/other])

**Landing zone**
- `RawIssueSnapshots` (Id, EndUserId, JiraProjectId, JiraIssueId, Payload, FetchedAt)

**Modeled data**
- `Issues` (Id, EndUserId, JiraProjectId, JiraIssueId, JiraKey, TypeId→dict, StatusId→dict, AssigneeId→dict, SprintId→dict, StoryPoints, ParentIssueId, CreatedAt, UpdatedAt)
- `IssueChangeHistory` (Id, EndUserId, JiraProjectId, JiraIssueId, Field, FromValue, ToValue, Author, ChangedAt) — append-only
- Dictionary tables: `Statuses`, `IssueTypes`, `Priorities`, `JiraUsers`, `Sprints`, `Teams`

**Sync operations**
- `SyncRuns` (Id, EndUserId, JiraProjectId, StartedAt, FinishedAt, Status, IssuesProcessed, Error, NextRunAt)

**Reporting**
- `ReportDefinitions` (curated, mostly static)
- `SavedFilters` (Id, EndUserId, Definition) — groundwork for the later generic builder

---

## 6. Suggested build phasing

1. **Foundation** — repo, solution scaffold, EF Core, local DB, CI skeleton.
2. **Identity** — seeding, invite codes, email-first registration + verification, blocking.
3. **Tenancy** — global query filter + isolation test (gate before any Jira data is stored).
4. **Connection & semantic layer** — connections, projects, queries, field/status mapping.
5. **Sync engine** — scheduler, incremental pull, raw landing, normalization, changelog, hierarchy.
6. **Reporting** — view data, predefined metrics, curated gap-checks.
7. **Hardening** — KMS, GDPR delete/retention, indexing, per-project observability.
8. *(Later)* generic filter builder; webhooks if real-time becomes a need.

---

## 7. Open items to revisit

- PostgreSQL vs SQL Server final call (PostgreSQL preferred for RLS).
- EC2-direct vs Docker/ECR deployment.
- Retention window length for ingested Jira data.
- Scale ceiling (issues per tenant) — informs indexing and whether the in-process scheduler stays sufficient.
