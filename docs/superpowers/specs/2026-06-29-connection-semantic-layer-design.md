# Module C — Connection & semantic layer (design)

**Date:** 2026-06-29
**Build phase:** 4 (REQUIREMENTS §6)
**Requirements covered:** FR-C1 (connection hardening) · FR-C2 (projects) · FR-C3 (queries) · FR-C4 (sync frequency) · FR-C5 (field/status mapping — the keystone)
**Status:** approved

## 1. Purpose & scope

Build the configuration layer the sync engine will consume: a *validated, encrypted* Jira
connection, the projects under it, the JQL queries per project, the per-project sync
frequency, and the field/status semantic mapping. This is pure configuration plus the first
real calls to the Jira API (for credential validation and metadata discovery). No bulk issue
data is pulled in this phase.

### In scope
- Harden the existing connection: validate credentials against Jira on create; encrypt the
  API token at rest (close the current plaintext gap).
- `JiraProject`, `JiraQuery`, `FieldMapping`, `StatusSemantic` entities + CRUD.
- Jira *discovery* read endpoints (fields, available projects, project statuses) so the user
  picks mappings from real metadata.
- Config-time validation against Jira: project key exists; JQL parses.
- Extend tenant isolation (Layer 1 query filter + Layer 2 RLS) to every new table, and extend
  the FR-B2 gate test to cover a new table.

### Out of scope (later phases)
- The sync engine — scheduler, incremental issue pull, raw landing zone, normalization,
  changelog, hierarchy, `SyncRuns` (Module D / build-phase 5).
- Real AWS KMS (Phase 7 hardening — swapped in behind `ITokenProtector`).
- Reporting / gap-checks (Module E) and the generic filter builder (later).

## 2. Locked decisions (from brainstorming)

| Decision | Choice |
|---|---|
| Token encryption | `ITokenProtector` abstraction; `AesGcmTokenProtector` (AES-256-GCM) now; AWS KMS provider deferred to hardening. |
| Mapping input | Jira **discovery endpoints**; persist the user's chosen field/status IDs. |
| Jira API testing | **WireMock.Net** — exercise the full HTTP stack against canned responses. |
| Config-time validation | Validate project key existence and JQL syntax against Jira at save time (fail-fast UX). |
| Project identity | Store **both** the stable Jira project id and the (mutable) project key. |

## 3. Domain entities

All four are `ITenantOwned` — `EndUserId` is auto-stamped on insert and enforced by both
isolation layers, exactly like `JiraConnection`.

- **`JiraProject`** — `Id`, `EndUserId`, `JiraConnectionId` (FK→JiraConnection), `JiraProjectKey`,
  `JiraProjectId` (Jira's stable id), `SyncIntervalMinutes` (int, ≥ 60), `CreatedAt`.
  Unique index: `(JiraConnectionId, JiraProjectKey)`.
- **`JiraQuery`** — `Id`, `EndUserId`, `JiraProjectId` (FK), `Jql`, `CreatedAt`. One-or-more per project.
- **`FieldMapping`** — `Id`, `EndUserId`, `JiraProjectId` (FK), `Role` (enum: Team, Assignee,
  Sprint, StoryPoints, Status), `JiraFieldId`. Unique index: `(JiraProjectId, Role)`.
- **`StatusSemantic`** — `Id`, `EndUserId`, `JiraProjectId` (FK), `JiraStatusId`, `StatusName`,
  `Class` (enum: Active, Done, Other). Unique index: `(JiraProjectId, JiraStatusId)`.

Enums are stored as strings (existing convention). FKs cascade-delete from their parent
(connection → projects → queries/mappings/semantics) so tenant data stays consistent; the
hard-delete-on-account-removal requirement (Phase 7) builds on this.

## 4. Tenant isolation extension (critical — keeps the FR-B2 gate green)

Each new table repeats the established two-layer treatment:

- **Layer 1** — `modelBuilder.Entity<T>().HasQueryFilter(x => x.EndUserId == _currentTenantId)`
  in `AppDbContext.OnModelCreating`, for all four entities.
- **Layer 2** — a new migration that, for each new table, mirrors `AddTenantRls`:
  `ENABLE` + `FORCE ROW LEVEL SECURITY`, the `tenant_isolation` policy
  (`USING`/`WITH CHECK` on `"EndUserId" = NULLIF(current_setting('app.current_tenant', true), '')::uuid`),
  and `GRANT` DML to `jiraunpacked_app`.

The FR-B2 gate test (`TenantIsolationGateTests`) is **extended** to assert no cross-tenant read
through any path on at least one new table (e.g. `JiraProject`), so the gate covers the new
surface instead of going stale.

## 5. Token protection

```csharp
public interface ITokenProtector
{
    string Protect(string plaintext);    // -> base64(nonce | tag | ciphertext)
    string Unprotect(string ciphertext);
}
```

- **`AesGcmTokenProtector`** — AES-256-GCM. Fresh 96-bit random nonce per encrypt; output is
  `base64(nonce(12) | tag(16) | ciphertext)`. Key read from config `TokenProtection:Key`
  (32 bytes, base64) or secret; **fail fast at startup** if missing or not 32 bytes. Dev/CI
  key supplied via `appsettings.Development.json` / CI env (rotated out-of-band in prod).
- **`JiraConnectionService.CreateAsync`** changes to: validate credentials against Jira →
  on success `Protect(apiToken)` and store the ciphertext in the existing `ApiTokenEncrypted`
  column (now genuinely ciphertext). The token is decrypted only in-memory to call Jira; it is
  **never logged and never returned** in any response.
- Existing dev rows hold plaintext — acceptable in dev; documented, not migrated.

## 6. Jira API client

`IJiraApiClient` — a typed `HttpClient` (registered via `IHttpClientFactory`, **no** base
address; the per-connection base URL and `Basic base64(email:token)` auth header are applied
per call). Methods:

| Method | Jira endpoint | Use |
|---|---|---|
| `ValidateAsync(baseUrl, email, token)` | `GET /rest/api/3/myself` | credential check (200 valid · 401/403 invalid · else error) |
| `GetFieldsAsync(conn)` | `GET /rest/api/3/field` | field discovery |
| `GetProjectsAsync(conn)` | `GET /rest/api/3/project/search` | project discovery |
| `GetProjectAsync(conn, projectKey)` | `GET /rest/api/3/project/{key}` | validate a key on project create + capture its stable id (404 → unknown key) |
| `GetStatusesAsync(conn, projectKey)` | `GET /rest/api/3/project/{key}/statuses` | status discovery (flattened across issue types, deduped by id) |
| `ParseJqlAsync(conn, jql)` | `POST /rest/api/3/jql/parse` | JQL validation |

Each call applies a request timeout and maps Jira responses to typed results/exceptions
(401/403 → invalid creds; 404 → not found; 429/5xx → transient/upstream error). Full
backoff/retry is a Module D concern; this phase only needs timeout + error mapping.

## 7. API endpoints

All endpoints are `[Authorize]` (End User) and tenant-scoped via the current-tenant filter.

**Connections** (existing, extended)
- `POST /api/connections` — now validates against Jira *before* storing; 201 on success,
  400/401 on bad creds (nothing stored).
- `GET /api/connections/{id}/fields` — discovery (proxies `GetFieldsAsync`).
- `GET /api/connections/{id}/projects-available` — discovery (proxies `GetProjectsAsync`).

**Projects**
- `POST /api/connections/{id}/projects` `{ projectKey, syncIntervalMinutes }` — validates the
  key exists in Jira (captures the stable project id); stores.
- `GET /api/connections/{id}/projects` · `GET /api/projects/{id}`.
- `PATCH /api/projects/{id}` `{ syncIntervalMinutes }` (≥ 60).

**Queries**
- `POST /api/projects/{id}/queries` `{ jql }` — validates via `ParseJqlAsync`; stores.
- `GET /api/projects/{id}/queries` · `DELETE /api/queries/{id}`.

**Mapping (keystone)**
- `GET /api/projects/{id}/jira-statuses` — discovery (proxies `GetStatusesAsync`).
- `PUT /api/projects/{id}/field-mappings` — upsert the role→fieldId set.
- `PUT /api/projects/{id}/status-semantics` — upsert the status→class set.
- `GET` for both.

## 8. Error handling

- Invalid Jira credentials → 400/401; store nothing; never echo the token.
- Jira unreachable / 5xx / timeout → 502/504 with a safe message. Config writes that do not
  need Jira still succeed.
- Duplicate project/query/mapping → 409 (unique constraints; catch Postgres `23505`, as in the
  registration TOCTOU fix).
- Cross-tenant access → 404 (query filter + RLS; no existence leak — established pattern).
- `syncIntervalMinutes < 60` → 400 (validation).

## 9. Testing (WireMock.Net)

- New test dependency: **WireMock.Net**.
- **Jira client tests** — start a `WireMockServer`, point the connection base URL at it, and
  assert: the `Authorization: Basic base64(email:token)` header, the request path, and the
  JSON→model mapping for `myself`, `field`, `project/search`, `project/{key}/statuses`, and
  `jql/parse`; plus 401 and malformed-body cases.
- **Integration tests** (`WebApplicationFactory`) — per-test WireMock base URL; cover:
  create-connection happy + bad-creds paths; **token stored encrypted** (DB column ≠ plaintext
  and `Unprotect` round-trips); project/query/mapping CRUD; JQL-invalid → 400; the discovery
  endpoints; and a **tenant-isolation assertion on a new table**.
- **`AesGcmTokenProtector` unit tests** — round-trip; distinct nonces across encrypts; tampered
  ciphertext → failure; wrong key → failure.

## 10. Success criteria (this phase's "done")

1. A Jira token is never stored or returned in plaintext; it round-trips through `ITokenProtector`.
2. Adding a connection with bad credentials is rejected and stores nothing.
3. An End User can register validated projects, attach validated JQL, set a per-project
   frequency (≥ 60 min), and complete the field + status mapping by picking from real Jira
   metadata.
4. Every new table is tenant-isolated (Layer 1 filter + Layer 2 RLS); the FR-B2 gate test
   covers at least one new table.
5. The full test suite is green in CI (including the WireMock-backed Jira tests), and there is
   no EF migration drift.

## 11. Open items deferred (not blocking this phase)

- AWS KMS provider for `ITokenProtector` (Phase 7 hardening).
- Retention window / hard-delete-on-account-removal cascade verification (Phase 7).
- Re-validation of a stored connection's credentials on demand (could add a
  `POST /api/connections/{id}/validate` later; not required now).
