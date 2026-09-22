# Resumable, Batched Sync — Design

**Status:** approved 2026-09-18
**Goal:** An interrupted sync costs one batch of work, not the whole run.

## Why

PLAT (92,763 issues, 1,477,699 change-history rows) has never completed a full sync. Repeated
attempts died between 2026-09-16 and 2026-09-18, the host interrupting the run each time —
Docker Desktop wedging, the laptop entering Modern Standby on battery, a container restart —
and every following attempt started again from zero.

Investigation found two distinct defects.

### 1. No memory of position

`Fetching` and `Changelog` already commit after every batch. That is why the raw snapshots
survived all six failures. What they lack is a record of *where they got to*: a restarted
run re-does identical, already-committed work. The writes were never the problem.

### 2. Derivation loads the entire project into memory

`DerivationService` performs three unbounded loads:

| Load | Site | PLAT size |
|---|---|---|
| All `RawIssueSnapshots` **including payloads** | `DeriveProjectAsync` | 92,763 issue JSONs |
| All `RawChangelogs` **including payloads** | `DeriveChangeHistoryAsync` | 92,763 changelog JSONs |
| All `IssueChangeHistory` keys into a `HashSet` | `DeriveChangeHistoryAsync` | 1,477,699 tuples |
| All `Issues` into a dictionary | `DeriveProjectAsync`, `DeriveHierarchyAsync` | 92,763 |

`ChangelogIngestService` documents avoiding exactly this ("loading the snapshot ENTITIES for
a whole project pulls every issue payload into memory at once — hundreds of megabytes").
`DerivationService` does it anyway.

Most of the long-running failures landed *in* `Deriving` — at 92,502, 92,673 and 140,750 items
— and one of those took the Docker daemon down with it while the host was on AC power, at 62%
memory, with no sleep event in the window. That is consistent with the workload destabilising
the VM, though the link is inferred from timing rather than proven. Bounding the memory is
worth doing on its own merits regardless of whether it also settles the host.

Batching fixes both defects with one change.

## Approach

Resume state lives in **new nullable columns on `SyncRuns`**, and a new run inherits the
checkpoint of the most recent unfinished run **whose `ScopeFingerprint` matches its own**.

Two approaches were rejected:

- **A new `SyncCheckpoints` table** — cleanest data model, but a new tenant-owned table
  requires both isolation layers *and* an FR-B2 gate extension. Disproportionate security
  surface for a cursor.
- **Columns on `JiraProject`** — simplest migration, but hangs per-attempt state on a
  long-lived configuration entity and muddies invalidation.

`SyncRuns` is already tenant-owned with both isolation layers, so `TenantIsolationGateTests.cs`
stays untouched. Fingerprint-matched inheritance mirrors a correctness rule the codebase
already enforces for the incremental watermark: a checkpoint, like a watermark, is only valid
for the scope that produced it.

## Schema

Three nullable columns on `SyncRuns`. Additive; no backfill. Existing rows hold `NULL` and are
simply not resumable, which is correct — they predate the mechanism.

```csharp
/// <summary>Which stage this run's checkpoint applies to, or null when there is none.</summary>
public string? ResumeStage { get; set; }

/// <summary>Opaque, stage-specific position within ResumeStage. Never user input.</summary>
public string? ResumeCursor { get; set; }

/// <summary>Items already completed within ResumeStage, so a resumed run's progress bar
/// continues rather than restarting at zero.</summary>
public int? ResumeItemsDone { get; set; }
```

## Stages and cursors

`ResumeStage` is internal and finer-grained than the displayed `Phase`. The UI keeps showing
`Counting` / `Fetching` / `Changelog` / `Deriving` exactly as today — no display change.

Stage order is fixed:

| `ResumeStage` | Batch unit | Cursor format | Iteration order |
|---|---|---|---|
| `fetch` | `CheckpointBatchSize` issues | `"{jqlIndex}\|{pageToken}"` | JQL list order, then Jira paging |
| `changelog` | 250 snapshots | last `RawIssueSnapshot.Id` | `ORDER BY "Id"` |
| `derive:issues` | `CheckpointBatchSize` snapshots | last `RawIssueSnapshot.Id` | `ORDER BY "Id"` |
| `derive:history` | `CheckpointBatchSize` changelogs | last `RawChangelog.Id` | `ORDER BY "Id"` |
| `derive:hierarchy` | `CheckpointBatchSize` issues | last `Issue.Id` | `ORDER BY "Id"` |

`Counting` is a single API call per JQL and is not checkpointed.

Resuming means: skip every stage before `ResumeStage` entirely, and re-enter `ResumeStage` at
`ResumeCursor`. A cursor of `NULL`/empty within a stage means "from the beginning of that stage".

### Why `changelog` batches at 250 rather than 10,000

Every changelog item costs a Jira round trip, so 250 issues is already several seconds of work
and `ChangelogIngestService` already commits there. Checkpointing at the existing commit point
costs nothing extra and loses **less** work on interruption than a 10,000 batch would. The
requirement is that a failure affects only one batch; a smaller batch serves that strictly
better. This is a deliberate deviation from the uniform 10,000, in the direction of the goal.

### JQL ordering must become deterministic

`ExecuteProjectAsync` currently reads queries with no `ORDER BY`:

```csharp
.Where(q => q.JiraProjectId == projectId).Select(q => q.Jql).ToListAsync(ct)
```

Postgres gives no ordering guarantee without `ORDER BY`, so `jqlIndex` would not be stable
across processes and a `fetch` cursor could resume into the wrong query. The list must be
sorted once, and that same sorted list used for both `ScopeFingerprint` and iteration.

## Commit semantics

Each batch writes, in a **single `SaveChangesAsync`**:

1. the batch's entity changes,
2. `ResumeStage`, `ResumeCursor`, `ResumeItemsDone`,
3. `PhaseProcessed`, `PhaseTotal`, `LastProgressAt`.

One transaction, so the cursor can never claim work that was not committed. This is the
property the whole design rests on: a cursor ahead of its data would silently skip issues.

On completing a stage, the same save advances `ResumeStage` to the next stage with a null
cursor. On overall success, all three resume columns are cleared.

## Resume selection

After `fingerprint` is computed in `ExecuteProjectAsync` / `ExecuteRederiveAsync`:

```csharp
var resumable = await _db.SyncRuns
    .Where(r => r.JiraProjectId == projectId
             && r.Kind == kind
             && r.ScopeFingerprint == fingerprint
             && r.ResumeStage != null
             && (r.Status == SyncRunStatus.Failed || r.Status == SyncRunStatus.Cancelled))
    .OrderByDescending(r => r.StartedAt)
    .FirstOrDefaultAsync(ct);
```

Matching on `Kind` keeps behaviour predictable: a `Sync` resumes a `Sync`, a `Rederive` resumes
a `Rederive`. A `Sync` that died in `derive:history` is therefore *not* continued by pressing
Re-derive — that starts derivation over. Derivation is the fastest stage, so the cost is small
and the rule is easy to reason about.

### Invalidation

A checkpoint is discarded, and the run starts clean, when:

- **The scope changed.** A differing `ScopeFingerprint` matches nothing. Identical to the
  existing watermark rule.
- **The raw data went away.** If `ResumeStage` is at or after `changelog` but the project holds
  no `RawIssueSnapshots`, there is nothing to resume over — retention, an erasure or a manual
  delete. Mirrors the existing belt-and-braces watermark guard.
- **Jira rejects a resumed page token.** `nextPageToken` is opaque and may expire across a long
  outage. On rejection, that one JQL restarts from its beginning. Snapshots upsert, so this
  repeats work but never duplicates rows, and the loss is bounded to a single query rather than
  the whole run.

The incremental watermark is unaffected by resumption: it derives from the most recent
**Succeeded** run, which does not change while attempts are failing, so a resumed run sends the
same effective JQL as the attempt it continues.

## Memory

Each stage holds at most one batch plus a constant. Specifically:

- `derive:issues` streams snapshots a batch at a time instead of materialising all payloads.
- `derive:history` scopes its dedup set to the batch's issue ids instead of loading all
  1,477,699 tuples.
- `derive:hierarchy` resolves parents a batch at a time.

Entities are detached after each batch, as `ChangelogIngestService` already does.

## Configuration

`SyncSchedulerOptions.CheckpointBatchSize`, default `10000`, validated greater than zero.
Applies to `fetch`, `derive:issues`, `derive:history` and `derive:hierarchy`.
`ChangelogIngestService.BatchSize` stays 250 for the reason given above.

## Testing

The centrepieces are the resume tests. For each stage: interrupt a run partway with a stub that
throws after N items, start a second run, and assert

1. it resumes at the recorded cursor,
2. the Jira stub records **no** calls for already-completed batches,
3. the final data is identical to an uninterrupted run over the same fixture.

Point 2 is what proves work is actually skipped rather than merely redone harmlessly — without
it the test passes on a design that fixes nothing.

Further tests:

- **Atomicity** — a failure during a batch save leaves neither the data nor the cursor advanced.
- **Scope change** — altering a project's JQL discards the checkpoint and pulls in full.
- **Vanished raw data** — a post-`fetch` checkpoint with no snapshots falls back to a full run.
- **Expired page token** — a rejected token restarts that JQL only, and earlier JQLs are not refetched.
- **Success clears state** — a Succeeded run leaves all three resume columns null.
- **Backward compatibility** — a run with null resume columns behaves exactly as today.
- **Memory bound** — deriving a 50,000-snapshot fixture never holds more than one batch in the
  change tracker.

## Non-goals

- No change to the progress display or `Phase` values.
- No new tenant-owned table, so the FR-B2 gate test is untouched.
- No parallelism across batches; batches stay sequential and ordered.
- `Counting` stays unbatched.
- No change to how the scheduler decides a project is due.

## Constraints preserved

- Tenant isolation unchanged: no new table, all reads through the tenant-scoped context.
- Tokens are never logged, returned, or stored in `SyncRun.Error`.
- Cursors are internally generated, never user input, and never reach a SQL string.
