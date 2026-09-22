# Jira Unpacked

A self-hosted, multi-tenant Jira data warehouse with a reporting and semantic layer.
It periodically pulls issue data from users' Jira Cloud instances, stores it normalized
with full field-change history, and exposes filters and reports for gap analysis and
delivery metrics.

**The full specification is in [REQUIREMENTS.md](REQUIREMENTS.md).** Start there.

## Stack

- ASP.NET Core Web API · EF Core (Npgsql / **PostgreSQL**)
- React 19 + TypeScript + Vite + Tailwind, served same-origin from the API's `wwwroot`
- xUnit · Vitest · GitHub Actions (CI) · GitHub Issues (tickets)
- Environments: **test** + **production** (no staging for v1)

## Solution layout

| Project | Purpose |
|---|---|
| `src/JiraUnpacked.Api` | ASP.NET Core Web API — entry point; also serves the built SPA |
| `src/JiraUnpacked.Domain` | Entities + interfaces, no dependencies |
| `src/JiraUnpacked.Infrastructure` | EF Core `AppDbContext`, migrations, Jira client, sync, reporting |
| `src/JiraUnpacked.Web` | React SPA (exactly four runtime dependencies) |
| `tests/JiraUnpacked.Tests` | xUnit unit + integration tests |

## Tenant isolation

Every tenant-owned table carries `EndUserId` and is guarded twice, independently:

1. **EF global query filter** — applied centrally in `AppDbContext` to every `ITenantOwned` type.
2. **Postgres row-level security** — the app connects as `jiraunpacked_app`, a `NOBYPASSRLS`
   role, and a connection interceptor sets `app.current_tenant` on every open.

A third, related guarantee: erasure. Every `ITenantOwned` type gets an `EndUserId -> Users`
cascade, also applied centrally, so deleting a user removes their data by construction.

Three gates keep this honest and must stay green:

- `TenantIsolationGateTests` — proves isolation **at the database**, with no EF in the path.
- `TenantDeletionGateTests` — proves every tenant-owned type cascades, and pins the **count**
  of such types so a new table cannot be added without acknowledging it.
- `IssueFilterSecurityTests` — proves the user-composable filter is injection-inert.

Adding a tenant-owned table means: `ITenantOwned`, a query filter, an RLS migration, and a case
in the isolation gate. The erasure cascade you get for free.

## Getting started

Requires the .NET 8 SDK, Node 24, and Docker for the local PostgreSQL.

```bash
docker compose up -d    # local Postgres
dotnet build            # build the solution
dotnet test             # .NET suite (needs the database above)
```

The SPA lives in `src/JiraUnpacked.Web`:

```bash
npm ci && npm run dev       # dev server, proxying /api
npm run typecheck && npm run lint && npm run test -- --run
npm run build               # emits into the API's wwwroot
```

Integration tests migrate `jiraunpacked_test` themselves. They never truncate it, so a test
that seeds fixed ids would only pass once — seed fresh ids and pin only their ordering.

## Local development database

Postgres runs in Docker — the same container the deployed stack uses.

```bash
docker compose up -d          # start Postgres 16 (named volume persists data)
docker compose ps             # check health
docker compose down           # stop (add -v to also delete the data volume)
```

Dev connection string (matches `docker-compose.yml`):

```
Host=localhost;Port=5432;Database=jiraunpacked;Username=jiraunpacked;Password=localdev
```

EF Core migrations use the local `dotnet-ef` tool:

```bash
dotnet tool restore                       # once, after clone
dotnet ef database update --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Api
```

## Deployment configuration

> `appsettings.Development.json` contains **real, published secrets** — a JWT signing key, the
> Jira API-token encryption key, and an admin password. They are committed deliberately so a
> clone runs with no setup. Treat any environment that uses them as public: anyone with this
> repository can mint access tokens and decrypt every stored Jira API token.

Outside Development the app **refuses to start** with those values, or with email unconfigured
(`ProductionConfigGate`). It reports every problem at once rather than one restart at a time.

| Setting | Env var | Required outside Development | Notes |
|---|---|---|---|
| `ConnectionStrings:Default` | `ConnectionStrings__Default` | yes | Migration/owner role. |
| `ConnectionStrings:App` | `ConnectionStrings__App` | yes | The **RLS-enforced** role the app runs as. Never point this at the owner — it would bypass isolation layer 2. |
| `ConnectionStrings:Reader` | `ConnectionStrings__Reader` | yes | Read-only, `BYPASSRLS`. Used only by the background readers (due-project discovery, retention, the stuck-run reaper, the token upgrader). Startup refuses if this equals `Default`. |
| `Jwt:SigningKey` | `Jwt__SigningKey` | yes | ≥32 chars, validated at startup. |
| `TokenProtection:Key` | `TokenProtection__Key` | yes | Base64 32-byte key; encrypts stored Jira API tokens. |
| `Email:Host` / `Email:FromAddress` | `Email__Host` / `Email__FromAddress` | yes | Without them the log-only development sender is selected — it never sends and writes verification codes to the log. |
| `Email:Port` / `UseStartTls` / `Username` / `Password` | | no | Default 587 with STARTTLS. |
| `App:BaseUrl` | `App__BaseUrl` | no | Public address used in password-reset links. Defaults to `http://localhost:8080`; must match where users actually browse. Taken from configuration and **never** from the request's `Host` header — deriving it from the request is host-header injection, which turns a legitimate reset email into a token handover. |
| `Security:HstsEnabled` | `Security__HstsEnabled` | no | Default true. Turn off when TLS terminates at a proxy that forwards plain HTTP. |
| `Security:TrustProxyHeaders` | `Security__TrustProxyHeaders` | no | **Default false.** See below. |
| `Security:SecureCookiesEnabled` | `Security__SecureCookiesEnabled` | no | Default true. Off only for plain HTTP that is not on a network — see the deployment section. |
| `Security:ForwardLimit` | `Security__ForwardLimit` | no | Proxy hops to unwind. Default 1. |
| `Jira:AllowedHostSuffixes` | `Jira__AllowedHostSuffixes__0`, `__1`, … | no | Hosts a connection's base URL may name. Default `.atlassian.net` (Jira Cloud only). A leading dot requires a subdomain. Setting it **replaces** the default. |
| `Jira:AllowInsecureHttp` | `Jira__AllowInsecureHttp` | no | **Default false.** Permits `http://` base URLs — the API token would then cross the network in clear text. |

### Running behind a reverse proxy

Set `Security__TrustProxyHeaders=true` **only when every request necessarily arrives through a
trusted proxy**. It is a two-sided risk:

- Left off behind a proxy, the app sees the proxy's address on every request. The per-IP auth
  rate limiter collapses into one bucket shared by all users, and `UseHttpsRedirection` sees
  plain HTTP and redirects forever.
- Turned on while the app is directly reachable, any client can send its own `X-Forwarded-For`
  and choose which rate-limit bucket to spend.

Set `Security__ForwardLimit` to the actual number of proxies in front of the app. A larger value
lets a client's forged entry survive the unwind and become the "client" address.

### Health

`GET /health/live` — the process is up. `GET /health/ready` — the database is reachable
(**503** when not). Both anonymous, both deliberately terse.

## API surface

All routes are under `/api` and require a bearer token unless noted. Anything not under `/api`
serves the SPA shell, so client-side routing survives a deep-link refresh; an unknown `/api`
path stays a JSON 404 rather than becoming HTML with a 200.

| Area | Routes |
|---|---|
| Auth | `POST register · verify · verify/resend · login · refresh · logout · password/forgot · password/reset` (anonymous), `GET/DELETE me` |
| Admin | `GET users`, `POST users/{id}/block · unblock`, `DELETE users/{id}`, `POST invite-codes` |
| Connections | `POST`, `GET` (list and one), `PATCH {id}` (rotate the API token, optionally the email), `DELETE {id}`, plus live Jira reads for fields, projects and statuses |
| Projects | `POST connections/{id}/projects`, `GET` (per connection and one), `PATCH {id}` (`syncIntervalMinutes` and/or `syncPaused`), `DELETE {id}`; field mappings, status semantics, issue-type classes, queries |
| Sync | `POST sync · rederive`, `GET sync-runs` |
| Issues | `GET issues`, `GET issues/{id}`, `POST issues/query` (the filter builder) |
| Filters | `GET filter-catalog`, and saved per-project filters (CRUD) |
| Reports | four metrics and the gap-checks, each with a `GET` (whole project) and a `POST` that takes a filter |

**Deleting** a connection removes its projects, everything synced for them and the connection's
dictionaries; deleting a project removes its data and keeps the connection. Both are one
transaction, answer **409** while an affected project has a sync `Running` (stop it first), and
**404** for anything that is not yours. Nothing in Jira is touched.

**Rotating a token** (`PATCH connections/{id}` with `apiToken` and optional `email`) checks the new
pair against Jira *before* storing it — 400 when Jira refuses it, 502 when Jira is unreachable —
so a mistyped token never replaces a working one. The token is never returned.

**Pausing** a project (`syncPaused: true`) takes it off the schedule; **Sync now** still works.

**Jira base URLs** must be `https` on a host ending in one of `Jira:AllowedHostSuffixes` (default
`.atlassian.net`), with no user name or password in the URL; a trailing slash is dropped. The base
URL is where the server sends the user's credentials, so this is checked before any request
leaves. `Jira:AllowInsecureHttp` (default false) permits `http`; both exist for tests and
non-standard deployments, not for pointing at internal hosts.

Every authorized request re-checks the account: a **blocked or deleted** user's access token
answers **401** at once rather than working until it expires. `login` matches an input
containing `@` against emails only and anything else against usernames only; usernames are
3–32 of `A–Z a–z 0–9 . _ -`, so the two can never collide. `verify/resend` is a silent no-op
within 60 seconds of the last code.

`password/forgot` answers **202 for every address**, so it cannot be used to discover which addresses are registered; a completed reset revokes every refresh token for that account.

A rejected **Jira** credential is **502**, never 401 — 401 means only that your own session
ended. Filter input never reaches a SQL string: it is compiled to an expression tree over a
closed field catalog, with values as constants and `ILIKE` wildcards escaped.

## How a sync behaves

- **Incremental.** After a project's first successful pull, each sync asks Jira only for issues
  `updated >=` the last run's data instant, written in the **Jira account's own time zone** (JQL
  reads date literals that way) less a 5-minute skew margin. If Jira will not say which zone,
  the window widens by 14 hours instead. Changing a project's queries forces a full pull.
- **Only what changed is re-modelled.** Changelog ingest and derivation touch just the issues the
  run fetched. A changed **field mapping** therefore reaches existing issues only through
  **Re-derive**, which rebuilds every issue from the stored raw data without calling Jira.
- **Removed issues are removed.** Issues deleted or moved in Jira, or no longer matched by the
  queries, are purged: exactly on a full pull, and on incremental syncs by comparing against an
  id-only listing of the scope. An incremental sync refuses to purge more than 10% of a project
  (at least 50 issues) at once and logs a warning instead.
- **Resumable.** An interrupted run leaves a checkpoint and the next run continues from it, but
  only while no later run of that kind has succeeded. A shutdown records the run as Interrupted
  and due at once.
- **Scheduling.** A project is due one interval after its last successful sync. After a failure
  it waits 5, 10, 20, 40 minutes (capped at its interval); after a Stop, a full interval. Paused
  projects and projects of blocked accounts are not scheduled ("Sync now" still works on a paused
  project), and blocking or deleting an account stops its running syncs.

## Upgrading

**Refresh tokens moved to an httpOnly cookie.** The token is no longer in any response body and
no longer in `localStorage`. On deploy, **every signed-in user is signed out once** — their
stored token is not sent anywhere and the server never issued them a cookie. They sign in again
and it is done. `localStorage` now holds only `ju.username` and `ju.theme`, neither a credential.

## Branching & CI

- `develop` — integration branch. Every change lands via pull request with passing checks.
- `main` — release branch. `develop` is promoted with a **merge commit**, never a squash, so
  the two stay comparable.

### Deployment — local Docker

Everything runs on your machine; nothing reaches a cloud.

```bash
cp .env.example .env     # then fill in the two keys and an admin password
docker compose up -d --build
```

| | |
|---|---|
| App + SPA | http://localhost:8080 |
| Mailbox (verification codes) | http://localhost:8025 |
| Postgres | localhost:5432 |

Four services: `postgres`, a one-shot `migrate`, the `api`, and `mailpit`. The api waits on
`migrate` **completing successfully**, so a failed migration stops the stack rather than
starting the app against a half-built schema. Its health check hits `/health/ready`, which
returns 503 while the database is unreachable — "healthy" therefore means genuinely serving.

**Getting a first user in.** Registration needs an invite code, and only an Admin can mint one.
Compose seeds the admin from `.env`, so: sign in as that admin, create an invite code, register
with it, then collect the verification code from the mailbox at :8025. Codes are credentials —
Mailpit exists so they never have to be read out of a log.

**Two settings are off because this is plain HTTP**, and both must go back on behind TLS:

- `Security__HstsEnabled=false` — HSTS would pin the browser to `https` for a host that does
  not serve it.
- `Security__SecureCookiesEnabled=false` — browsers disagree about `Secure` cookies on
  `http://localhost`, so leaving it on makes sign-in work in Chrome and fail in Safari. Over a
  real network, leaving it off puts the refresh token on the wire in clear text.

**Secrets live in `.env`, which is gitignored.** The app refuses to start without a private
`Jwt__SigningKey` and `TokenProtection__Key`, and refuses the values committed in
`appsettings.Development.json` — a dev key still mints valid tokens and still decrypts stored
Jira tokens, so the failure it prevents is invisible.

**The image is built and exercised in CI on every PR.** That is not incidental: the SPA is
built into `wwwroot` *before* `dotnet publish`, and `UseStaticFiles` no-ops on a missing
directory, so the wrong order produces a working API with no UI and no error. The build asserts
`index.html` exists and CI runs that assertion.

**Backups are yours to arrange.** Postgres data lives in the `jiraunpacked-pgdata` volume:

```bash
docker compose exec postgres pg_dump -U jiraunpacked jiraunpacked > backup.sql
```

**Upgrading** is `git pull && docker compose up -d --build`. Migrations run automatically, as
the owner role, before the new app starts.

## Project board

Work is tracked as GitHub Issues grouped into phase milestones (see the
[Suggested build phasing](REQUIREMENTS.md#6-suggested-build-phasing) section).
The board is bootstrapped by [`bootstrap-board.sh`](bootstrap-board.sh).

> **Phase gate:** the FR-B2 tenant-isolation integration test must be green
> before any Jira data ingestion is built.
