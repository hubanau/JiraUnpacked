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

Production uses AWS RDS PostgreSQL; locally we run Postgres in Docker.

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
| `Jwt:SigningKey` | `Jwt__SigningKey` | yes | ≥32 chars, validated at startup. |
| `TokenProtection:Key` | `TokenProtection__Key` | yes | Base64 32-byte key; encrypts stored Jira API tokens. |
| `Email:Host` / `Email:FromAddress` | `Email__Host` / `Email__FromAddress` | yes | Without them the log-only development sender is selected — it never sends and writes verification codes to the log. |
| `Email:Port` / `UseStartTls` / `Username` / `Password` | | no | Default 587 with STARTTLS. |
| `Security:HstsEnabled` | `Security__HstsEnabled` | no | Default true. Turn off when TLS terminates at a proxy that forwards plain HTTP. |
| `Security:TrustProxyHeaders` | `Security__TrustProxyHeaders` | no | **Default false.** See below. |
| `Security:ForwardLimit` | `Security__ForwardLimit` | no | Proxy hops to unwind. Default 1. |

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
| Auth | `POST register · verify · verify/resend · login · refresh · logout` (anonymous), `GET/DELETE me` |
| Admin | `GET users`, `POST users/{id}/block · unblock`, `DELETE users/{id}`, `POST invite-codes` |
| Connections | CRUD, plus live Jira reads for fields, projects and statuses |
| Projects | CRUD, field mappings, status semantics, issue-type classes, queries |
| Sync | `POST sync · rederive`, `GET sync-runs` |
| Issues | `GET issues`, `GET issues/{id}`, `POST issues/query` (the filter builder) |
| Filters | `GET filter-catalog`, and saved per-project filters (CRUD) |
| Reports | four metrics and the gap-checks, each with a `GET` (whole project) and a `POST` that takes a filter |

A rejected **Jira** credential is **502**, never 401 — 401 means only that your own session
ended. Filter input never reaches a SQL string: it is compiled to an expression tree over a
closed field catalog, with values as constants and `ILIKE` wildcards escaped.

## Upgrading

**Refresh tokens moved to an httpOnly cookie.** The token is no longer in any response body and
no longer in `localStorage`. On deploy, **every signed-in user is signed out once** — their
stored token is not sent anywhere and the server never issued them a cookie. They sign in again
and it is done. `localStorage` now holds only `ju.username` and `ju.theme`, neither a credential.

## Branching & CI

- `develop` — integration branch. Every change lands via pull request with passing checks.
- `main` — release branch. `develop` is promoted with a **merge commit**, never a squash, so
  the two stay comparable.

### Deployment — Docker / ECR / ECS Fargate

`Dockerfile` builds a deployable image; `.github/workflows/deploy.yml` pushes it to ECR,
applies migrations, and rolls the ECS service. `develop` deploys to **test**, `main` to
**production**, each through a GitHub Environment so production secrets are unreadable from the
test job and an approval gate can be attached without touching the workflow.

Three things in the image are deliberate:

- **The SPA is built before `dotnet publish`.** Vite writes into the API's `wwwroot`, and
  `UseStaticFiles` no-ops on a missing directory — get the order wrong and you ship a working
  API with no UI and no error. The build fails on a missing `index.html` rather than trusting
  the ordering, and CI builds the image on every PR so that guard actually runs.
- **A self-contained EF migration bundle ships alongside the app** (`/app/migrate`). Migrations
  run as a one-off ECS task inside the VPC, so the database is never reachable from a GitHub
  runner. It is a separate binary because it needs a different database role — see below.
- **The container runs as the image's non-root `app` user** and listens on plain HTTP:8080. TLS
  terminates at the load balancer, which is why `Security__TrustProxyHeaders` must be set.

**Two database roles, not one.** The app runs as `jiraunpacked_app`, which is `NOBYPASSRLS` and
by design cannot create tables, manage policies, or install extensions. Migrations do all three
— two set up row-level security and one runs `CREATE EXTENSION pg_trgm` — so the migration task
must supply `ConnectionStrings__Default` as the **owner** role, from Secrets Manager.

**What must exist in AWS** (the workflow reads these as repository/environment variables, and
authenticates by OIDC — no long-lived keys):

| Variable | What it is |
|---|---|
| `AWS_DEPLOY_ROLE_ARN` | role the workflow assumes via OIDC |
| `AWS_REGION`, `ECR_REPOSITORY` | registry location |
| `ECS_CLUSTER`, `ECS_SERVICE`, `ECS_TASK_DEFINITION` | the running service |
| `ECS_MIGRATION_TASK_DEFINITION` | runs `/app/migrate`, owner connection string from Secrets Manager |
| `ECS_SUBNET_IDS`, `ECS_SECURITY_GROUP_IDS` | where the migration task runs |

Point the service's container health check at **`/health/ready`** — it returns 503 while the
database is unreachable, which is what makes `aws ecs wait services-stable` meaningful: a task
that cannot reach RDS never goes healthy, so a bad deploy fails the rollout instead of quietly
replacing a working one.

Images are tagged with the commit SHA and never with a moving tag, so a rollback is "point the
service at the previous SHA" rather than hoping the registry still holds what `:latest` meant.

> The image and the migration bundle are verified — both are built and exercised in CI. The
> **deploy workflow itself has not been run against a real AWS account**; it encodes the
> decisions and the ordering, and the resource names above still need creating. Tracked in
> [#80](https://github.com/hubanau/JiraUnpacked/issues/80).
>
> Until then the deploy job **skips**, guarded on `AWS_DEPLOY_ROLE_ARN` being set — a merge
> should not paint the branch red for a deploy that was never going to work. Setting that one
> variable activates the workflow with no further edit.

## Project board

Work is tracked as GitHub Issues grouped into phase milestones (see the
[Suggested build phasing](REQUIREMENTS.md#6-suggested-build-phasing) section).
The board is bootstrapped by [`bootstrap-board.sh`](bootstrap-board.sh).

> **Phase gate:** the FR-B2 tenant-isolation integration test must be green
> before any Jira data ingestion is built.
