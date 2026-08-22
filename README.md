# Jira Unpacked

A self-hosted, multi-tenant Jira data warehouse with a reporting and semantic layer.
It periodically pulls issue data from users' Jira Cloud instances, stores it normalized
with full field-change history, and exposes filters and reports for gap analysis and
delivery metrics.

**The full specification is in [REQUIREMENTS.md](REQUIREMENTS.md).** Start there.

## Stack

- ASP.NET Core Web API · EF Core (Npgsql / **PostgreSQL**)
- xUnit · GitHub Actions (CI) · GitHub Issues (tickets)
- Environments: **test** + **production** (no staging for v1)

## Solution layout

| Project | Purpose |
|---|---|
| `src/JiraUnpacked.Api` | ASP.NET Core Web API — entry point |
| `src/JiraUnpacked.Domain` | Entities + interfaces, no dependencies |
| `src/JiraUnpacked.Infrastructure` | EF Core `AppDbContext`, migrations, Jira client (later) |
| `tests/JiraUnpacked.Tests` | xUnit unit + integration tests |

## Getting started

Requires the .NET 8 SDK and (for later phases) a local PostgreSQL via Docker.

```bash
dotnet build            # build the solution
dotnet test             # run the test suite
```

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

## Branching & CI

- `develop` — integration branch; CI deploys to **test** on merge.
- `main` — production; deploys to **prod** behind manual approval.
- Every change lands via pull request with passing checks.

## Project board

Work is tracked as GitHub Issues grouped into phase milestones (see the
[Suggested build phasing](REQUIREMENTS.md#6-suggested-build-phasing) section).
The board is bootstrapped by [`bootstrap-board.sh`](bootstrap-board.sh).

> **Phase gate:** the FR-B2 tenant-isolation integration test must be green
> before any Jira data ingestion is built.
