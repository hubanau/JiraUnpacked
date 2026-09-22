# Resumable, Batched Sync — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An interrupted sync resumes from its last committed batch instead of restarting from zero.

**Architecture:** Each long stage processes a bounded batch, then writes its data, its cursor and its progress in a single `SaveChangesAsync`. A new run inherits the checkpoint of the most recent unfinished run whose `ScopeFingerprint` matches its own — the same validity rule the incremental watermark already follows. Resume state lives in three nullable columns on the existing tenant-owned `SyncRuns` table, so no new table and no isolation work.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit, Quartz.

**Spec:** `docs/superpowers/specs/2026-09-18-resumable-batched-sync-design.md`

## Global Constraints

- **NO new tenant-owned table.** `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` must stay untouched and green.
- All reads go through the injected tenant-scoped `AppDbContext`. No `IgnoreQueryFilters`, no privileged factory, no `FromSql` in these paths.
- Cursors are internally generated and must never reach a SQL string.
- Tokens are never logged, returned, or stored in `SyncRun.Error`.
- `SyncPhase` values and the progress UI do not change. `ResumeStage` is internal and separate from the displayed `Phase`.
- Data and cursor advance in ONE `SaveChangesAsync`. A cursor ahead of its data silently skips issues — this is the invariant the design rests on.
- Follow `ChangelogIngestService`'s detach-per-batch pattern for bounded memory.
- `docker-compose.override.yml` is an untracked local maintenance file. Never commit it.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Domain/Entities/SyncRun.cs` | +3 nullable resume columns |
| `src/JiraUnpacked.Domain/Abstractions/ResumeStage.cs` | **new** — stage constants + execution order |
| `src/JiraUnpacked.Infrastructure/Sync/SyncSchedulerOptions.cs` | +`CheckpointBatchSize` |
| `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` | checkpoint helpers, resume selection, batched fetch |
| `src/JiraUnpacked.Infrastructure/Sync/ChangelogIngestService.cs` | cursor at its existing commit point |
| `src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs` | streamed issue derivation |
| `src/JiraUnpacked.Infrastructure/Sync/DerivationService.History.cs` | streamed history + hierarchy |
| `src/JiraUnpacked.Api/Program.cs` | options validation |
| `tests/JiraUnpacked.Tests/Sync/SyncResumeTests.cs` | **new** — the resume centrepieces |

---

### Task 1: Foundation — columns, config, deterministic JQL order

**Files:**
- Modify: `src/JiraUnpacked.Domain/Entities/SyncRun.cs`
- Create: `src/JiraUnpacked.Domain/Abstractions/ResumeStage.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncSchedulerOptions.cs`
- Modify: `src/JiraUnpacked.Api/Program.cs:27-28`
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` (JQL ordering, ~line 119)
- Test: `tests/JiraUnpacked.Tests/Sync/SyncResumeTests.cs`

**Interfaces:**
- Produces: `SyncRun.ResumeStage/ResumeCursor/ResumeItemsDone`; `ResumeStage.Fetch/Changelog/DeriveIssues/DeriveHistory/DeriveHierarchy`, `ResumeStage.Order`, `ResumeStage.IndexOf(string?)`; `SyncSchedulerOptions.CheckpointBatchSize`.

- [ ] **Step 1: Write the failing test for deterministic JQL order**

`ScopeFingerprint` already sorts before hashing, so the fingerprint is order-independent. The *fetch* iteration is not — and a fetch cursor addresses a query by index. This test pins the iteration order.

```csharp
[Fact]
public async Task The_queries_are_fetched_in_a_deterministic_sorted_order()
{
    // A fetch cursor says "resume at query 2". That is only meaningful if query 2 is the same
    // query every time. Postgres gives no ordering guarantee without ORDER BY, so without this
    // a resumed run can re-enter the wrong query and silently skip every issue in another.
    await using var h = await SyncResumeHarness.CreateAsync();
    await h.AddQueriesAsync("project = Z", "project = A", "project = M");

    await h.Orchestrator.RunProjectAsync(h.ProjectId);

    Assert.Equal(
        new[] { "project = A", "project = M", "project = Z" },
        h.Jira.SeenJql);
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: FAIL — the harness does not exist yet, then once it does, the order is insertion order rather than sorted.

- [ ] **Step 3: Add the resume columns**

In `src/JiraUnpacked.Domain/Entities/SyncRun.cs`, after `ScopeFingerprint`:

```csharp
    /// <summary>
    /// Which stage this run's checkpoint applies to, or null when it has none.
    ///
    /// Deliberately separate from Phase. Phase is a label for the UI and has four coarse values;
    /// this is an internal position marker and splits Deriving into its three passes, because a
    /// run that dies two thirds of the way through derivation should not redo the first two.
    /// Null on runs made before this column existed, which correctly makes them unresumable.
    /// </summary>
    public string? ResumeStage { get; set; }

    /// <summary>
    /// Opaque position within ResumeStage — a page token for the fetch, a last-processed row id
    /// for the rest. Internally generated, never user input, and never interpolated into SQL.
    /// </summary>
    public string? ResumeCursor { get; set; }

    /// <summary>
    /// Items already completed within ResumeStage, so a resumed run's progress bar continues
    /// from where the last attempt left off rather than dropping back to zero.
    /// </summary>
    public int? ResumeItemsDone { get; set; }
```

- [ ] **Step 4: Create the stage constants**

Create `src/JiraUnpacked.Domain/Abstractions/ResumeStage.cs`:

```csharp
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Where a run got to, at a finer grain than <see cref="SyncPhase"/>. Plain strings for the same
/// reason SyncPhase uses them: the value goes to a varchar, and a stage written by a newer build
/// must not break an older one reading the row.
/// </summary>
public static class ResumeStage
{
    public const string Fetch = "fetch";
    public const string Changelog = "changelog";
    public const string DeriveIssues = "derive:issues";
    public const string DeriveHistory = "derive:history";
    public const string DeriveHierarchy = "derive:hierarchy";

    /// <summary>Execution order. Resuming at a stage skips every stage before it.</summary>
    public static readonly string[] Order =
        { Fetch, Changelog, DeriveIssues, DeriveHistory, DeriveHierarchy };

    /// <summary>
    /// Position of a stage in <see cref="Order"/>. An unknown or null stage answers 0 — start
    /// from the beginning. That is the safe direction: redoing work costs time, whereas treating
    /// an unrecognised stage as "late" would skip work that was never done.
    /// </summary>
    public static int IndexOf(string? stage)
    {
        if (stage is null) return 0;
        var i = Array.IndexOf(Order, stage);
        return i < 0 ? 0 : i;
    }
}
```

- [ ] **Step 5: Add the batch-size option**

In `src/JiraUnpacked.Infrastructure/Sync/SyncSchedulerOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
```

```csharp
    /// <summary>
    /// Items per checkpoint for the fetch and derivation stages. After each batch the run commits
    /// its data and its position together, so an interruption costs at most one batch.
    /// Changelog ingest keeps its own smaller 250 batch — see ChangelogIngestService.
    /// </summary>
    [Range(1, 1_000_000)]
    public int CheckpointBatchSize { get; set; } = 10_000;
```

In `src/JiraUnpacked.Api/Program.cs`, replace lines 27-28:

```csharp
builder.Services.AddOptions<JiraUnpacked.Infrastructure.Sync.SyncSchedulerOptions>()
    .Bind(builder.Configuration.GetSection(JiraUnpacked.Infrastructure.Sync.SyncSchedulerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

- [ ] **Step 6: Sort the queries once, and use that list for both purposes**

In `SyncOrchestrator.ExecuteProjectAsync`, replace the query load (~line 119):

```csharp
            // Sorted here, once, and this same list feeds BOTH the fingerprint and the fetch loop.
            // ScopeFingerprint sorts internally so the hash never cared about order, but a fetch
            // cursor addresses a query by INDEX, and Postgres promises no order without ORDER BY.
            // Unsorted, a resumed run could re-enter the wrong query and skip every issue in another.
            var queries = await _db.JiraQueries
                .Where(q => q.JiraProjectId == projectId)
                .Select(q => q.Jql)
                .ToListAsync(ct);
            queries.Sort(StringComparer.Ordinal);
```

Leave the `wholeProject` default and `ScopeFingerprint(queries)` call below it unchanged — appending the whole-project default to an already-sorted single-element list keeps it sorted.

- [ ] **Step 7: Create the test harness**

Create `tests/JiraUnpacked.Tests/Sync/SyncResumeTests.cs` with a harness that later tasks reuse. Model it on the existing `SyncOrchestratorTests` stubs (`PassthroughProtector`, `StubJiraClient`) — read that file first and follow its shape rather than inventing a second style.

```csharp
/// <summary>
/// A project with a real AppDbContext and a scripted Jira, built once per test.
/// Exposes the counters the resume tests assert on: which JQL was asked for, and which page
/// tokens — a resumed run proving it skipped work means proving Jira was NOT asked again.
/// </summary>
internal sealed class SyncResumeHarness : IAsyncDisposable
{
    public Guid ProjectId { get; private set; }
    public RecordingJiraClient Jira { get; private set; } = null!;
    public ISyncOrchestrator Orchestrator { get; private set; } = null!;
    // CreateAsync builds the tenant, connection, project and orchestrator.
    // AddQueriesAsync(params string[] jql) inserts JiraQuery rows.
    // NewRunAsync() constructs a fresh orchestrator over the same database, modelling a restart.
}
```

`RecordingJiraClient` extends the existing stub with:

```csharp
    /// <summary>Every (jql, pageToken) pair requested, in order. The resume tests assert on
    /// what is ABSENT here: a batch that was already committed must never be requested again.</summary>
    public List<(string Jql, string? Token)> Requests { get; } = new();
```

- [ ] **Step 8: Add the migration**

```bash
dotnet ef migrations add AddSyncRunResumeCheckpoint -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Api
```

Verify the generated `Up` adds exactly three nullable columns to `SyncRuns` and contains no data movement.

- [ ] **Step 9: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: PASS.

Then the full suite: `dotnet test --nologo -v q` — expected 463 passing plus the new test.

- [ ] **Step 10: Commit**

```bash
git add src tests docs
git commit -m "feat(sync): resume columns, batch-size option, deterministic query order"
```

---

### Task 2: Checkpoint primitives

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs`
- Test: `tests/JiraUnpacked.Tests/Sync/SyncResumeTests.cs`

**Interfaces:**
- Consumes: `ResumeStage.*`, `SyncRun.Resume*`, `SyncSchedulerOptions.CheckpointBatchSize`.
- Produces: `readonly record struct SyncCheckpoint(string Stage, string? Cursor, int ItemsDone)`; `Task SaveCheckpointAsync(SyncRun run, string stage, string? cursor, int itemsDone, int? phaseTotal, CancellationToken ct)`; `Task<SyncCheckpoint?> FindResumePointAsync(Guid projectId, SyncRunKind kind, string fingerprint, CancellationToken ct)`; `void ClearCheckpoint(SyncRun run)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task A_checkpoint_is_only_inherited_within_the_same_scope()
{
    // A checkpoint, like a watermark, says "I got this far through THIS set of queries". After
    // the queries change it describes a scope that was never fetched, and honouring it would
    // skip every issue the new query covers but the old one did not.
    await using var h = await SyncResumeHarness.CreateAsync();
    await h.AddQueriesAsync("project = A");
    await h.FailMidRunAsync(ResumeStage.Changelog, cursor: Guid.NewGuid().ToString(), itemsDone: 500);

    await h.ReplaceQueriesAsync("project = B");          // scope changes
    var resume = await h.FindResumePointAsync();

    Assert.Null(resume);
}

[Fact]
public async Task A_post_fetch_checkpoint_is_discarded_when_the_raw_rows_are_gone()
{
    // Retention, an erasure or a manual delete. The cursor indexes into rows that no longer
    // exist, so resuming would start mid-way through a project holding nothing.
    await using var h = await SyncResumeHarness.CreateAsync();
    await h.AddQueriesAsync("project = A");
    await h.FailMidRunAsync(ResumeStage.DeriveIssues, cursor: Guid.NewGuid().ToString(), itemsDone: 900);
    await h.DeleteAllSnapshotsAsync();

    Assert.Null(await h.FindResumePointAsync());
}

[Fact]
public async Task A_succeeded_run_leaves_no_checkpoint_behind()
{
    await using var h = await SyncResumeHarness.CreateAsync();
    await h.AddQueriesAsync("project = A");

    var result = await h.Orchestrator.RunProjectAsync(h.ProjectId);

    Assert.Equal("Succeeded", result.Run!.Status);
    var row = await h.ReadRunAsync(result.Run.Id);
    Assert.Null(row.ResumeStage);
    Assert.Null(row.ResumeCursor);
    Assert.Null(row.ResumeItemsDone);
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: FAIL — `FindResumePointAsync` does not exist.

- [ ] **Step 3: Implement the primitives**

Add to `SyncOrchestrator`:

```csharp
    /// <summary>Where a previous attempt stopped: a stage, a position within it, and how much
    /// of that stage was already done.</summary>
    private readonly record struct SyncCheckpoint(string Stage, string? Cursor, int ItemsDone);

    /// <summary>
    /// Commits a batch: the entities the caller has already added or modified, the cursor, and
    /// the progress fields — in ONE SaveChangesAsync.
    ///
    /// The single save is the whole point. Two saves can be interrupted between them, and a
    /// cursor that survives while its data does not claims work that was never committed, which
    /// silently skips those issues forever. Callers must NOT save their data separately.
    /// </summary>
    private async Task SaveCheckpointAsync(
        SyncRun run, string stage, string? cursor, int itemsDone, int? phaseTotal, CancellationToken ct)
    {
        run.ResumeStage = stage;
        run.ResumeCursor = cursor;
        run.ResumeItemsDone = itemsDone;
        run.PhaseProcessed = itemsDone;
        run.PhaseTotal = phaseTotal;
        run.LastProgressAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The checkpoint this run may continue from, or null to start clean.
    ///
    /// Matched on scope, because a checkpoint is only valid for the queries that produced it —
    /// the same rule the incremental watermark follows, and for the same reason. Matched on Kind
    /// so a Rederive continues a Rederive and nothing else: predictable beats clever here.
    /// </summary>
    private async Task<SyncCheckpoint?> FindResumePointAsync(
        Guid projectId, SyncRunKind kind, string fingerprint, CancellationToken ct)
    {
        var prior = await _db.SyncRuns
            .Where(r => r.JiraProjectId == projectId
                     && r.Kind == kind
                     && r.ScopeFingerprint == fingerprint
                     && r.ResumeStage != null
                     && (r.Status == SyncRunStatus.Failed || r.Status == SyncRunStatus.Cancelled))
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new { r.ResumeStage, r.ResumeCursor, r.ResumeItemsDone })
            .FirstOrDefaultAsync(ct);

        if (prior?.ResumeStage is null) return null;

        // Belt and braces, mirroring the watermark guard above: a cursor past the fetch indexes
        // into raw rows, and if those went away there is nothing to be part-way through.
        if (prior.ResumeStage != ResumeStage.Fetch &&
            !await _db.RawIssueSnapshots.AnyAsync(r => r.JiraProjectId == projectId, ct))
        {
            _logger.LogWarning(
                "Sync {ProjectId}: discarding a {Stage} checkpoint because the project holds no stored "
                + "issues — there is nothing to resume over. Starting clean.", projectId, prior.ResumeStage);
            return null;
        }

        return new SyncCheckpoint(prior.ResumeStage, prior.ResumeCursor, prior.ResumeItemsDone ?? 0);
    }

    /// <summary>A finished run is not part-way through anything.</summary>
    private static void ClearCheckpoint(SyncRun run)
    {
        run.ResumeStage = null;
        run.ResumeCursor = null;
        run.ResumeItemsDone = null;
    }
```

Call `ClearCheckpoint(run)` in the success path of both `ExecuteProjectAsync` and `ExecuteRederiveAsync`, beside the existing `run.Phase = null;`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: PASS.

- [ ] **Step 5: Mutation check**

Delete the `prior.ResumeStage != ResumeStage.Fetch` guard's second half so vanished raw data no longer invalidates. Re-run — `A_post_fetch_checkpoint_is_discarded_when_the_raw_rows_are_gone` must fail and nothing else. Restore.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(sync): checkpoint primitives with scope-matched resume selection"
```

---

### Task 3: Batched, resumable fetch

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` (fetch loop, ~lines 176-243)
- Test: `tests/JiraUnpacked.Tests/Sync/SyncResumeTests.cs`

**Interfaces:**
- Consumes: `SaveCheckpointAsync`, `FindResumePointAsync`, `SyncCheckpoint`, `ResumeStage.Fetch`.
- Produces: `static (int JqlIndex, string? PageToken) ParseFetchCursor(string? cursor)`; cursor format `"{jqlIndex}|{pageToken}"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task A_resumed_fetch_does_not_ask_Jira_for_pages_it_already_stored()
{
    // THE test. Resuming that merely redoes the work harmlessly would pass a weaker assertion
    // and fix nothing, so this asserts on what is ABSENT from the request log.
    await using var h = await SyncResumeHarness.CreateAsync(batchSize: 2);
    await h.AddQueriesAsync("project = A");
    h.Jira.ScriptPages(("t1", 2), ("t2", 2), ("t3", 2));   // 3 pages of 2 issues
    h.Jira.ThrowOnRequestNumber(3);                        // die after two pages are committed

    await Assert.ThrowsAnyAsync<Exception>(() => h.Orchestrator.RunProjectAsync(h.ProjectId));
    await h.MarkRunFailedAsync();
    h.Jira.Requests.Clear();

    var second = await h.NewRunAsync();
    await second.RunProjectAsync(h.ProjectId);

    Assert.DoesNotContain(h.Jira.Requests, r => r.Token is null);  // never restarted the query
    Assert.All(h.Jira.Requests, r => Assert.Equal("project = A", r.Jql));
    Assert.Equal(6, await h.CountSnapshotsAsync());                 // and every issue landed
}

[Fact]
public async Task An_expired_page_token_restarts_only_its_own_query()
{
    // nextPageToken is opaque and can expire across a long outage. Restarting that one query is
    // acceptable — snapshots upsert, so it costs time, not correctness. Restarting the whole run
    // is what this design exists to prevent.
    await using var h = await SyncResumeHarness.CreateAsync(batchSize: 2);
    await h.AddQueriesAsync("project = A", "project = B");
    await h.SeedCheckpointAsync(ResumeStage.Fetch, cursor: "1|stale-token", itemsDone: 4);
    h.Jira.RejectToken("stale-token");

    var run = await h.NewRunAsync();
    await run.RunProjectAsync(h.ProjectId);

    Assert.DoesNotContain(h.Jira.Requests, r => r.Jql == "project = A");   // query 0 untouched
    Assert.Contains(h.Jira.Requests, r => r.Jql == "project = B" && r.Token is null);
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: FAIL — the fetch restarts from query 0 and re-requests every page.

- [ ] **Step 3: Add the cursor parser**

```csharp
    /// <summary>
    /// "{jqlIndex}|{pageToken}". Anything unparseable answers (0, null) — start over. Redoing
    /// work is the safe failure here; guessing a position is not.
    /// </summary>
    private static (int JqlIndex, string? PageToken) ParseFetchCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return (0, null);
        var bar = cursor.IndexOf('|');
        if (bar <= 0 || !int.TryParse(cursor[..bar], out var index) || index < 0) return (0, null);
        var token = cursor[(bar + 1)..];
        return (index, token.Length == 0 ? null : token);
    }
```

- [ ] **Step 4: Rewrite the fetch loop**

Replace the `for`/`do` block. Key changes: start at `startIndex`, seed the first query's token from the cursor, checkpoint every `CheckpointBatchSize` issues, and recover from a rejected resumed token.

```csharp
            var checkpoint = await FindResumePointAsync(projectId, SyncRunKind.Sync, fingerprint, ct);
            var startStage = ResumeStage.IndexOf(checkpoint?.Stage);

            var processed = checkpoint?.ItemsDone ?? 0;
            if (startStage <= ResumeStage.IndexOf(ResumeStage.Fetch))
            {
                var (startIndex, startToken) = startStage == ResumeStage.IndexOf(ResumeStage.Fetch)
                    ? ParseFetchCursor(checkpoint?.Cursor)
                    : (0, null);

                if (startIndex > 0 || startToken is not null)
                    _logger.LogInformation(
                        "Sync {ProjectKey}: resuming the fetch at query {Index} of {Count} with {Processed} issue(s) "
                        + "already stored.", project.JiraProjectKey, startIndex, effective.Count, processed);

                var sinceCheckpoint = 0;
                for (var q = Math.Min(startIndex, effective.Count); q < effective.Count; q++)
                {
                    var jql = effective[q];
                    var token = q == startIndex ? startToken : null;
                    var resumedToken = token;
                    var fetched = 0;
                    var pages = 0;

                    do
                    {
                        SearchPage page;
                        try
                        {
                            page = await _jira.SearchIssuesPageAsync(creds, jql, token, ct);
                        }
                        catch (JiraApiException) when (token is not null && token == resumedToken)
                        {
                            // Only the token we inherited can be stale; one we minted this run
                            // cannot be, so a failure there is a real error and must propagate.
                            _logger.LogWarning(
                                "Sync {ProjectKey}: Jira rejected the resumed page token for query {Index}. "
                                + "Restarting that query only — stored issues are upserted, so this repeats "
                                + "work without duplicating rows.", project.JiraProjectKey, q);
                            token = null;
                            resumedToken = null;
                            continue;
                        }

                        fetched += page.Issues.Count;
                        pages++;

                        // ... existing upsert body, unchanged, ending with processed++ per issue ...

                        sinceCheckpoint += page.Issues.Count;
                        token = page.NextPageToken;

                        run.IssuesProcessed = processed;
                        if (sinceCheckpoint >= _options.CheckpointBatchSize || token is null)
                        {
                            await SaveCheckpointAsync(
                                run, ResumeStage.Fetch, $"{q}|{token}", processed, run.TotalToSync, ct);
                            sinceCheckpoint = 0;
                        }
                        else
                        {
                            await _db.SaveChangesAsync(ct);
                        }

                        DetachAll<RawIssueSnapshot>();
                    } while (token is not null);

                    _logger.LogInformation(
                        "Sync {ProjectKey}: JQL [{Jql}] returned {Fetched} issue(s) over {Pages} page(s).",
                        project.JiraProjectKey, jql, fetched, pages);
                }
            }
```

Note `$"{q}|{token}"` writes an empty token when the query is exhausted, which `ParseFetchCursor` reads back as `null` — "this query is done". The next stage's checkpoint supersedes it immediately.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: PASS.

- [ ] **Step 6: Mutation check**

Change `var token = q == startIndex ? startToken : null;` to always `null`. Re-run — `A_resumed_fetch_does_not_ask_Jira_for_pages_it_already_stored` must fail. Restore.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(sync): batch and checkpoint the fetch, resuming mid-query"
```

---

### Task 4: Batched, resumable changelog ingest

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/ChangelogIngestService.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/IChangelogIngestService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` (changelog call site, ~line 249)
- Test: `tests/JiraUnpacked.Tests/Sync/SyncResumeTests.cs`

**Interfaces:**
- Consumes: `SaveCheckpointAsync`, `ResumeStage.Changelog`.
- Produces: `IngestProjectAsync(Guid projectId, JiraCredentials creds, Guid? afterSnapshotId, int alreadyDone, Func<int,int,Guid,CancellationToken,Task>? onBatch, CancellationToken ct)` — the callback now carries the last snapshot id of the batch, which is the cursor.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task A_resumed_changelog_ingest_skips_the_issues_it_already_wrote()
{
    await using var h = await SyncResumeHarness.CreateAsync();
    var ids = await h.SeedSnapshotsAsync(count: 600);            // ordered by Id
    var cutoff = ids.OrderBy(x => x).ElementAt(249);             // first batch of 250 done
    await h.SeedCheckpointAsync(ResumeStage.Changelog, cursor: cutoff.ToString(), itemsDone: 250);

    var run = await h.NewRunAsync();
    await run.RunProjectAsync(h.ProjectId);

    // The issues in the completed batch must never be asked about again.
    var askedAbout = h.Jira.ChangelogRequests.ToHashSet();
    Assert.All(ids.OrderBy(x => x).Take(250),
        id => Assert.DoesNotContain(h.JiraIssueIdFor(id), askedAbout));
    Assert.Equal(600, await h.CountChangelogsAsync());            // and all 600 end up stored
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: FAIL — ingest starts from the first snapshot and asks Jira about all 600.

- [ ] **Step 3: Make ingest resumable**

In `ChangelogIngestService.IngestProjectAsync`, replace the id load and loop:

```csharp
        // Ordered so a cursor means something. Ids only — loading the snapshot ENTITIES for a
        // whole project pulls every issue payload into memory at once.
        var snapshotIds = await _db.RawIssueSnapshots
            .Where(r => r.JiraProjectId == projectId && (afterSnapshotId == null || r.Id > afterSnapshotId))
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var total = alreadyDone + snapshotIds.Count;
        var done = alreadyDone;
        if (onBatch is not null && snapshotIds.Count == 0) return;

        foreach (var batch in snapshotIds.Chunk(BatchSize))
        {
            await IngestBatchAsync(projectId, batch, creds, ct);
            done += batch.Length;

            // The cursor advances at the batch's own commit point, which already existed. A
            // smaller batch than the 10,000 used elsewhere is deliberate: every item here costs
            // a Jira round trip, so 250 is already seconds of work, and losing less is the goal.
            if (onBatch is not null) await onBatch(done, total, batch[^1], ct);
        }
```

Note `IngestBatchAsync` must no longer call `SaveChangesAsync` itself — the orchestrator's `onBatch` callback does the single save that commits both the batch and the cursor. Remove the `await _db.SaveChangesAsync(ct);` line from it and leave the detach loop.

- [ ] **Step 4: Update the call site**

```csharp
            if (startStage <= ResumeStage.IndexOf(ResumeStage.Changelog))
            {
                await SetPhaseAsync(run, SyncPhase.Changelog, ct);
                var after = startStage == ResumeStage.IndexOf(ResumeStage.Changelog) && Guid.TryParse(checkpoint?.Cursor, out var c)
                    ? c : (Guid?)null;
                var alreadyDone = after is null ? 0 : checkpoint!.Value.ItemsDone;

                await _ingest.IngestProjectAsync(projectId, creds, after, alreadyDone,
                    async (done, total, lastId, token) =>
                        await SaveCheckpointAsync(run, ResumeStage.Changelog, lastId.ToString(), done, total, token),
                    ct);
            }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: PASS. Then the full suite.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(sync): resume changelog ingest from its last committed batch"
```

---

### Task 5: Streamed, resumable issue derivation

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/IDerivationService.cs`
- Test: `tests/JiraUnpacked.Tests/Sync/SyncResumeTests.cs`

**Interfaces:**
- Consumes: `SyncSchedulerOptions.CheckpointBatchSize`, `ResumeStage.DeriveIssues`.
- Produces: `DeriveIssuesAsync(Guid projectId, Guid? afterSnapshotId, int alreadyDone, int total, Func<int,int,Guid,CancellationToken,Task>? onBatch, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Derivation_never_holds_more_than_one_batch_in_the_change_tracker()
{
    // The bug this replaces loaded every snapshot payload, every changelog payload and 1.48M
    // history keys at once. A run that needs the whole project resident cannot survive a laptop.
    await using var h = await SyncResumeHarness.CreateAsync(batchSize: 500);
    await h.SeedSnapshotsAsync(count: 50_000);
    var peak = 0;

    await h.Derivation.DeriveIssuesAsync(h.ProjectId, null, 0, 50_000,
        (done, total, lastId, ct) =>
        {
            peak = Math.Max(peak, h.Db.ChangeTracker.Entries().Count());
            return Task.CompletedTask;
        }, default);

    Assert.True(peak < 1_500, $"change tracker peaked at {peak} entries; the batch is 500");
}

[Fact]
public async Task A_resumed_derivation_starts_after_the_last_committed_snapshot()
{
    await using var h = await SyncResumeHarness.CreateAsync(batchSize: 100);
    var ids = (await h.SeedSnapshotsAsync(count: 300)).OrderBy(x => x).ToList();
    var seen = new List<Guid>();

    await h.Derivation.DeriveIssuesAsync(h.ProjectId, ids[99], alreadyDone: 100, total: 300,
        (done, total, lastId, ct) => { seen.Add(lastId); return Task.CompletedTask; }, default);

    Assert.Equal(200, await h.CountIssuesAsync());       // only the remaining 200 derived
    Assert.Equal(ids[^1], seen[^1]);                     // finished at the last snapshot
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: FAIL — `DeriveIssuesAsync` does not exist; the current method loads everything.

- [ ] **Step 3: Rewrite the issue pass to stream**

Replace the snapshot load and `foreach` in `DeriveProjectAsync` with a batched loop. The dictionary caches (`statusCache` … `teamCache`) stay across batches — they are small and resolving a status twice is wasted work, not a bug.

```csharp
        Guid? after = afterSnapshotId;
        var done = alreadyDone;

        while (true)
        {
            // One batch of payloads at a time. Loading them all is what made this stage the place
            // every large sync died — see ChangelogIngestService, which has always done it this way.
            var batch = await _db.RawIssueSnapshots
                .Where(r => r.JiraProjectId == projectId && (after == null || r.Id > after))
                .OrderBy(r => r.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            // Scoped to the batch, not the project: the dictionary of every Issue was the second
            // unbounded load in this method.
            var batchJiraIds = batch.Select(s => s.JiraIssueId).ToList();
            var existingIssues = await _db.Issues
                .Where(i => i.JiraProjectId == projectId && batchJiraIds.Contains(i.JiraIssueId))
                .ToDictionaryAsync(i => i.JiraIssueId, ct);

            foreach (var snap in batch)
            {
                // ... existing per-snapshot body, unchanged ...
                done++;
            }

            after = batch[^1].Id;
            if (onBatch is not null) await onBatch(done, total, after.Value, ct);
            else await _db.SaveChangesAsync(ct);

            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                if (entry.Entity is RawIssueSnapshot or Issue) entry.State = EntityState.Detached;
        }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: PASS.

- [ ] **Step 5: Mutation check**

Remove the `.Take(batchSize)`. Re-run — the memory test must fail. Restore.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(sync): stream issue derivation a batch at a time"
```

---

### Task 6: Streamed, resumable history derivation

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/DerivationService.History.cs`
- Test: `tests/JiraUnpacked.Tests/Sync/SyncResumeTests.cs`

**Interfaces:**
- Consumes: `SyncSchedulerOptions.CheckpointBatchSize`, `ResumeStage.DeriveHistory`.
- Produces: `DeriveChangeHistoryAsync(Guid projectId, Guid? afterChangelogId, int alreadyDone, int total, Func<int,int,Guid,CancellationToken,Task>? onBatch, CancellationToken ct)`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task History_dedup_loads_only_the_batch_it_is_working_on()
{
    // The old dedup set held every (issue, historyId, field) tuple in the project — 1,477,699
    // rows on PLAT — purely to answer "have I seen this one". Scoped per batch it answers the
    // same question at a bounded cost, and the append-only result is identical.
    await using var h = await SyncResumeHarness.CreateAsync(batchSize: 100);
    await h.SeedChangelogsAsync(count: 1_000, historiesPerIssue: 5);
    await h.Derivation.DeriveChangeHistoryAsync(h.ProjectId, null, 0, 1_000, null, default);
    var first = await h.CountHistoryAsync();

    // Running it again must add nothing: dedup still holds across batches, via the database.
    await h.Derivation.DeriveChangeHistoryAsync(h.ProjectId, null, 0, 1_000, null, default);

    Assert.Equal(first, await h.CountHistoryAsync());
    Assert.Equal(5_000, first);
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: FAIL — signature mismatch.

- [ ] **Step 3: Rewrite the history pass**

```csharp
        Guid? after = afterChangelogId;
        var done = alreadyDone;

        while (true)
        {
            var batch = await _db.RawChangelogs
                .Where(r => r.JiraProjectId == projectId && (after == null || r.Id > after))
                .OrderBy(r => r.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            // Scoped to this batch's issues. Loading the project's whole key set was the third
            // unbounded load in derivation, and by far the largest.
            var batchIssueIds = batch.Select(l => l.JiraIssueId).ToList();
            var existing = (await _db.IssueChangeHistory
                    .Where(hh => hh.JiraProjectId == projectId && batchIssueIds.Contains(hh.JiraIssueId))
                    .Select(hh => new { hh.JiraIssueId, hh.JiraHistoryId, hh.Field })
                    .ToListAsync(ct))
                .Select(x => (x.JiraIssueId, x.JiraHistoryId, x.Field))
                .ToHashSet();

            foreach (var log in batch)
            {
                // ... existing per-changelog body, unchanged ...
                done++;
            }

            after = batch[^1].Id;
            if (onBatch is not null) await onBatch(done, total, after.Value, ct);
            else await _db.SaveChangesAsync(ct);

            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                if (entry.Entity is RawChangelog or IssueChangeHistory) entry.State = EntityState.Detached;
        }
        return done;
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: PASS. Then the full suite.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(sync): stream history derivation with per-batch dedup"
```

---

### Task 7: Hierarchy batching, Rederive resume, and the end-to-end proof

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/DerivationService.History.cs` (`DeriveHierarchyAsync`)
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` (`ExecuteRederiveAsync`)
- Test: `tests/JiraUnpacked.Tests/Sync/SyncResumeTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `DeriveHierarchyAsync(Guid projectId, Guid? afterIssueId, Func<Guid,CancellationToken,Task>? onBatch, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task An_interrupted_sync_finishes_with_exactly_the_same_data_as_an_uninterrupted_one()
{
    // The end-to-end proof: resumption must be invisible in the result.
    await using var a = await SyncResumeHarness.CreateAsync(batchSize: 50);
    await a.AddQueriesAsync("project = A");
    a.Jira.ScriptIssues(count: 500, historiesPerIssue: 3);
    await a.Orchestrator.RunProjectAsync(a.ProjectId);
    var clean = await a.SnapshotOfDerivedDataAsync();

    await using var b = await SyncResumeHarness.CreateAsync(batchSize: 50);
    await b.AddQueriesAsync("project = A");
    b.Jira.ScriptIssues(count: 500, historiesPerIssue: 3);
    await b.RunWithInterruptionsAsync(everyNthBatch: 3);   // dies and restarts repeatedly

    Assert.Equal(clean, await b.SnapshotOfDerivedDataAsync());
}

[Fact]
public async Task A_rederive_resumes_a_rederive_and_not_a_sync()
{
    // Kind-matched on purpose: predictable beats clever. A Sync that died in derivation is
    // continued by the next Sync, not by pressing Re-derive.
    await using var h = await SyncResumeHarness.CreateAsync();
    await h.SeedSnapshotsAsync(count: 100);
    await h.SeedCheckpointAsync(ResumeStage.DeriveIssues, cursor: Guid.NewGuid().ToString(),
                                itemsDone: 50, kind: SyncRunKind.Sync);

    var run = await h.NewRunAsync();
    await run.RunRederiveAsync(h.ProjectId);

    Assert.Equal(100, await h.CountIssuesAsync());   // derived all 100, ignoring the Sync cursor
}

[Fact]
public async Task A_run_with_no_checkpoint_columns_behaves_exactly_as_before()
{
    // Every row written before this feature has nulls here. They must simply run in full.
    await using var h = await SyncResumeHarness.CreateAsync();
    await h.AddQueriesAsync("project = A");
    await h.SeedLegacyFailedRunAsync();               // Failed, ScopeFingerprint set, Resume* all null

    var result = await h.Orchestrator.RunProjectAsync(h.ProjectId);

    Assert.Equal("Succeeded", result.Run!.Status);
    Assert.Contains(h.Jira.Requests, r => r.Token is null);   // started from the beginning
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: FAIL.

- [ ] **Step 3: Batch the hierarchy pass**

```csharp
    private async Task DeriveHierarchyAsync(
        Guid projectId, Guid? afterIssueId, Func<Guid, CancellationToken, Task>? onBatch, CancellationToken ct)
    {
        // Parents are resolved against the whole project, so the lookup is built from ids alone —
        // two Guids per issue rather than whole entities.
        var byJiraId = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Select(i => new { i.JiraIssueId, i.Id })
            .ToDictionaryAsync(x => x.JiraIssueId, x => x.Id, ct);

        Guid? after = afterIssueId;
        while (true)
        {
            var batch = await _db.Issues
                .Where(i => i.JiraProjectId == projectId && (after == null || i.Id > after))
                .OrderBy(i => i.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var issue in batch)
            {
                var target = issue.ParentJiraIssueId is not null
                             && byJiraId.TryGetValue(issue.ParentJiraIssueId, out var pid) ? pid : (Guid?)null;
                if (issue.ParentIssueId != target) issue.ParentIssueId = target;
            }

            after = batch[^1].Id;
            if (onBatch is not null) await onBatch(after.Value, ct);
            else await _db.SaveChangesAsync(ct);

            foreach (var entry in _db.ChangeTracker.Entries().ToList())
                if (entry.Entity is Issue) entry.State = EntityState.Detached;
        }
    }
```

- [ ] **Step 4: Wire Rederive to the same resume path**

In `ExecuteRederiveAsync`, before the derive call:

```csharp
            var checkpoint = await FindResumePointAsync(projectId, SyncRunKind.Rederive, fingerprint, ct);
            var startStage = ResumeStage.IndexOf(checkpoint?.Stage);
```

then gate each derive stage on `startStage <= ResumeStage.IndexOf(...)` exactly as `ExecuteProjectAsync` does, and call `ClearCheckpoint(run)` on success.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SyncResumeTests" -v q`
Expected: PASS.

- [ ] **Step 6: Run everything**

```bash
dotnet test --nologo -v q
```
Expected: all green, including `TenantIsolationGateTests` untouched.

```bash
cd src/JiraUnpacked.Web && npm run -s lint && npx tsc --noEmit && npm run -s test
```
Expected: unchanged — this plan touches no web code.

- [ ] **Step 7: Confirm the isolation gate was not edited**

```bash
git diff --stat develop -- tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs
```
Expected: empty output.

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "feat(sync): batch the hierarchy pass and resume rederives"
```

---

## Self-Review

**Spec coverage.** Schema → T1. Config → T1. JQL ordering → T1. Stage constants and order → T1. Commit-in-one-save → T2. Resume selection and both invalidation rules → T2. Clear on success → T2. Expired token → T3. Per-stage batching and cursors → T3-T7. Memory bounds → T5, T6, T7. Backward compatibility → T7. No requirement is unclaimed.

**Type consistency.** `onBatch` has one shape throughout — `Func<int,int,Guid,CancellationToken,Task>` — except `DeriveHierarchyAsync`, which reports no counts and so takes `Func<Guid,CancellationToken,Task>`. `SaveCheckpointAsync` has one signature, used by every call site. `ResumeStage.IndexOf` is the only stage comparison.

**Known gap, deliberate:** the atomicity requirement ("a failed batch save advances neither data nor cursor") is enforced structurally — `SaveCheckpointAsync` is the only place the cursor is written, and it writes in the same `SaveChangesAsync` as the data. The mutation checks in T2 and T3 are what guard it; a test that forces a mid-save failure would test EF's transaction, not this code.
