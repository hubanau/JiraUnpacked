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
