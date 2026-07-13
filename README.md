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

The Jira API-token encryption key (`TokenProtection:Key`, a base64-encoded 32-byte key)
ships in `appsettings.Development.json` for dev/CI only. **Non-Development environments
(test, production) MUST inject `TokenProtection__Key` as an environment variable** — the
app validates it at startup (`ValidateOnStart` in `DependencyInjection.cs`) and fails
fast if it is missing or not a 32-byte base64 value.

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
