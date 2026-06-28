# Phase 3 — Tenant Isolation Gate: Design

**Date:** 2026-06-28
**Status:** Approved (design); pending implementation plan
**Covers:** REQUIREMENTS FR-B1, FR-B2 (Module B — Tenancy) · issues #11, #12, #13, #14

## 1. Context & goal

Tenant isolation is the highest-priority risk (REQUIREMENTS §4). Each **End User is an isolated tenant**. This phase builds the isolation **mechanism** and proves it with an integration test that is a **hard gate**: per the locked rule, no Jira data ingestion (Phase 4+) is built until that test is green.

There are no tenant-scoped data tables yet (the Jira tables are Phase 4). So this phase pulls the first one — `JiraConnection` — forward in minimal form to have a real table to enforce and prove isolation on. Phase 4 then builds the connection/semantic layer on top of an already-protected table.

## 2. Locked decisions (from brainstorming)

1. **First tenant-scoped entity:** `JiraConnection`, pulled forward minimally (no KMS encryption / Jira validation yet — those are Phase 4).
2. **Both isolation layers in this slice:** the EF Core global query filter (primary, FR-B2-mandated) **and** Postgres Row-Level Security (defense-in-depth).
3. **Current tenant** is resolved from the existing `endUserId` JWT claim.
4. **Admins** are not given cross-tenant read access in v1 (they carry `endUserId = own id` and own no connections, so they see no tenant rows).

## 3. Architecture

Two enforcement layers, one source of truth for "who is the current tenant."

- **Current tenant:** a scoped `ICurrentTenant` reads the `endUserId` claim via `IHttpContextAccessor`, exposing `Guid? EndUserId`. No authenticated tenant → `null` → reads match nothing (deny by default).
- **Layer 1 — EF Core global query filter (primary):** every tenant-owned entity is filtered on `EndUserId` centrally; never hand-written `WHERE` per query.
- **Layer 2 — Postgres RLS (defense-in-depth):** the same tables enforce row visibility at the database, so a bug that bypasses the EF filter still cannot read across tenants.

## 4. Components

### 4.1 Tenant ownership
- `ITenantOwned { Guid EndUserId { get; set; } }` (Domain abstraction).
- `JiraConnection : ITenantOwned` (Domain entity): `Id (uuid)`, `EndUserId (uuid)`, `BaseUrl (string)`, `Email (string)`, `ApiTokenEncrypted (string — plain column for now)`, `CreatedAt (timestamptz)`. A migration creates the `JiraConnections` table with an index on `EndUserId`.

### 4.2 Current-tenant resolution
- `ICurrentTenant` (Domain) → `CurrentTenant` (Infrastructure/Api) backed by `IHttpContextAccessor`, parsing the `endUserId` claim. Registered scoped; `AddHttpContextAccessor()` wired in `Program.cs`.

### 4.3 EF filter + auto-stamp (`AppDbContext`)
- `AppDbContext` takes `ICurrentTenant` (in addition to options). `OnModelCreating` applies `HasQueryFilter(e => e.EndUserId == <current tenant>)` to every `ITenantOwned` type. The filter references the injected tenant through the context instance; EF parameterizes it per query.
- `SaveChangesAsync` override **auto-stamps `EndUserId`** on `Added` `ITenantOwned` entities from the current tenant. Callers never set `EndUserId`, so a tenant cannot create a row owned by another tenant. If there is no current tenant on insert of a tenant-owned entity, `SaveChanges` throws (writes require a tenant).
- The **design-time factory, migrations, and the admin seeder** construct the context with a no-op `ICurrentTenant` (returns `null`). The query filter is part of the EF model but does not affect DDL, and those paths never write tenant-owned entities, so the throw-on-missing-tenant rule does not bite them.

### 4.4 `/api/connections` endpoints (the read path to test through)
EndUser-authenticated (`[Authorize]`), so the gate proves isolation through the real HTTP read path:
- `POST /api/connections` `{ baseUrl, email, apiToken }` → creates a `JiraConnection` for the current tenant → 201 with `{ id, baseUrl, email, createdAt }` (never returns the token).
- `GET /api/connections` → lists the current tenant's connections.
- `GET /api/connections/{id}` → the connection if owned by the current tenant, else 404 (filtered out — no existence leak).

### 4.5 RLS (Layer 2)
- **Two DB roles.** Migrations and the admin seeder connect as the privileged owner role (`jiraunpacked`). The **request-time `AppDbContext` connects as a new non-superuser, `NOBYPASSRLS` login role `jiraunpacked_app`** — superusers and table owners bypass RLS otherwise. Tenant tables also get `ALTER TABLE ... FORCE ROW LEVEL SECURITY`.
- **Policy** on `JiraConnections`: `USING (end_user_id = current_setting('app.current_tenant', true)::uuid)` and the same `WITH CHECK`. `current_setting(..., true)` returns `NULL` when unset → no rows.
- **Session variable.** An EF Core **connection interceptor** issues `SET app.current_tenant = '<endUserId>'` on connection-open, reading `ICurrentTenant`. Npgsql resets connection state when the connection returns to the pool, so the GUC does not leak between requests. When there is no tenant, the GUC is not set → RLS shows nothing.
- **Provisioning.** A migration (raw SQL, idempotent `DO`-block) creates the `jiraunpacked_app` role + `GRANT`s, enables + forces RLS on `JiraConnections`, and creates the policy. It runs under the privileged role.
- **Configuration.** Two connection strings: `ConnectionStrings:Default` (privileged — migrations, seeder, design-time factory) and `ConnectionStrings:App` (the `jiraunpacked_app` role — the scoped runtime `AppDbContext`). The runtime DbContext registration uses `App`.

## 5. The FR-B2 gate test (#13)

Runs against Postgres **as the `jiraunpacked_app` (RLS-enforced) role**, so both layers are exercised. Using the `WebApplicationFactory`:
- Seed two Active End Users — tenant A and tenant B — and log each in (tokens carry their `endUserId`).
- Each creates a connection via `POST /api/connections`.
- **Isolation assertions (every read path):**
  - `GET /api/connections` as B returns only B's connection (not A's).
  - `GET /api/connections/{A.id}` as B → 404.
  - A `DbContext` scoped to tenant B returns only B's rows (Layer 1).
  - With `IgnoreQueryFilters()` (Layer 1 deliberately bypassed) while connected as `jiraunpacked_app`, B **still** cannot read A's rows — proving RLS independently (Layer 2).

This is the gate. It must be green before Phase 4.

## 6. CI / test harness

- The migration that provisions the `jiraunpacked_app` role runs in CI (under the privileged role) before tests.
- `EnsureDatabaseAsync` migrates as `Default` (privileged); the `WebApplicationFactory`'s runtime `AppDbContext` is overridden to the `App` (RLS-enforced) connection so the gate exercises RLS in CI as well as locally.
- `docker-compose.yml` / CI Postgres need no new container — the second role is created by the migration; the `App` connection string for local/CI points at `jiraunpacked_app`.

## 7. Error handling

- Writing a tenant-owned entity with no current tenant → `InvalidOperationException` (fail closed).
- `GET /api/connections/{id}` for a non-owned/absent id → 404 (no existence leak).
- A missing `app.current_tenant` GUC → RLS returns zero rows (deny by default), never an error.

## 8. Testing

- **Unit:** `CurrentTenant` claim parsing (present / absent / malformed → `null`); auto-stamp sets `EndUserId` from the tenant; the EF model includes the query filter on `JiraConnection`.
- **Integration (the gate, §5):** the two-tenant any-read-path isolation test, run as the RLS-enforced role.
- **CI:** runs both layers via the `App` connection.

## 9. Out of scope (deferred)

- KMS envelope encryption of the API token, and validating the connection against Jira (Phase 4 / FR-C1).
- Additional tenant-scoped tables — `JiraProjects`, queries, mappings, raw/modeled data (Phase 4+); each will simply implement `ITenantOwned` and inherit both layers.
- Admin cross-tenant read access (add explicitly later if operations need it).

## 10. Build order (maps to issues)

1. **#11 — `ITenantOwned` + `JiraConnection`** entity + migration (table only).
2. **#12 — EF global query filter + auto-stamp** (`ICurrentTenant`, `AppDbContext` filter, `SaveChanges` stamping) + `/api/connections` endpoints.
3. **#14 — RLS layer**: `jiraunpacked_app` role + RLS enable/force/policy migration, connection interceptor for the GUC, the `App` connection string + runtime registration.
4. **#13 — the FR-B2 gate test** (two-tenant, any-read-path, exercising both layers as the RLS-enforced role). **The gate.**
