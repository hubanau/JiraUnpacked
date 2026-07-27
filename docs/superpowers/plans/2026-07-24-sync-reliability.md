# Phase 7 Spec 2 — Sync Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix cross-project data corruption when a tenant has multiple Jira connections, close the `AlreadyRunning` concurrency race at the database level, and remove the N+1 reads / per-row saves in the sync path.

**Architecture:** Four unique indexes gain `JiraProjectId` and their three unscoped lookups are scoped to match (index + predicate move together). A partial unique index on `SyncRuns` enforces one `Running` run per project, with a precise `23505`/constraint-name catch. Per-row `FirstOrDefaultAsync` lookups become one batched, project-scoped dictionary query each.

**Tech Stack:** ASP.NET Core 8, EF Core 8 + Npgsql/PostgreSQL, xUnit, WireMock.Net.

**Spec:** `docs/superpowers/specs/2026-07-24-sync-reliability-design.md` (commit 83f8faa).
**Branch:** `feat/sync-reliability`, cut from `develop` @ 3b7c523.

## Global Constraints

- **No `IgnoreQueryFilters`, privileged factory, `FromSql`, or `.Include(...)` in production paths.** Tests may use `IgnoreQueryFilters`/privileged raw SQL for cross-tenant seeding and assertions.
- **No new tenant table** — index changes only — so the FR-B2 gate (`tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs`) stays **untouched and green**; do not modify it or weaken any assertion anywhere.
- **Tokens never logged or stored in `SyncRun.Error`.** The failure path keeps its existing curated-message handling — do not change it.
- **The incremental watermark keeps filtering `Kind == SyncRunKind.Sync && Status == SyncRunStatus.Succeeded`** (`SyncOrchestrator.cs:47-49`). No task touches that query.
- **Two migrations, both index-only, no data migration.** Widening a unique index is strictly more permissive (uniqueness on the narrow key implies uniqueness on the wider one), so existing rows always satisfy it.
- EF migrations: `dotnet ef migrations add <Name> --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure`.
- The `jiraunpacked_test` Postgres runs in Docker container `jiraunpacked-db`. If tests cannot connect, `docker start jiraunpacked-db`, wait, re-run — a connection failure is not a code failure.

**Existing interfaces this plan builds on (verified against source):**
- `SyncOutcome { Completed, AlreadyRunning, ProjectNotFound }` and `SyncRunResult(SyncOutcome Outcome, SyncRunView? Run)` in `JiraUnpacked.Domain.Abstractions` (`ISyncOrchestrator.cs`).
- `SyncRunStatus` (`Running`/`Succeeded`/`Failed`, stored via `HasConversion<string>()`), `SyncRunKind` (`Sync`/`Rederive`).
- Both `RunProjectAsync` (`SyncOrchestrator.cs:28`) and `RunRederiveAsync` (`:126`) do: load project → `AnyAsync(Running)` pre-check → `Add(run)` → `SaveChangesAsync`.

---

## File Structure

| File | Change |
|---|---|
| `…/Configurations/RawIssueSnapshotConfiguration.cs` | widen unique index (+`JiraProjectId`) |
| `…/Configurations/RawChangelogConfiguration.cs` | widen unique index |
| `…/Configurations/IssueConfiguration.cs` | widen unique index |
| `…/Configurations/IssueChangeHistoryConfiguration.cs` | widen unique index |
| `…/Sync/SyncOrchestrator.cs` | scope + batch extract lookup; TOCTOU catch helper at both call sites |
| `…/Sync/ChangelogIngestService.cs` | scope + batch ingest lookup; hoist `SaveChanges` |
| `…/Sync/DerivationService.cs` | scope + batch issue lookup |
| `…/Configurations/SyncRunConfiguration.cs` | partial unique index (`HasFilter`) |
| two generated migrations | `AddSyncReliabilityUniqueScoping`, `AddOneRunningRunPerProject` |
| `tests/…/Integration/SyncCollisionTests.cs` | new — the centrepiece |
| `tests/…/Integration/SyncConcurrencyTests.cs` | new — TOCTOU |
| `tests/…/Integration/ChangelogIngestTests.cs` | +1 re-sync test |
| `tests/…/Tenancy/TenantDeletionTestData.cs` | `seedSyncRun` param |
| `tests/…/Integration/SyncRunRetentionTests.cs` | call with `seedSyncRun: false` |

---

## Task 1 (SR1): Cross-project collision fix — widened keys + scoped lookups

**Files:**
- Modify: the four `Configurations/*.cs` above
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` (extract lookup only)
- Modify: `src/JiraUnpacked.Infrastructure/Sync/ChangelogIngestService.cs` (ingest lookup only)
- Modify: `src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs` (issue lookup only)
- Create: migration `…AddSyncReliabilityUniqueScoping.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/SyncCollisionTests.cs`

**Interfaces:**
- Consumes: the sync API (`POST /api/connections`, `/projects`, `/queries`, `/sync`), `JiraUnpackedApiFactory.TestConnectionString` (privileged), `TestData.AuthedClientAsync`.
- Produces: the widened schema every later task depends on.

Scope this task to the **collision** only — the batching (removing the N+1) is SR3. Here you only *scope* each lookup (add the `JiraProjectId` predicate); you do **not** yet convert them to dictionary batches.

- [ ] **Step 1: Write the failing collision test**

The pre-fix corruption manifests only through the application upsert path (the app finds the wrong row and *updates* it — it never inserts a second row, so raw-SQL seeding two snapshots is impossible under the current index). The test therefore drives two real syncs through the API, each backed by its own WireMock server, both returning issue id `10001` with a distinguishable summary.

Create `tests/JiraUnpacked.Tests/Integration/SyncCollisionTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/SyncCollisionTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// A tenant with two Jira connections whose sites share issue id "10001" must end up with two
/// independent rows, not one project's data overwriting the other's. This test FAILS on the
/// pre-fix schema (the tenant-wide unique key + unscoped lookup collapse them into one row).
/// </summary>
public sealed class SyncCollisionTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jiraA = WireMockServer.Start();
    private readonly WireMockServer _jiraB = WireMockServer.Start();
    public SyncCollisionTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() { _jiraA.Stop(); _jiraB.Stop(); }

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    // Stubs one WireMock server as a Jira site whose project <key> contains a single issue
    // "10001" with the given summary, plus the connection-validation + jql-parse endpoints.
    private static void StubSite(WireMockServer jira, string key, string summary)
    {
        jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        jira.Given(Request.Create().WithPath($"/rest/api/3/project/{key}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key, name = key }));
        jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = $"project = {key}", errors = new string[0] } } }));
        jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new object[] { new { id = "10001", key = $"{key}-1",
                    fields = new { summary, created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-01T00:00:00.000+0000" },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } } }
            }));
    }

    private async Task<Guid> ConfigureAndSyncAsync(HttpClient client, WireMockServer jira, string key, string summary)
    {
        StubSite(jira, key, summary);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = key, syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = $"project = {key}" });
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/sync", new { });
        return project.Id;
    }

    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public async Task Two_connections_sharing_an_issue_id_do_not_collide()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var projectA = await ConfigureAndSyncAsync(client, _jiraA, "ACME", "Acme's 10001");
        var projectB = await ConfigureAndSyncAsync(client, _jiraB, "BETA", "Beta's 10001");

        await using var db = Privileged();

        // Two independent raw snapshots, each under its own project, each with its own payload.
        var snaps = await db.RawIssueSnapshots.IgnoreQueryFilters()
            .Where(r => r.JiraProjectId == projectA || r.JiraProjectId == projectB)
            .ToListAsync();
        Assert.Equal(2, snaps.Count);
        Assert.Contains(snaps, s => s.JiraProjectId == projectA && s.Payload.Contains("Acme's 10001"));
        Assert.Contains(snaps, s => s.JiraProjectId == projectB && s.Payload.Contains("Beta's 10001"));

        // Two independent modeled issues, each under its own project.
        var issues = await db.Issues.IgnoreQueryFilters()
            .Where(i => i.JiraProjectId == projectA || i.JiraProjectId == projectB)
            .ToListAsync();
        Assert.Equal(2, issues.Count);
        Assert.Equal("Acme's 10001", Assert.Single(issues, i => i.JiraProjectId == projectA).Summary);
        Assert.Equal("Beta's 10001", Assert.Single(issues, i => i.JiraProjectId == projectB).Summary);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncCollisionTests`
Expected: FAIL — on the current schema the second sync's extract finds project A's snapshot (matched on `EndUserId` + `JiraIssueId`) and overwrites it, so only one snapshot/issue survives and the counts are 1, not 2. (If instead you see a duplicate-key error, that also confirms the collision — but the expected pre-fix behaviour here is the silent overwrite, so the assertion `Equal(2, …)` fails.)

- [ ] **Step 3: Widen the four unique indexes**

In `RawIssueSnapshotConfiguration.cs`, change:
```csharp
        b.HasIndex(x => new { x.EndUserId, x.JiraIssueId }).IsUnique();
```
to:
```csharp
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraIssueId }).IsUnique();
```

Make the identical change in `RawChangelogConfiguration.cs`.

In `IssueConfiguration.cs`, change the same `(EndUserId, JiraIssueId)` unique index to `(EndUserId, JiraProjectId, JiraIssueId)`. Leave the other indexes in that file untouched.

In `IssueChangeHistoryConfiguration.cs`, change:
```csharp
        b.HasIndex(x => new { x.EndUserId, x.JiraIssueId, x.JiraHistoryId, x.Field }).IsUnique();
```
to:
```csharp
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraIssueId, x.JiraHistoryId, x.Field }).IsUnique();
```

- [ ] **Step 4: Scope the three lookups**

In `SyncOrchestrator.cs` (extract loop, currently `~:70`):
```csharp
                        var existing = await _db.RawIssueSnapshots
                            .FirstOrDefaultAsync(x => x.JiraProjectId == projectId && x.JiraIssueId == issue.JiraIssueId, ct);
```

In `ChangelogIngestService.cs` (`~:27`):
```csharp
            var existing = await _db.RawChangelogs
                .FirstOrDefaultAsync(x => x.JiraProjectId == projectId && x.JiraIssueId == snap.JiraIssueId, ct);
```

In `DerivationService.cs` (`~:47`):
```csharp
            var issue = await _db.Issues
                            .FirstOrDefaultAsync(i => i.JiraProjectId == projectId && i.JiraIssueId == snap.JiraIssueId, ct)
                        ?? new Issue { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = snap.JiraIssueId };
```

The change-history dedup at `DerivationService.History.cs:14` is already project-scoped — leave it.

- [ ] **Step 5: Generate the migration**

Run:
```bash
dotnet ef migrations add AddSyncReliabilityUniqueScoping \
  --project src/JiraUnpacked.Infrastructure \
  --startup-project src/JiraUnpacked.Infrastructure
```
Expected: `Up` drops the four old unique indexes and creates four widened ones — **nothing else**. Open the file and confirm; any column/table/data change means unintended drift, so stop and report. There is no data migration: widening cannot violate an existing row.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncCollisionTests` → **1 passing**.
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → `No changes…`.
Run: `dotnet test JiraUnpacked.sln` → full suite green (report the count); the FR-B2 gate and existing sync/derivation suites must stay green — they exercise single-connection tenants, which the widened key still serves.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Persistence/Configurations/RawIssueSnapshotConfiguration.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/RawChangelogConfiguration.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueConfiguration.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueChangeHistoryConfiguration.cs \
        src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs \
        src/JiraUnpacked.Infrastructure/Sync/ChangelogIngestService.cs \
        src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        tests/JiraUnpacked.Tests/Integration/SyncCollisionTests.cs
git commit -m "fix: scope sync raw/modeled keys per project to stop cross-connection collision (Phase 7, Spec 2)"
```

---

## Task 2 (SR2): `AlreadyRunning` enforced by a partial unique index

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/SyncRunConfiguration.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` (both call sites + a helper)
- Create: migration `…AddOneRunningRunPerProject.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/SyncConcurrencyTests.cs`

**Interfaces:**
- Consumes: `SyncOutcome.AlreadyRunning`, `SyncRunResult`, `SyncRunStatus.Running` (all existing).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Integration/SyncConcurrencyTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/SyncConcurrencyTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncConcurrencyTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public SyncConcurrencyTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Seeds a tenant + connection + project directly; returns (tenant, projectId).
    private async Task<(Guid tenant, Guid projectId)> SeedProjectAsync()
    {
        var tenant = await Tenancy.TenantDeletionTestData.SeedUserAsync(_factory);
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
        return (tenant, projId);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Database_rejects_a_second_running_run_for_a_project()
    {
        var (tenant, projectId) = await SeedProjectAsync();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();

        async Task InsertRunning(Guid id) => await Exec(conn,
            @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,now(),'Running','Sync',0)",
            ("i", id), ("t", tenant), ("p", projectId));

        await InsertRunning(Guid.NewGuid());   // first Running row is fine

        // The partial unique index must reject the second, with SqlState 23505.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertRunning(Guid.NewGuid()));
        Assert.Equal("23505", ex.SqlState);
    }

    [Fact]
    public async Task A_sync_is_rejected_while_a_run_is_already_in_progress()
    {
        // A racy "two simultaneous first-inserts" test is non-deterministic: Task.WhenAll does not
        // guarantee overlap, so if the calls serialize the first finishes (-> Failed) and the second
        // legitimately starts a NEW run, giving runCount == 2. The DB-level race guarantee is proven
        // deterministically by Database_rejects_a_second_running_run_for_a_project above; here we
        // prove the orchestrator's integration guard deterministically by seeding an in-progress run.
        var (tenant, projectId) = await SeedProjectAsync();
        await using (var seed = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await seed.OpenAsync();
            await Exec(seed, @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,now(),'Running','Sync',0)",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", projectId));
        }

        async Task<SyncOutcome> RunOnce()
        {
            var tenantScope = _factory.Services.GetRequiredService<ITenantScope>();
            using (tenantScope.BeginScope(tenant))
            using (var scope = _factory.Services.CreateScope())
            {
                var orch = scope.ServiceProvider.GetRequiredService<ISyncOrchestrator>();
                return (await orch.RunProjectAsync(projectId)).Outcome;
            }
        }

        var outcomes = await Task.WhenAll(RunOnce(), RunOnce());

        // With a run already in progress, every call is rejected and no new run is created.
        Assert.All(outcomes, o => Assert.Equal(SyncOutcome.AlreadyRunning, o));

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        var runCount = await db.SyncRuns.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == projectId);
        Assert.Equal(1, runCount);   // still only the seeded in-progress run
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncConcurrencyTests`
Expected: `Database_rejects_a_second_running_run_for_a_project` FAILS — no partial index yet, so the second raw insert succeeds and no `23505` is thrown. (`A_sync_is_rejected_while_a_run_is_already_in_progress` may already pass on the existing pre-check, since it seeds an in-progress run; that is fine — it guards the integration path and must stay green after the change too.)

- [ ] **Step 3: Add the partial unique index**

In `SyncRunConfiguration.cs`, add after the existing indexes:
```csharp
        // At most one Running run per project — enforces the AlreadyRunning invariant at the DB
        // level, closing the check-then-insert race. Status is stored as a string (HasConversion),
        // so the filter compares the converted value 'Running'.
        b.HasIndex(x => x.JiraProjectId)
            .HasFilter("\"Status\" = 'Running'")
            .IsUnique()
            .HasDatabaseName("IX_SyncRuns_OneRunningPerProject");
```

`HasDatabaseName` pins the constraint name the catch logic matches on, and keeps the model snapshot aware of the filtered index so drift stays clean.

- [ ] **Step 4: Add the catch helper and use it at both call sites**

In `SyncOrchestrator.cs`, add these usings if absent: `using Microsoft.EntityFrameworkCore;` (present) and `using Npgsql;`.

Add a private helper:
```csharp
    // Inserts the Running SyncRun, translating the partial-unique-index violation (lost race)
    // into AlreadyRunning. Only THIS constraint is swallowed; any other error propagates.
    private async Task<bool> TryStartRunAsync(SyncRun run, CancellationToken ct)
    {
        _db.SyncRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == "23505"
            && pg.ConstraintName == "IX_SyncRuns_OneRunningPerProject")
        {
            return false;
        }
    }
```

In `RunProjectAsync`, replace the current `_db.SyncRuns.Add(run); await _db.SaveChangesAsync(ct);` (`~:42-43`) with:
```csharp
        if (!await TryStartRunAsync(run, ct))
            return new SyncRunResult(SyncOutcome.AlreadyRunning, null);
```
Keep the existing `AnyAsync` pre-check above it — it stays as the fast path.

In `RunRederiveAsync`, make the identical replacement of its `Add` + `SaveChangesAsync` (`~:138-139`) with the same guarded call.

> After a lost race, the failed `SaveChanges` leaves the scoped `AppDbContext` unusable — but both methods `return` immediately in that branch and the request's scoped context is then disposed, so nothing reuses it.

- [ ] **Step 5: Generate the migration**

Run:
```bash
dotnet ef migrations add AddOneRunningRunPerProject \
  --project src/JiraUnpacked.Infrastructure \
  --startup-project src/JiraUnpacked.Infrastructure
```
Expected: one `CreateIndex` on `SyncRuns(JiraProjectId)` with `unique: true`, `filter: "\"Status\" = 'Running'"`, name `IX_SyncRuns_OneRunningPerProject`, and nothing else. Confirm.

- [ ] **Step 6: Run tests + drift + suite**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncConcurrencyTests` → **2 passing**.
Run: `dotnet ef migrations has-pending-model-changes …` → `No changes…`.
Run: `dotnet test JiraUnpacked.sln` → green; report the count.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Persistence/Configurations/SyncRunConfiguration.cs \
        src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        tests/JiraUnpacked.Tests/Integration/SyncConcurrencyTests.cs
git commit -m "fix: enforce one running sync per project via a partial unique index (Phase 7, Spec 2)"
```

---

## Task 3 (SR3): Batch the per-row lookups; hoist the ingest save

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` (extract loop)
- Modify: `src/JiraUnpacked.Infrastructure/Sync/ChangelogIngestService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/ChangelogIngestTests.cs` (add one)

**Interfaces:**
- Consumes: the project-scoped lookups from SR1.
- Produces: nothing later tasks depend on.

This is behaviour-preserving: the output is identical, only the number of round-trips drops. The existing sync/ingest/derivation suites are the regression net.

- [ ] **Step 1: Batch the extract lookup (keep per-page save)**

In `SyncOrchestrator.cs`, inside the `do { … } while` page loop, before `foreach (var issue in page.Issues)`:
```csharp
                    var pageIds = page.Issues.Select(i => i.JiraIssueId).ToList();
                    var existingByJiraId = await _db.RawIssueSnapshots
                        .Where(x => x.JiraProjectId == projectId && pageIds.Contains(x.JiraIssueId))
                        .ToDictionaryAsync(x => x.JiraIssueId, ct);
```
Then replace the per-issue `FirstOrDefaultAsync` with an in-memory lookup:
```csharp
                        existingByJiraId.TryGetValue(issue.JiraIssueId, out var existing);
                        if (existing is null)
                        {
                            _db.RawIssueSnapshots.Add(new RawIssueSnapshot { … });   // unchanged body
                        }
                        else { existing.Payload = issue.RawJson; existing.FetchedAt = _clock.UtcNow; }
```
Keep the `await _db.SaveChangesAsync(ct);` at the end of the page (unchanged cadence).

- [ ] **Step 2: Batch the ingest lookup and hoist the save**

Replace the body of `IngestProjectAsync` in `ChangelogIngestService.cs`:
```csharp
        var snapshots = await _db.RawIssueSnapshots.Where(r => r.JiraProjectId == projectId).ToListAsync(ct);
        var existingByJiraId = await _db.RawChangelogs
            .Where(x => x.JiraProjectId == projectId)
            .ToDictionaryAsync(x => x.JiraIssueId, ct);

        foreach (var snap in snapshots)
        {
            var histories = await AssembleHistoriesJsonAsync(snap, creds, ct);   // per-issue Jira call stays
            var payload = "{\"histories\":[" + string.Join(",", histories) + "]}";

            if (existingByJiraId.TryGetValue(snap.JiraIssueId, out var existing))
            {
                existing.Payload = payload; existing.FetchedAt = _clock.UtcNow;
            }
            else
            {
                _db.RawChangelogs.Add(new RawChangelog
                {
                    Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = snap.JiraIssueId,
                    Payload = payload, FetchedAt = _clock.UtcNow
                });
            }
        }
        await _db.SaveChangesAsync(ct);   // hoisted out of the loop — one save per project
```

- [ ] **Step 3: Batch the derivation issue lookup**

In `DerivationService.cs`, after `var snapshots = await _db.RawIssueSnapshots.Where(r => r.JiraProjectId == projectId).ToListAsync(ct);`, add:
```csharp
        var existingIssues = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .ToDictionaryAsync(i => i.JiraIssueId, ct);
```
Replace the per-snapshot lookup with:
```csharp
            existingIssues.TryGetValue(snap.JiraIssueId, out var found);
            var issue = found ?? new Issue { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = snap.JiraIssueId };
```
The `if (_db.Entry(issue).State == EntityState.Detached) _db.Issues.Add(issue);` line already handles the new-vs-existing add correctly and is unchanged.

- [ ] **Step 4: Add the re-sync test**

In `tests/JiraUnpacked.Tests/Integration/ChangelogIngestTests.cs`, add a test that ingests twice for the same project and asserts no duplicate `RawChangelog` rows (proving the hoisted save still upserts). Follow the file's existing seeding/privileged-read helpers; assert exactly one `RawChangelog` per issue after two `IngestProjectAsync` calls (or two `POST /rederive`/`/sync` cycles, matching the file's existing style).

- [ ] **Step 5: Run tests + suite**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~ChangelogIngestTests` → all passing.
Run: `dotnet test JiraUnpacked.sln` → green; the sync/derivation suites (behaviour preservation) must stay green. Report the count.
Run: `dotnet ef migrations has-pending-model-changes …` → `No changes…` (this task adds no migration).

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs \
        src/JiraUnpacked.Infrastructure/Sync/ChangelogIngestService.cs \
        src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs \
        tests/JiraUnpacked.Tests/Integration/ChangelogIngestTests.cs
git commit -m "perf: batch sync lookups per page/project and hoist the ingest save (Phase 7, Spec 2)"
```

---

## Task 4 (SR4): Decouple the retention tests from the erasure fixture

**Files:**
- Modify: `tests/JiraUnpacked.Tests/Tenancy/TenantDeletionTestData.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/SyncRunRetentionTests.cs`

**Interfaces:**
- Consumes: `SeedFullGraphAsync` (Spec 1).
- Produces: nothing.

- [ ] **Step 1: Add the opt-out parameter**

In `TenantDeletionTestData.SeedFullGraphAsync`, add a trailing parameter `bool seedSyncRun = true`, and wrap the `SyncRuns` INSERT in `if (seedSyncRun) { … }`. The default `true` keeps the erasure-completeness gate seeding all 16 tables unchanged.

- [ ] **Step 2: Opt the retention tests out**

In `SyncRunRetentionTests.cs`, change each `SeedFullGraphAsync(tenant)` call to `SeedFullGraphAsync(tenant, seedSyncRun: false)`. The tests already seed their own `SyncRuns` rows with explicit timestamps; removing the fixture's 2020 row eliminates the implicit dependency that `Assert.Equal(2, …)` relied on. **Do not change any assertion.**

- [ ] **Step 3: Run both suites**

Run: `dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~SyncRunRetentionTests|FullyQualifiedName~TenantDeletion"` → all passing (retention tests no longer see the fixture run; the erasure gate still seeds all 16 tables).
Run: `dotnet test JiraUnpacked.sln -c Release` → full suite green; report the count.
Run: `dotnet build JiraUnpacked.sln -c Release --nologo` → 0 warnings / 0 errors.

- [ ] **Step 4: Commit**

```bash
git add tests/JiraUnpacked.Tests/Tenancy/TenantDeletionTestData.cs \
        tests/JiraUnpacked.Tests/Integration/SyncRunRetentionTests.cs
git commit -m "test: decouple SyncRun retention tests from the shared erasure fixture (Phase 7, Spec 2)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green (report the count).
- [ ] `dotnet ef migrations has-pending-model-changes …` → "No changes".
- [ ] Grep the touched production files for `IgnoreQueryFilters`, `FromSql`, `.Include(`, privileged factory → no new occurrences.
- [ ] Confirm `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is unmodified (`git log develop..HEAD -- <that path>` empty) and green.
- [ ] Confirm the branch adds exactly two migrations, both index-only, and no data migration.
- [ ] Push `feat/sync-reliability`; PR → develop; CI green; squash-merge; then `develop → main` (merge-commit). Before the release, `git log --oneline origin/develop..origin/main --no-merges` — non-empty means a direct-to-main follow-up landed and must be back-merged first (release merge commits alone are expected).

## Notes for execution

- **SR1's index and predicate are one fix.** Do not split them across commits — the intermediate states are either silent corruption or a hard duplicate-key failure.
- **The collision test drives the real sync** through two WireMock servers; it cannot be reproduced by raw-SQL seeding, because the pre-fix unique index would reject the second seed row.
- **Batching (SR3) changes no output** — if a sync/derivation test's *assertions* start failing, that is a real regression in the batching, not a test to adjust.
- **Never widen the `23505` catch** to match on SqlState alone — an unrelated unique violation (including SR1's widened indexes) must not be reported as `AlreadyRunning`.
- Deferred, not in this spec: the stuck-`Running`-run reaper, the sprint take-last heuristic, the empty-embedded-changelog edge, `RemoveRange → ExecuteDelete`.
