# Module E — Reporting: Curated metrics (FR-E2) design

**Status:** Approved (2026-07-21)
**Epic:** Phase 6 / Module E (Reporting & filters)
**This spec:** Spec **2 of 3** of Module E.

- Spec 1 — View loaded data (FR-E1): **shipped** (paginated issue list + issue detail).
- **Spec 2 (this doc) — Curated metrics (FR-E2):** story counts, average time-in-progress, throughput, status aging — computed by a status-timeline engine over `IssueChangeHistory` + `StatusSemantics`.
- Spec 3 — Curated gap-checks (FR-E3): flagship "epics not done where all child stories are done."

Consumes the modeled data from Module D and the per-project semantic layer from Module C. **Read-only.** FR-E4 (generic filter builder) remains out of scope for v1.

---

## 1. Scope & non-goals

**In scope:** a pure `StatusTimelineEngine`; an `IMetricsQueryService` + four `[Authorize]`, tenant-scoped read endpoints (story counts, time-in-progress, throughput, status aging); one serving-index migration.

**Out of scope:** gap-checks (Spec 3); the generic filter builder (FR-E4); any write path; **any new tenant table**; caching; materialized/pre-aggregated metrics.

## 2. Decisions locked in brainstorming

- **Compute on the fly, no materialized tables.** Metrics are folded per request from `IssueChangeHistory` + `StatusSemantics`. This is what makes "editing a mapping changes results **without re-pulling from Jira**" true by construction — the class is never baked into stored data. A materialized interval table was rejected: it either bakes the class (breaking that constraint) or is a pure cache, and it would add a new tenant table (migration + RLS + **FR-B2 gate extension**) for scale we do not have. SQL window functions were rejected for v1 because raw SQL bypasses the EF query filter and breaks the "no `FromSql` in the reporting path" convention; noted as the scale-up path.
- **Average time-in-progress = cycle time of completed issues:** first entry into an `Active` status → first entry into a `Done` status, averaged over issues that reached both.
- **Throughput = caller-selected bucket** (`day` | `week` | `month`), counting issues by the time they *first* entered a `Done`-class status.
- **Status aging = per-issue list + age-band histogram** (both shapes).
- **Four separate curated endpoints**, not one combined summary.
- **Unmapped statuses** (a name absent from `StatusSemantics`) classify as `Other` and are excluded from Active/Done math.
- **"Story counts"** = issue counts grouped by current status (each carrying its class) with totals and summed `StoryPoints`, across **all** issues — not only `Story`-type issues.

## 3. The status-timeline engine

A pure, DB-free function — the heart of the spec, unit-testable with no database.

**Input per issue:** `JiraCreatedAt`; that issue's status transitions (`Field == "status"`, ordered by `ChangedAt`) as `(ChangedAt, FromValue, ToValue)`; and its current status name (`Issue.StatusId` → `Status.Name`).

**Segment reconstruction:**
- With ≥1 transition: the initial status is the **first transition's `FromValue`**, effective from `JiraCreatedAt`; each transition closes the open segment and opens a new one with `ToValue`; the final segment (the current status) stays open.
- With no transitions: a single open segment = the current status name, from `JiraCreatedAt`.
- Defensive: if the first transition's `FromValue` is null, skip the pre-first-transition segment and start the timeline at that transition's `ChangedAt` with its `ToValue`.

**Classification:** each segment's status **name** maps to a `StatusClass` (`Active` | `Done` | `Other`) via the project's `StatusSemantics` (`StatusName → Class`), case-insensitive; a name absent from the map → `Other`.

> `IssueChangeHistory.FromValue`/`ToValue` hold status **display names**, not `JiraStatusId`s (the derivation stores Jira's `fromString`/`toString`). Classification therefore joins on `StatusSemantic.StatusName`. This is the single point where the mapping is applied, which is precisely why a mapping edit changes every metric on the next request with no re-pull and no re-derive.

**Three derived facts per issue** feed all four metrics from one fold:
- `FirstActiveAt` — start of the first `Active` segment (null if never Active).
- `FirstDoneAt` — start of the first `Done` segment, **independent of `FirstActiveAt`** (null if never Done).
- `Current` — the last segment's status name, class, and `EnteredAt`.

> `FirstDoneAt` is deliberately *not* conditioned on `FirstActiveAt`. An issue that reached `Done` without ever passing through an `Active` status (e.g. created directly in a done status) must still count toward throughput; it is only cycle time that requires both.

**Metric derivation:**
- **Cycle time** = `FirstDoneAt − FirstActiveAt`, only for issues where **both exist and `FirstDoneAt > FirstActiveAt`**; all other issues are excluded and counted in `IssuesExcluded`.
- **Throughput** = `FirstDoneAt` bucketed by day/week/month (every issue with a `FirstDoneAt`).
- **Aging** = `now − Current.EnteredAt`, for issues where `Current.Class != Done`.
- **Story counts** = group by the issue's current status name + class, counting issues and summing `StoryPoints`. This is equivalent to grouping by `Current`, but is computed **without the engine** — see §6: the current status is read straight off `Issue.StatusId`, so `story-counts` never loads change history.

`now` comes from the existing `IClock` (`SystemClock` in production, `FakeClock` in tests), so aging is deterministic under test.

**Accepted v1 limitations (documented, not fixed):**
- A status **renamed in Jira** after its transitions were recorded classifies as `Other`, because history holds the old name while `StatusSemantics` holds the new one.
- A **re-opened** issue keeps its *first* `Done` for cycle time and throughput, while still appearing in aging if its current class is not `Done`. This is consistent with the chosen definitions.

## 4. Endpoints (all `[Authorize]`, tenant-scoped, read-only)

Under `api/projects/{projectId:guid}/metrics/…`. Each performs the Spec 1 existence check (`_db.JiraProjects.AnyAsync` over the tenant-filtered set) and returns **404** for an unknown or cross-tenant project — no existence leak.

- **`GET …/metrics/story-counts`** → `StoryCountsReport`.
- **`GET …/metrics/time-in-progress`** → `TimeInProgressReport`.
- **`GET …/metrics/throughput?bucket=&from=&to=`** → `ThroughputReport`. `bucket` ∈ {`day`, `week`, `month`}, default `week` (ISO week starting Monday, UTC boundaries); an invalid value → **400**. `from`/`to` are optional UTC dates, defaulting to the last 12 buckets ending today; both **snap outward to bucket boundaries** (a `from` mid-week starts at that week's Monday), and the range is inclusive of the bucket containing `to`. The response echoes the resolved `From`/`To`. **Empty buckets are emitted as zero** so the series is dense and chartable.
- **`GET …/metrics/status-aging?page=&pageSize=`** → `StatusAgingReport`. Population = issues whose current class ≠ `Done`. The list is ordered `AgeDays DESC, JiraKey ASC`, with Spec 1's clamps (`page` ≥ 1; `pageSize` default 50, max 100). **The histogram is computed over the whole population, not just the current page.**

## 5. DTOs (API contracts)

In `src/JiraUnpacked.Domain/Abstractions/MetricViews.cs`, mirroring Spec 1's `IssueViews.cs` and **reusing Spec 1's `PagedResult<T>`**:

```csharp
record StatusCount(string Status, string Class, int Count, decimal StoryPoints);
record StoryCountsReport(IReadOnlyList<StatusCount> ByStatus, int TotalIssues, decimal TotalStoryPoints);

record TimeInProgressReport(double? AverageDays, int CompletedIssuesConsidered, int IssuesExcluded);

record ThroughputBucket(DateTime PeriodStart, int Completed);
record ThroughputReport(string Bucket, DateTime From, DateTime To, IReadOnlyList<ThroughputBucket> Buckets);

record AgingItem(string JiraIssueId, string JiraKey, string? Summary, string CurrentStatus,
                 string Class, double AgeDays, DateTime EnteredAt);
record AgingBand(string Band, int Count);
record StatusAgingReport(PagedResult<AgingItem> Items, IReadOnlyList<AgingBand> Histogram);
```

- `AverageDays` is **nullable** — null when no issue qualifies, rather than `0`, which would be a lie. `CompletedIssuesConsidered` and `IssuesExcluded` state the population honestly (and sum to the aged population).
- Age bands are **half-open intervals in days**, so every age falls in exactly one band: `[0,1)` → `"≤1d"`, `[1,3)` → `"1–3d"`, `[3,7)` → `"3–7d"`, `[7,14)` → `"1–2w"`, `[14,∞)` → `">2w"`. All five bands are always emitted, including zero-count ones.
- `Class` is serialized as the `StatusClass` name (`Active` / `Done` / `Other`).

## 6. Read model & loading

`IMetricsQueryService` (in `Domain/Abstractions`) implemented by `MetricsQueryService` (in `Infrastructure/Reporting`, alongside `IssueQueryService`); `MetricsController` in `Api/Controllers`.

A shared private `LoadTimelinesAsync(projectId, ct)` backs the three timeline metrics and issues, all project-scoped:
1. the tenant-scoped project existence check → 404;
2. the project's `StatusSemantics` → an in-memory `Dictionary<string, StatusClass>` (small);
3. the project's issues, projected (id, key, summary, current status name, story points, `JiraCreatedAt`) — no navigation properties, using Spec 1's correlated-subquery style;
4. the project's status-history rows (`Field == "status"`), projected to `(JiraIssueId, ChangedAt, FromValue, ToValue)`.

History is then grouped by issue, folded through the engine, and aggregated.

**`story-counts` deliberately skips the history load entirely** — the current status comes straight off `Issue.StatusId`, so it is a plain issues + semantics group-by. `LoadTimelinesAsync` is also the natural seam for caching later.

## 7. Isolation

- Every read goes through the injected tenant-scoped `AppDbContext`. **No `IgnoreQueryFilters`, no privileged factory, no `FromSql`** in production code.
- All five tables touched already carry **both** isolation layers — Layer 1 query filters at `AppDbContext.cs` (`JiraProject`:46, `StatusSemantic`:49, `Status`:50, `Issue`:57, `IssueChangeHistory`:58) and Layer 2 Postgres RLS (CSL7 for the semantic layer, SS3 for the sync tables).
- **This spec adds no new tenant table — only an index — so the FR-B2 gate test is untouched and stays green as-is.**
- Unknown or cross-tenant project → 404 on every metrics route.

## 8. Index migration

`AddChangeHistoryStatusIndex`: `IssueChangeHistory(EndUserId, JiraProjectId, Field, ChangedAt)`, so the project-scoped `Field == "status"` scan is served ordered from an index rather than a table-scan (the "reports must not table-scan" NFR). EF configuration adds the matching `b.HasIndex(...)` to `IssueChangeHistoryConfiguration`. No new table, no RLS change, no gate impact; drift stays clean.

## 9. Testing

**Unit (no DB)** — the payoff of a pure engine: initial-status reconstruction from the first `FromValue`; the no-transition case; null `FromValue`; never-Active; re-opened issue; unmapped name → `Other`; deterministic under `FakeClock`.

**Integration** via `JiraUnpackedApiFactory`, seeding by raw SQL **scoped to each test's own `JiraProjectId`** (parallel tests share one Postgres and reuse fixed Jira ids), with known timestamps so each metric asserts **exact numbers**, not "greater than zero":
- story counts grouped correctly with story-point sums;
- cycle-time average over a seeded Active→Done pair, with the excluded population counted;
- throughput bucketed by day/week/month, including zero-filled empty buckets;
- aging list ordering + the histogram computed over the whole population.

**The defining test — mapping change without re-pull:** read a metric, flip a `StatusSemantic` row's `Class`, read again, assert the number changed. These tests instantiate **no WireMock at all**, which is itself the proof that FR-E2 computes with zero Jira dependency.

**Also:** cross-tenant → 404 on the metrics routes; empty project → zeros/null with **200, not 404**; invalid `bucket` → 400; no migration drift; full suite green.

## 10. Success criteria

- An End User gets story counts, average cycle time, throughput, and status aging for a project, all derived from stored change history — **no Jira calls**.
- Editing a `StatusSemantic` changes the numbers on the next request, proven by test.
- All reads tenant-scoped; the FR-B2 gate unchanged and green; no new tenant table.
- One migration (the serving index); no drift; Release build clean.

## 11. Notes / follow-ups

- Spec 3 (gap-checks) reuses this spec's `Done`-classification and the tenant-scoped read patterns, adding the epic→story hierarchy traversal.
- If metrics get slow at scale, the levers in order are: cache `LoadTimelinesAsync` per project per sync-run, then move the fold to SQL window functions (§2's rejected Approach C).
