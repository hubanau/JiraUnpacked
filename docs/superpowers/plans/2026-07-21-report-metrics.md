# Module E Spec 2 — Curated Metrics (FR-E2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Four curated, tenant-scoped metric endpoints (story counts, average cycle time, throughput, status aging) computed on the fly from `IssueChangeHistory` + `StatusSemantics`, with zero Jira calls.

**Architecture:** A pure `StatusTimelineEngine` folds each issue's ordered status transitions into three derived facts (`FirstActiveAt`, `FirstDoneAt`, `Current`). A `MetricsQueryService` loads project-scoped data through the injected tenant-filtered `AppDbContext`, runs the engine, and aggregates. Because status *names* are classified against `StatusSemantics` at query time, editing a mapping changes results on the next request with no re-pull and no re-derive.

**Tech Stack:** ASP.NET Core 8 Web API, EF Core 8 + Npgsql/PostgreSQL, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-21-report-metrics-design.md` (commit 1333345).
**Branch:** `feat/report-metrics`, cut from `develop` @ b5d308c.

## Global Constraints

Every task's requirements implicitly include these.

- **All reads go through the injected tenant-scoped `AppDbContext`.** No `IgnoreQueryFilters`, no privileged/superuser DbContext factory, no `FromSql`/raw SQL in production code. (Tests may use raw SQL for seeding only.)
- **No new tenant table.** This spec adds exactly one index. The FR-B2 tenant-isolation gate test must remain untouched and green.
- **No navigation properties** may be added to `Issue`. Dictionary names resolve via correlated-subquery projection, exactly as `IssueQueryService` does.
- Unknown or cross-tenant project → **404** via `_db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)` over the tenant-filtered set. Empty project → zeros/null with **200**, never 404.
- `now` comes from the injected `IClock` (`DateTime UtcNow { get; }`), never `DateTime.UtcNow`, in production code.
- Status classification joins on **status name**, case-insensitive (`StringComparer.OrdinalIgnoreCase`); a name absent from `StatusSemantics` classifies as `StatusClass.Other`.
- Pagination clamps match Spec 1 exactly: `page < 1 → 1`; `pageSize < 1 → 50`; `pageSize > 100 → 100`.
- Integration tests seed by raw SQL **scoped to the test's own `JiraProjectId`** (parallel tests share one Postgres and reuse fixed Jira ids), and assert **exact numbers**.
- **No WireMock anywhere in this spec's tests** — its absence is the proof that FR-E2 computes with zero Jira dependency.
- EF migrations: `dotnet ef migrations add <Name> --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure`.

**Two deliberate, documented refinements of the approved spec's DTO block** (apply these, not the spec's literal text):
1. `StatusCount.Status` and `AgingItem.CurrentStatus` are **`string?`**, not `string` — null when an issue has no status FK. This matches Spec 1's `IssueListItem.Status` and avoids a magic `"(none)"` sentinel in an API contract.
2. Age-band labels are **ASCII**: `"<=1d"`, `"1-3d"`, `"3-7d"`, `"1-2w"`, `">2w"` (the spec rendered them typographically as `≤1d`, `1–3d`, …). Identical half-open ranges; ASCII avoids source/JSON encoding pitfalls.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Domain/Abstractions/MetricViews.cs` | Metric DTOs + `ThroughputBucketSize` enum + its parser. Reuses Spec 1's `PagedResult<T>`. |
| `src/JiraUnpacked.Domain/Abstractions/IMetricsQueryService.cs` | The four query methods. |
| `src/JiraUnpacked.Infrastructure/Reporting/StatusTimelineEngine.cs` | Pure fold: transitions → `IssueTimeline`. No EF, no clock, no I/O. |
| `src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs` | Tenant-scoped loading + aggregation for all four metrics. |
| `src/JiraUnpacked.Api/Controllers/MetricsController.cs` | Four `[Authorize]` routes; parses/validates `bucket`. |
| `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueChangeHistoryConfiguration.cs` | + serving index. |
| `tests/JiraUnpacked.Tests/Reporting/StatusTimelineEngineTests.cs` | Engine unit tests — **no database**. |
| `tests/JiraUnpacked.Tests/Integration/MetricsTestData.cs` | Shared auth + raw-SQL seeding helpers for all metrics integration tests. |
| `tests/JiraUnpacked.Tests/Integration/Metrics*Tests.cs` | One file per metric + one for the cross-cutting guarantees. |

---

## Task 1 (MT1): Pure status-timeline engine + DTOs + interface

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/MetricViews.cs`
- Create: `src/JiraUnpacked.Domain/Abstractions/IMetricsQueryService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/StatusTimelineEngine.cs`
- Test: `tests/JiraUnpacked.Tests/Reporting/StatusTimelineEngineTests.cs`

**Interfaces:**
- Consumes: `PagedResult<T>` from `src/JiraUnpacked.Domain/Abstractions/IssueViews.cs` (Spec 1); `StatusClass { Active, Done, Other }` from `JiraUnpacked.Domain.Enums`.
- Produces: everything later tasks depend on — `StatusTransition`, `IssueTimeline`, `StatusTimelineEngine.Build`, all metric DTOs, `ThroughputBucketSize`, `ThroughputBucketSizes.TryParse`, `IMetricsQueryService`.

- [ ] **Step 1: Write the failing unit tests**

Create `tests/JiraUnpacked.Tests/Reporting/StatusTimelineEngineTests.cs`:

```csharp
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Reporting;

namespace JiraUnpacked.Tests.Reporting;

public sealed class StatusTimelineEngineTests
{
    private static readonly DateTime Created = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // "To Do" => Other, "In Progress" => Active, "Done" => Done.
    private static Dictionary<string, StatusClass> Semantics() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["To Do"] = StatusClass.Other,
            ["In Progress"] = StatusClass.Active,
            ["Done"] = StatusClass.Done,
        };

    private static StatusTransition T(int day, string? from, string? to) =>
        new(new DateTime(2024, 1, day, 0, 0, 0, DateTimeKind.Utc), from, to);

    [Fact]
    public void Reconstructs_initial_status_from_first_from_value()
    {
        // The pre-first-transition segment is "To Do", effective from creation.
        var t = StatusTimelineEngine.Build(Created, "In Progress",
            new[] { T(3, "To Do", "In Progress") }, Semantics());

        Assert.Equal(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), t.FirstActiveAt);
        Assert.Null(t.FirstDoneAt);
        Assert.Equal("In Progress", t.CurrentStatus);
        Assert.Equal(StatusClass.Active, t.CurrentClass);
        Assert.Equal(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), t.CurrentEnteredAt);
    }

    [Fact]
    public void No_transitions_yields_single_segment_from_creation()
    {
        var t = StatusTimelineEngine.Build(Created, "To Do",
            Array.Empty<StatusTransition>(), Semantics());

        Assert.Null(t.FirstActiveAt);
        Assert.Null(t.FirstDoneAt);
        Assert.Equal("To Do", t.CurrentStatus);
        Assert.Equal(StatusClass.Other, t.CurrentClass);
        Assert.Equal(Created, t.CurrentEnteredAt);   // entered at creation
    }

    [Fact]
    public void Null_from_value_skips_the_pre_transition_segment()
    {
        // Defensive: unknown prior status => the timeline starts at the transition itself,
        // NOT at creation.
        var t = StatusTimelineEngine.Build(Created, "In Progress",
            new[] { T(4, null, "In Progress") }, Semantics());

        Assert.Equal(new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc), t.FirstActiveAt);
        Assert.Equal(new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc), t.CurrentEnteredAt);
    }

    [Fact]
    public void Done_without_ever_being_active_still_records_first_done()
    {
        // The independence property: throughput must count this issue even though
        // it never passed through an Active status.
        var t = StatusTimelineEngine.Build(Created, "Done",
            new[] { T(5, "To Do", "Done") }, Semantics());

        Assert.Null(t.FirstActiveAt);
        Assert.Equal(new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), t.FirstDoneAt);
        Assert.Equal(StatusClass.Done, t.CurrentClass);
    }

    [Fact]
    public void Reopened_issue_keeps_the_first_done_and_the_later_current()
    {
        var t = StatusTimelineEngine.Build(Created, "In Progress", new[]
        {
            T(2, "To Do", "In Progress"),
            T(5, "In Progress", "Done"),
            T(9, "Done", "In Progress"),   // reopened
        }, Semantics());

        Assert.Equal(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), t.FirstActiveAt);
        Assert.Equal(new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), t.FirstDoneAt); // FIRST done
        Assert.Equal(StatusClass.Active, t.CurrentClass);                                  // not Done now
        Assert.Equal(new DateTime(2024, 1, 9, 0, 0, 0, DateTimeKind.Utc), t.CurrentEnteredAt);
    }

    [Fact]
    public void Unmapped_status_name_classifies_as_other()
    {
        var t = StatusTimelineEngine.Build(Created, "Blocked",
            new[] { T(3, "To Do", "Blocked") }, Semantics());

        Assert.Null(t.FirstActiveAt);
        Assert.Null(t.FirstDoneAt);
        Assert.Equal(StatusClass.Other, t.CurrentClass);
    }

    [Fact]
    public void Classification_is_case_insensitive()
    {
        var t = StatusTimelineEngine.Build(Created, "IN PROGRESS",
            new[] { T(3, "to do", "IN PROGRESS") }, Semantics());

        Assert.Equal(StatusClass.Active, t.CurrentClass);
        Assert.Equal(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), t.FirstActiveAt);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~StatusTimelineEngineTests`
Expected: **compile error** — `StatusTimelineEngine`, `StatusTransition` do not exist.

- [ ] **Step 3: Create the metric DTOs**

Create `src/JiraUnpacked.Domain/Abstractions/MetricViews.cs`:

```csharp
// src/JiraUnpacked.Domain/Abstractions/MetricViews.cs
namespace JiraUnpacked.Domain.Abstractions;

// Status is null when the issue has no status FK.
public sealed record StatusCount(string? Status, string Class, int Count, decimal StoryPoints);

public sealed record StoryCountsReport(
    IReadOnlyList<StatusCount> ByStatus, int TotalIssues, decimal TotalStoryPoints);

// AverageDays is null (not 0) when no issue qualifies, so the number is never a lie.
public sealed record TimeInProgressReport(
    double? AverageDays, int CompletedIssuesConsidered, int IssuesExcluded);

public sealed record ThroughputBucket(DateTime PeriodStart, int Completed);

public sealed record ThroughputReport(
    string Bucket, DateTime From, DateTime To, IReadOnlyList<ThroughputBucket> Buckets);

public sealed record AgingItem(
    string JiraIssueId, string JiraKey, string? Summary, string? CurrentStatus,
    string Class, double AgeDays, DateTime EnteredAt);

public sealed record AgingBand(string Band, int Count);

public sealed record StatusAgingReport(
    PagedResult<AgingItem> Items, IReadOnlyList<AgingBand> Histogram);

public enum ThroughputBucketSize { Day, Week, Month }

public static class ThroughputBucketSizes
{
    /// <summary>Parses the `bucket` query value. Null/empty defaults to Week; unknown values fail.</summary>
    public static bool TryParse(string? value, out ThroughputBucketSize size)
    {
        if (string.IsNullOrWhiteSpace(value)) { size = ThroughputBucketSize.Week; return true; }
        switch (value.Trim().ToLowerInvariant())
        {
            case "day": size = ThroughputBucketSize.Day; return true;
            case "week": size = ThroughputBucketSize.Week; return true;
            case "month": size = ThroughputBucketSize.Month; return true;
            default: size = ThroughputBucketSize.Week; return false;
        }
    }
}
```

- [ ] **Step 4: Create the service interface**

Create `src/JiraUnpacked.Domain/Abstractions/IMetricsQueryService.cs`:

```csharp
// src/JiraUnpacked.Domain/Abstractions/IMetricsQueryService.cs
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>
/// Curated FR-E2 metrics. Every method returns null when the project is unknown or
/// belongs to another tenant (=> the controller returns 404, no existence leak).
/// </summary>
public interface IMetricsQueryService
{
    Task<StoryCountsReport?> GetStoryCountsAsync(Guid projectId, CancellationToken ct = default);

    Task<TimeInProgressReport?> GetTimeInProgressAsync(Guid projectId, CancellationToken ct = default);

    Task<ThroughputReport?> GetThroughputAsync(
        Guid projectId, ThroughputBucketSize bucket, DateTime? from, DateTime? to, CancellationToken ct = default);

    Task<StatusAgingReport?> GetStatusAgingAsync(
        Guid projectId, int page, int pageSize, CancellationToken ct = default);
}
```

- [ ] **Step 5: Implement the engine**

Create `src/JiraUnpacked.Infrastructure/Reporting/StatusTimelineEngine.cs`:

```csharp
// src/JiraUnpacked.Infrastructure/Reporting/StatusTimelineEngine.cs
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>One status change: Jira's changelog carries display NAMES, not status ids.</summary>
public sealed record StatusTransition(DateTime ChangedAt, string? FromValue, string? ToValue);

/// <summary>The three derived facts every FR-E2 metric is computed from.</summary>
public sealed record IssueTimeline(
    DateTime? FirstActiveAt,
    DateTime? FirstDoneAt,
    string? CurrentStatus,
    StatusClass CurrentClass,
    DateTime CurrentEnteredAt);

/// <summary>
/// Pure fold from an issue's status history to <see cref="IssueTimeline"/>. No EF, no clock,
/// no I/O — so it is unit-testable without a database. Classification happens HERE, at query
/// time, against the caller's semantics map; that is what makes a StatusSemantics edit change
/// every metric on the next request with no Jira re-pull and no re-derive.
/// </summary>
public static class StatusTimelineEngine
{
    /// <param name="jiraCreatedAt">When the issue was created — the start of the first segment.</param>
    /// <param name="currentStatusName">Current status name (Issue.StatusId -> Status.Name); may be null.</param>
    /// <param name="transitions">Status transitions ordered by ChangedAt ascending.</param>
    /// <param name="semantics">Status name -> class. MUST use a case-insensitive comparer.</param>
    public static IssueTimeline Build(
        DateTime jiraCreatedAt,
        string? currentStatusName,
        IReadOnlyList<StatusTransition> transitions,
        IReadOnlyDictionary<string, StatusClass> semantics)
    {
        // Segment starts, chronological. A segment is (status, when it was entered).
        var starts = new List<(string? Status, DateTime Start)>();

        if (transitions.Count == 0)
        {
            // No status ever changed: one open segment holding the current status since creation.
            starts.Add((currentStatusName, jiraCreatedAt));
        }
        else
        {
            // The status before the first transition is that transition's FromValue, held since creation.
            // Defensive: a null FromValue means the prior status is unknown, so we skip that segment
            // rather than invent one.
            if (transitions[0].FromValue is not null)
                starts.Add((transitions[0].FromValue, jiraCreatedAt));

            foreach (var t in transitions)
                starts.Add((t.ToValue, t.ChangedAt));
        }

        DateTime? firstActive = null;
        DateTime? firstDone = null;
        foreach (var (status, start) in starts)
        {
            var cls = Classify(status, semantics);
            if (cls == StatusClass.Active && firstActive is null) firstActive = start;
            // FirstDoneAt is deliberately independent of FirstActiveAt: an issue that reached Done
            // without ever being Active must still count toward throughput.
            if (cls == StatusClass.Done && firstDone is null) firstDone = start;
        }

        var last = starts[^1];
        return new IssueTimeline(
            firstActive, firstDone, last.Status, Classify(last.Status, semantics), last.Start);
    }

    /// <summary>A name absent from the project's mapping classifies as Other.</summary>
    public static StatusClass Classify(string? status, IReadOnlyDictionary<string, StatusClass> semantics) =>
        status is not null && semantics.TryGetValue(status, out var cls) ? cls : StatusClass.Other;
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~StatusTimelineEngineTests`
Expected: **PASS (7 tests)**.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/MetricViews.cs \
        src/JiraUnpacked.Domain/Abstractions/IMetricsQueryService.cs \
        src/JiraUnpacked.Infrastructure/Reporting/StatusTimelineEngine.cs \
        tests/JiraUnpacked.Tests/Reporting/StatusTimelineEngineTests.cs
git commit -m "feat: pure status-timeline engine + metric DTOs (Module E, FR-E2)"
```

---

## Task 2 (MT2): Story counts endpoint + shared test fixture

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs`
- Create: `src/JiraUnpacked.Api/Controllers/MetricsController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` (register the service next to `IIssueQueryService`)
- Create: `tests/JiraUnpacked.Tests/Integration/MetricsTestData.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/MetricsStoryCountsTests.cs`

**Interfaces:**
- Consumes: `IMetricsQueryService`, all DTOs, `StatusTimelineEngine.Classify` (Task 1).
- Produces: `MetricsQueryService` with `ProjectExistsAsync` / `LoadSemanticsAsync` private helpers reused by Tasks 3–4; `MetricsController`; `MetricsTestData` seeding helpers reused by Tasks 3–5.

Story counts deliberately **never loads change history** — the current status comes straight off `Issue.StatusId`.

- [ ] **Step 1: Create the shared test fixture**

Create `tests/JiraUnpacked.Tests/Integration/MetricsTestData.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/MetricsTestData.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>Seeded project context: ids the tests need to add issues/history to.</summary>
internal sealed record SeedContext(
    Guid Tenant, Guid ConnectionId, Guid ProjectId,
    Guid ToDoStatusId, Guid InProgressStatusId, Guid DoneStatusId);

/// <summary>
/// Shared auth + raw-SQL seeding for the FR-E2 metrics tests. Seeding uses the superuser
/// connection (bypasses RLS) purely to build fixtures; every assertion reads through the API
/// as the owning tenant. NOTE: no WireMock anywhere — metrics must need zero Jira calls.
/// </summary>
internal static class MetricsTestData
{
    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    /// <summary>Creates an Active EndUser and logs in. The tenant EndUserId == user.Id.</summary>
    internal static async Task<(HttpClient client, Guid tenantId)> AuthedTenantAsync(JiraUnpackedApiFactory factory)
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User
            {
                Id = userId, Email = $"u-{unique}@x.com", Username = $"u-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var c = factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return (c, userId);
    }

    internal static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static NpgsqlConnection Conn() => new(JiraUnpackedApiFactory.TestConnectionString);

    /// <summary>
    /// Seeds connection + project + three statuses ("To Do", "In Progress", "Done") and the
    /// matching StatusSemantics (Other / Active / Done). Each call makes a FRESH project id,
    /// so parallel tests never collide.
    /// </summary>
    internal static async Task<SeedContext> SeedProjectAsync(Guid tenant)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var todo = Guid.NewGuid();
        var wip = Guid.NewGuid();
        var done = Guid.NewGuid();

        await using var conn = Conn();
        await conn.OpenAsync();

        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())",
            ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())",
            ("i", projId), ("t", tenant), ("c", connId));

        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'1','To Do','new')",
            ("i", todo), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'2','In Progress','indeterminate')",
            ("i", wip), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'3','Done','done')",
            ("i", done), ("t", tenant), ("c", connId));

        // StatusSemantics.Class is stored as a string (HasConversion<string>).
        await SeedSemanticAsync(conn, tenant, projId, "1", "To Do", "Other");
        await SeedSemanticAsync(conn, tenant, projId, "2", "In Progress", "Active");
        await SeedSemanticAsync(conn, tenant, projId, "3", "Done", "Done");

        return new SeedContext(tenant, connId, projId, todo, wip, done);
    }

    private static Task SeedSemanticAsync(
        NpgsqlConnection conn, Guid tenant, Guid projId, string jiraStatusId, string name, string cls) =>
        Exec(conn, @"INSERT INTO ""StatusSemantics""(""Id"",""EndUserId"",""JiraProjectId"",""JiraStatusId"",""StatusName"",""Class"") VALUES (@i,@t,@p,@sid,@n,@c)",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("sid", jiraStatusId), ("n", name), ("c", cls));

    /// <summary>Repoints a status name to a different class — the FR-E2 "mapping edit" lever.</summary>
    internal static async Task SetSemanticClassAsync(SeedContext ctx, string jiraStatusId, string cls)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"UPDATE ""StatusSemantics"" SET ""Class"" = @c WHERE ""JiraProjectId"" = @p AND ""JiraStatusId"" = @sid",
            ("c", cls), ("p", ctx.ProjectId), ("sid", jiraStatusId));
    }

    internal static async Task SeedIssueAsync(
        SeedContext ctx, string jiraIssueId, string jiraKey, string? summary,
        Guid? statusId, decimal? storyPoints, DateTime createdAt)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""StoryPoints"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,@jid,@k,@s,@st,@sp,@c,@c,now())",
            ("i", Guid.NewGuid()), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("jid", jiraIssueId),
            ("k", jiraKey), ("s", summary), ("st", statusId), ("sp", storyPoints), ("c", createdAt));
    }

    /// <summary>Seeds one status transition (Field = "status", as the derivation stores it).</summary>
    internal static async Task SeedStatusChangeAsync(
        SeedContext ctx, string jiraIssueId, string historyId, string? from, string? to, DateTime changedAt)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") VALUES (@i,@t,@p,@jid,@h,'status',@f,@to,'Alice',@at)",
            ("i", Guid.NewGuid()), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("jid", jiraIssueId),
            ("h", historyId), ("f", from), ("to", to), ("at", changedAt));
    }
}
```

- [ ] **Step 2: Write the failing story-counts test**

Create `tests/JiraUnpacked.Tests/Integration/MetricsStoryCountsTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/MetricsStoryCountsTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class MetricsStoryCountsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsStoryCountsTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record StatusCountDto(string? Status, string Class, int Count, decimal StoryPoints);
    private sealed record ReportDto(List<StatusCountDto> ByStatus, int TotalIssues, decimal TotalStoryPoints);

    private static readonly DateTime Jan1 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Groups_issues_by_current_status_with_classes_and_point_sums()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // 2 In Progress (3 + 5 points), 1 Done (2 points), 1 with no status at all.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.InProgressStatusId, 3m, Jan1);
        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "b", ctx.InProgressStatusId, 5m, Jan1);
        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "c", ctx.DoneStatusId, 2m, Jan1);
        await MetricsTestData.SeedIssueAsync(ctx, "10004", "ACME-4", "d", null, null, Jan1);

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/story-counts"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Equal(4, r!.TotalIssues);
        Assert.Equal(10m, r.TotalStoryPoints);

        var wip = Assert.Single(r.ByStatus, s => s.Status == "In Progress");
        Assert.Equal("Active", wip.Class);
        Assert.Equal(2, wip.Count);
        Assert.Equal(8m, wip.StoryPoints);

        var done = Assert.Single(r.ByStatus, s => s.Status == "Done");
        Assert.Equal("Done", done.Class);
        Assert.Equal(1, done.Count);

        // The status-less issue groups under a null status, classified Other.
        var none = Assert.Single(r.ByStatus, s => s.Status is null);
        Assert.Equal("Other", none.Class);
        Assert.Equal(1, none.Count);
        Assert.Equal(0m, none.StoryPoints);
    }

    [Fact]
    public async Task Empty_project_returns_200_with_zeros()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/story-counts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);   // 200, NOT 404

        var r = await resp.Content.ReadFromJsonAsync<ReportDto>();
        Assert.Equal(0, r!.TotalIssues);
        Assert.Equal(0m, r.TotalStoryPoints);
        Assert.Empty(r.ByStatus);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~MetricsStoryCountsTests`
Expected: FAIL — 404, because no `/metrics/story-counts` route exists yet.

- [ ] **Step 4: Implement the service (story counts only)**

Create `src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs`:

```csharp
// src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// FR-E2 curated metrics, computed on the fly from stored data — no Jira calls. Every read goes
/// through the injected tenant-scoped AppDbContext (EF query filter + Postgres RLS).
/// </summary>
public sealed class MetricsQueryService : IMetricsQueryService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public MetricsQueryService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Tenant-scoped existence check: a cross-tenant/unknown project is filtered out => 404.</summary>
    private Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct) =>
        _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct);

    /// <summary>The project's status-name -> class map. Case-insensitive, per the spec.</summary>
    private async Task<Dictionary<string, StatusClass>> LoadSemanticsAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await _db.StatusSemantics
            .Where(s => s.JiraProjectId == projectId)
            .Select(s => new { s.StatusName, s.Class })
            .ToListAsync(ct);

        var map = new Dictionary<string, StatusClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) map[r.StatusName] = r.Class;
        return map;
    }

    public async Task<StoryCountsReport?> GetStoryCountsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        var semantics = await LoadSemanticsAsync(projectId, ct);

        // Current status comes straight off Issue.StatusId — this metric never loads change history.
        var rows = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Select(i => new
            {
                Status = _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                i.StoryPoints
            })
            .ToListAsync(ct);

        var byStatus = rows
            .GroupBy(r => r.Status)
            .Select(g => new StatusCount(
                g.Key,
                StatusTimelineEngine.Classify(g.Key, semantics).ToString(),
                g.Count(),
                g.Sum(x => x.StoryPoints ?? 0m)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Status)
            .ToList();

        return new StoryCountsReport(byStatus, rows.Count, rows.Sum(r => r.StoryPoints ?? 0m));
    }

    public Task<TimeInProgressReport?> GetTimeInProgressAsync(Guid projectId, CancellationToken ct = default) =>
        throw new NotImplementedException("MT3");

    public Task<ThroughputReport?> GetThroughputAsync(
        Guid projectId, ThroughputBucketSize bucket, DateTime? from, DateTime? to, CancellationToken ct = default) =>
        throw new NotImplementedException("MT3");

    public Task<StatusAgingReport?> GetStatusAgingAsync(
        Guid projectId, int page, int pageSize, CancellationToken ct = default) =>
        throw new NotImplementedException("MT4");
}
```

- [ ] **Step 5: Add the controller**

Create `src/JiraUnpacked.Api/Controllers/MetricsController.cs`:

```csharp
// src/JiraUnpacked.Api/Controllers/MetricsController.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class MetricsController : ControllerBase
{
    private readonly IMetricsQueryService _metrics;
    public MetricsController(IMetricsQueryService metrics) => _metrics = metrics;

    [HttpGet("api/projects/{projectId:guid}/metrics/story-counts")]
    public async Task<IActionResult> StoryCounts(Guid projectId, CancellationToken ct = default)
    {
        var report = await _metrics.GetStoryCountsAsync(projectId, ct);
        return report is null ? NotFound() : Ok(report);
    }
}
```

- [ ] **Step 6: Register the service**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, immediately after the existing
`services.AddScoped<IIssueQueryService, IssueQueryService>();` line, add:

```csharp
        services.AddScoped<IMetricsQueryService, MetricsQueryService>();
```

(`using JiraUnpacked.Infrastructure.Reporting;` is already present from Spec 1's registration.)

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~MetricsStoryCountsTests`
Expected: **PASS (2 tests)**.

- [ ] **Step 8: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs \
        src/JiraUnpacked.Api/Controllers/MetricsController.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Integration/MetricsTestData.cs \
        tests/JiraUnpacked.Tests/Integration/MetricsStoryCountsTests.cs
git commit -m "feat: story-counts metric endpoint (Module E, FR-E2)"
```

---

## Task 3 (MT3): Time-in-progress + throughput

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs` (add `LoadTimelinesAsync`, implement two methods)
- Modify: `src/JiraUnpacked.Api/Controllers/MetricsController.cs` (two routes)
- Test: `tests/JiraUnpacked.Tests/Integration/MetricsTimeInProgressTests.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/MetricsThroughputTests.cs`

**Interfaces:**
- Consumes: `StatusTimelineEngine.Build`, `IssueTimeline`, `StatusTransition` (Task 1); `ProjectExistsAsync`, `LoadSemanticsAsync`, `MetricsTestData` (Task 2).
- Produces: `LoadTimelinesAsync` returning `(List<IssueRow> Issues, Dictionary<string, IssueTimeline> Timelines)` and the private `IssueRow` record — Task 4 reuses both.

- [ ] **Step 1: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Integration/MetricsTimeInProgressTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/MetricsTimeInProgressTests.cs
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class MetricsTimeInProgressTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsTimeInProgressTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ReportDto(double? AverageDays, int CompletedIssuesConsidered, int IssuesExcluded);

    private static DateTime Jan(int day) => new(2024, 1, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Averages_cycle_time_over_completed_issues_only()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // A: Active Jan2 -> Done Jan5  => 3 days.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.DoneStatusId, 3m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "In Progress", Jan(2));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h2", "In Progress", "Done", Jan(5));

        // B: Active Jan2 -> Done Jan7  => 5 days. Average of A and B = 4 days.
        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "b", ctx.DoneStatusId, 1m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10002", "h3", "To Do", "In Progress", Jan(2));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10002", "h4", "In Progress", "Done", Jan(7));

        // C: still In Progress -> no FirstDoneAt -> excluded.
        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "c", ctx.InProgressStatusId, 1m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10003", "h5", "To Do", "In Progress", Jan(3));

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/time-in-progress"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Equal(4.0, r!.AverageDays!.Value, 3);
        Assert.Equal(2, r.CompletedIssuesConsidered);
        Assert.Equal(1, r.IssuesExcluded);
    }

    [Fact]
    public async Task Average_is_null_when_no_issue_qualifies()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // Never reached Done.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.InProgressStatusId, 1m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "In Progress", Jan(2));

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/time-in-progress"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Null(r!.AverageDays);           // null, not 0 — 0 would be a lie
        Assert.Equal(0, r.CompletedIssuesConsidered);
        Assert.Equal(1, r.IssuesExcluded);
    }
}
```

Create `tests/JiraUnpacked.Tests/Integration/MetricsThroughputTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/MetricsThroughputTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class MetricsThroughputTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsThroughputTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record BucketDto(DateTime PeriodStart, int Completed);
    private sealed record ReportDto(string Bucket, DateTime From, DateTime To, List<BucketDto> Buckets);

    private static DateTime Jan(int day) => new(2024, 1, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Buckets_completions_by_day_and_zero_fills_gaps()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // Two issues done on Jan 3, one on Jan 5. Jan 4 must appear as an explicit zero.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.DoneStatusId, null, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "Done", Jan(3));
        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "b", ctx.DoneStatusId, null, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10002", "h2", "To Do", "Done", Jan(3));
        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "c", ctx.DoneStatusId, null, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10003", "h3", "To Do", "Done", Jan(5));

        var r = await (await client.GetAsync(
                $"/api/projects/{ctx.ProjectId}/metrics/throughput?bucket=day&from=2024-01-03&to=2024-01-05"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Equal("day", r!.Bucket);
        Assert.Equal(3, r.Buckets.Count);                       // Jan 3, 4, 5 — dense
        Assert.Equal(2, r.Buckets[0].Completed);
        Assert.Equal(0, r.Buckets[1].Completed);                // zero-filled gap
        Assert.Equal(1, r.Buckets[2].Completed);
        Assert.Equal(Jan(3), r.Buckets[0].PeriodStart);
    }

    [Fact]
    public async Task Week_buckets_snap_outward_to_iso_monday()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // Jan 3 2024 is a Wednesday; its ISO week starts Monday Jan 1.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.DoneStatusId, null, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "Done", Jan(3));

        var r = await (await client.GetAsync(
                $"/api/projects/{ctx.ProjectId}/metrics/throughput?bucket=week&from=2024-01-03&to=2024-01-03"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Single(r!.Buckets);
        Assert.Equal(Jan(1), r.Buckets[0].PeriodStart);   // snapped back to Monday
        Assert.Equal(1, r.Buckets[0].Completed);
    }

    [Fact]
    public async Task Invalid_bucket_returns_400()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/throughput?bucket=fortnight");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~MetricsTimeInProgressTests|FullyQualifiedName~MetricsThroughputTests"`
Expected: FAIL — routes missing (404) / `NotImplementedException("MT3")`.

- [ ] **Step 3: Add shared timeline loading + both metrics to the service**

In `src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs`, add this private record just above the `GetStoryCountsAsync` method:

```csharp
    /// <summary>Projected issue row — no navigation properties, Spec 1's correlated-subquery style.</summary>
    private sealed record IssueRow(
        string JiraIssueId, string JiraKey, string? Summary, string? CurrentStatus,
        decimal? StoryPoints, DateTime JiraCreatedAt);
```

Then add the shared loader (used by time-in-progress, throughput, and Task 4's aging):

```csharp
    /// <summary>
    /// Loads everything the timeline metrics need, project-scoped: the semantics map, the
    /// projected issues, and their status transitions — then folds each issue through the engine.
    /// Story counts deliberately does NOT call this: it needs no change history.
    /// </summary>
    private async Task<(List<IssueRow> Issues, Dictionary<string, IssueTimeline> Timelines)>
        LoadTimelinesAsync(Guid projectId, CancellationToken ct)
    {
        var semantics = await LoadSemanticsAsync(projectId, ct);

        var issues = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Select(i => new IssueRow(
                i.JiraIssueId,
                i.JiraKey,
                i.Summary,
                _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                i.StoryPoints,
                i.JiraCreatedAt))
            .ToListAsync(ct);

        // "status" is the field name Jira's changelog uses, and what the derivation stores.
        var history = await _db.IssueChangeHistory
            .Where(h => h.JiraProjectId == projectId && h.Field == "status")
            .OrderBy(h => h.ChangedAt)
            .Select(h => new { h.JiraIssueId, h.ChangedAt, h.FromValue, h.ToValue })
            .ToListAsync(ct);

        var byIssue = history
            .GroupBy(h => h.JiraIssueId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<StatusTransition>)g
                    .Select(h => new StatusTransition(h.ChangedAt, h.FromValue, h.ToValue))
                    .ToList());

        var timelines = new Dictionary<string, IssueTimeline>();
        foreach (var i in issues)
        {
            var transitions = byIssue.TryGetValue(i.JiraIssueId, out var t)
                ? t
                : (IReadOnlyList<StatusTransition>)Array.Empty<StatusTransition>();
            timelines[i.JiraIssueId] =
                StatusTimelineEngine.Build(i.JiraCreatedAt, i.CurrentStatus, transitions, semantics);
        }

        return (issues, timelines);
    }
```

Replace the `GetTimeInProgressAsync` stub with:

```csharp
    public async Task<TimeInProgressReport?> GetTimeInProgressAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        var (_, timelines) = await LoadTimelinesAsync(projectId, ct);

        var durations = new List<double>();
        var excluded = 0;
        foreach (var t in timelines.Values)
        {
            // Cycle time needs BOTH ends, and Done must come after Active.
            if (t.FirstActiveAt is DateTime a && t.FirstDoneAt is DateTime d && d > a)
                durations.Add((d - a).TotalDays);
            else
                excluded++;
        }

        return new TimeInProgressReport(
            durations.Count == 0 ? null : durations.Average(),
            durations.Count,
            excluded);
    }
```

Replace the `GetThroughputAsync` stub with:

```csharp
    public async Task<ThroughputReport?> GetThroughputAsync(
        Guid projectId, ThroughputBucketSize bucket, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        var (_, timelines) = await LoadTimelinesAsync(projectId, ct);

        // Resolve the window: both ends snap OUTWARD to bucket boundaries; the default is the
        // last 12 buckets ending with the bucket containing "now".
        var toStart = FloorTo(ToUtc(to) ?? _clock.UtcNow, bucket);
        var fromStart = from is DateTime f ? FloorTo(ToUtc(f)!.Value, bucket) : StepBack(toStart, bucket, 11);

        var counts = new Dictionary<DateTime, int>();
        foreach (var t in timelines.Values)
        {
            if (t.FirstDoneAt is not DateTime done) continue;
            var b = FloorTo(done, bucket);
            if (b < fromStart || b > toStart) continue;
            counts[b] = counts.TryGetValue(b, out var c) ? c + 1 : 1;
        }

        // Emit every bucket in range, including empty ones, so the series is dense/chartable.
        var buckets = new List<ThroughputBucket>();
        for (var cursor = fromStart; cursor <= toStart; cursor = NextBucket(cursor, bucket))
            buckets.Add(new ThroughputBucket(cursor, counts.TryGetValue(cursor, out var c) ? c : 0));

        return new ThroughputReport(
            bucket.ToString().ToLowerInvariant(), fromStart, toStart, buckets);
    }

    private static DateTime? ToUtc(DateTime? value) => value is null
        ? null
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static DateTime FloorTo(DateTime utc, ThroughputBucketSize bucket) => bucket switch
    {
        ThroughputBucketSize.Day => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
        ThroughputBucketSize.Week => StartOfIsoWeek(utc),
        ThroughputBucketSize.Month => new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
    };

    /// <summary>ISO week starts Monday.</summary>
    private static DateTime StartOfIsoWeek(DateTime utc)
    {
        var day = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
        var delta = (7 + (int)day.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return day.AddDays(-delta);
    }

    private static DateTime NextBucket(DateTime start, ThroughputBucketSize bucket) => bucket switch
    {
        ThroughputBucketSize.Day => start.AddDays(1),
        ThroughputBucketSize.Week => start.AddDays(7),
        ThroughputBucketSize.Month => start.AddMonths(1),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
    };

    private static DateTime StepBack(DateTime start, ThroughputBucketSize bucket, int steps)
    {
        var cursor = start;
        for (var i = 0; i < steps; i++)
            cursor = bucket switch
            {
                ThroughputBucketSize.Day => cursor.AddDays(-1),
                ThroughputBucketSize.Week => cursor.AddDays(-7),
                ThroughputBucketSize.Month => cursor.AddMonths(-1),
                _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
            };
        return cursor;
    }
```

- [ ] **Step 4: Add the two routes**

In `src/JiraUnpacked.Api/Controllers/MetricsController.cs`, add after the `StoryCounts` action:

```csharp
    [HttpGet("api/projects/{projectId:guid}/metrics/time-in-progress")]
    public async Task<IActionResult> TimeInProgress(Guid projectId, CancellationToken ct = default)
    {
        var report = await _metrics.GetTimeInProgressAsync(projectId, ct);
        return report is null ? NotFound() : Ok(report);
    }

    [HttpGet("api/projects/{projectId:guid}/metrics/throughput")]
    public async Task<IActionResult> Throughput(
        Guid projectId,
        [FromQuery] string? bucket = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        if (!ThroughputBucketSizes.TryParse(bucket, out var size))
            return BadRequest(new { error = "bucket must be one of: day, week, month" });

        var report = await _metrics.GetThroughputAsync(projectId, size, from, to, ct);
        return report is null ? NotFound() : Ok(report);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~MetricsTimeInProgressTests|FullyQualifiedName~MetricsThroughputTests"`
Expected: **PASS (5 tests)**.

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs \
        src/JiraUnpacked.Api/Controllers/MetricsController.cs \
        tests/JiraUnpacked.Tests/Integration/MetricsTimeInProgressTests.cs \
        tests/JiraUnpacked.Tests/Integration/MetricsThroughputTests.cs
git commit -m "feat: cycle-time + throughput metrics (Module E, FR-E2)"
```

---

## Task 4 (MT4): Status aging (list + histogram)

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs` (implement `GetStatusAgingAsync`)
- Modify: `src/JiraUnpacked.Api/Controllers/MetricsController.cs` (one route)
- Test: `tests/JiraUnpacked.Tests/Integration/MetricsStatusAgingTests.cs`

**Interfaces:**
- Consumes: `LoadTimelinesAsync`, `IssueRow` (Task 3); `AgingItem`, `AgingBand`, `StatusAgingReport`, `PagedResult<T>` (Task 1).
- Produces: nothing later tasks depend on.

Ages are asserted against timestamps seeded **relative to `DateTime.UtcNow` at seed time**, with a tolerance — the production code uses the injected `IClock`, and this avoids replacing `IClock` in the shared test host.

- [ ] **Step 1: Write the failing test**

Create `tests/JiraUnpacked.Tests/Integration/MetricsStatusAgingTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/MetricsStatusAgingTests.cs
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class MetricsStatusAgingTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsStatusAgingTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record AgingItemDto(string JiraIssueId, string JiraKey, string? Summary,
        string? CurrentStatus, string Class, double AgeDays, DateTime EnteredAt);
    private sealed record BandDto(string Band, int Count);
    private sealed record PageDto(List<AgingItemDto> Items, int Page, int PageSize, int TotalCount);
    private sealed record ReportDto(PageDto Items, List<BandDto> Histogram);

    [Fact]
    public async Task Lists_open_issues_oldest_first_with_histogram_over_whole_population()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        // One open 10 days (band "1-2w" = [7,14)), one open 2 days (band "1-3d" = [1,3)),
        // and one Done issue that must NOT appear at all.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "old", ctx.InProgressStatusId, null, now.AddDays(-30));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "In Progress", now.AddDays(-10));

        await MetricsTestData.SeedIssueAsync(ctx, "10002", "ACME-2", "new", ctx.InProgressStatusId, null, now.AddDays(-30));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10002", "h2", "To Do", "In Progress", now.AddDays(-2));

        await MetricsTestData.SeedIssueAsync(ctx, "10003", "ACME-3", "done", ctx.DoneStatusId, null, now.AddDays(-30));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10003", "h3", "To Do", "Done", now.AddDays(-1));

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/status-aging"))
            .Content.ReadFromJsonAsync<ReportDto>();

        // Done issues are excluded from the aging population.
        Assert.Equal(2, r!.Items.TotalCount);
        Assert.Equal(2, r.Items.Items.Count);

        // Oldest first.
        Assert.Equal("ACME-1", r.Items.Items[0].JiraKey);
        Assert.Equal("ACME-2", r.Items.Items[1].JiraKey);
        Assert.Equal(10.0, r.Items.Items[0].AgeDays, 1);
        Assert.Equal(2.0, r.Items.Items[1].AgeDays, 1);
        Assert.Equal("In Progress", r.Items.Items[0].CurrentStatus);
        Assert.Equal("Active", r.Items.Items[0].Class);

        // All five bands always emitted; the 10-day issue lands in "1-2w", the 2-day in "1-3d".
        Assert.Equal(5, r.Histogram.Count);
        Assert.Equal(1, Assert.Single(r.Histogram, b => b.Band == "1-2w").Count);
        Assert.Equal(1, Assert.Single(r.Histogram, b => b.Band == "1-3d").Count);
        Assert.Equal(0, Assert.Single(r.Histogram, b => b.Band == ">2w").Count);
    }

    [Fact]
    public async Task Histogram_covers_the_whole_population_not_just_the_page()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var now = DateTime.UtcNow;

        // Three open issues, all ~2 days old; ask for a single-item page.
        for (var n = 1; n <= 3; n++)
        {
            await MetricsTestData.SeedIssueAsync(ctx, $"1000{n}", $"ACME-{n}", "x", ctx.InProgressStatusId, null, now.AddDays(-30));
            await MetricsTestData.SeedStatusChangeAsync(ctx, $"1000{n}", $"h{n}", "To Do", "In Progress", now.AddDays(-2));
        }

        var r = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/status-aging?page=1&pageSize=1"))
            .Content.ReadFromJsonAsync<ReportDto>();

        Assert.Single(r!.Items.Items);          // page holds one
        Assert.Equal(3, r.Items.TotalCount);    // population is three
        Assert.Equal(3, Assert.Single(r.Histogram, b => b.Band == "1-3d").Count); // histogram counts all three
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~MetricsStatusAgingTests`
Expected: FAIL — route missing / `NotImplementedException("MT4")`.

- [ ] **Step 3: Implement aging**

In `src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs`, replace the `GetStatusAgingAsync` stub with:

```csharp
    public async Task<StatusAgingReport?> GetStatusAgingAsync(
        Guid projectId, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        var (issues, timelines) = await LoadTimelinesAsync(projectId, ct);
        var now = _clock.UtcNow;

        // Population: everything not currently Done.
        var open = new List<AgingItem>();
        foreach (var i in issues)
        {
            var t = timelines[i.JiraIssueId];
            if (t.CurrentClass == StatusClass.Done) continue;

            open.Add(new AgingItem(
                i.JiraIssueId, i.JiraKey, i.Summary,
                t.CurrentStatus, t.CurrentClass.ToString(),
                (now - t.CurrentEnteredAt).TotalDays, t.CurrentEnteredAt));
        }

        // Histogram spans the WHOLE population, deliberately computed before paging.
        var histogram = BuildHistogram(open);

        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var ordered = open
            .OrderByDescending(a => a.AgeDays)
            .ThenBy(a => a.JiraKey)
            .ToList();

        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new StatusAgingReport(
            new PagedResult<AgingItem>(items, page, pageSize, ordered.Count), histogram);
    }

    /// <summary>Half-open day bands, so every age falls in exactly one. All five are always emitted.</summary>
    private static IReadOnlyList<AgingBand> BuildHistogram(IReadOnlyList<AgingItem> items)
    {
        var bands = new (string Name, double Lo, double Hi)[]
        {
            ("<=1d", 0, 1),
            ("1-3d", 1, 3),
            ("3-7d", 3, 7),
            ("1-2w", 7, 14),
            (">2w", 14, double.PositiveInfinity),
        };

        return bands
            .Select(b => new AgingBand(b.Name, items.Count(i => i.AgeDays >= b.Lo && i.AgeDays < b.Hi)))
            .ToList();
    }
```

- [ ] **Step 4: Add the route**

In `src/JiraUnpacked.Api/Controllers/MetricsController.cs`, add:

```csharp
    [HttpGet("api/projects/{projectId:guid}/metrics/status-aging")]
    public async Task<IActionResult> StatusAging(
        Guid projectId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var report = await _metrics.GetStatusAgingAsync(projectId, page, pageSize, ct);
        return report is null ? NotFound() : Ok(report);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~MetricsStatusAgingTests`
Expected: **PASS (2 tests)**.

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs \
        src/JiraUnpacked.Api/Controllers/MetricsController.cs \
        tests/JiraUnpacked.Tests/Integration/MetricsStatusAgingTests.cs
git commit -m "feat: status-aging metric with age histogram (Module E, FR-E2)"
```

---

## Task 5 (MT5): Serving index + the cross-cutting guarantees

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueChangeHistoryConfiguration.cs`
- Create: migration `…AddChangeHistoryStatusIndex.cs` (generated)
- Test: `tests/JiraUnpacked.Tests/Integration/MetricsGuaranteesTests.cs`

**Interfaces:**
- Consumes: all four endpoints (Tasks 2–4); `MetricsTestData.SetSemanticClassAsync` (Task 2).
- Produces: nothing.

This task proves the two properties the whole spec exists for: **tenant isolation** and **mapping edits change results with no Jira re-pull**.

- [ ] **Step 1: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Integration/MetricsGuaranteesTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/MetricsGuaranteesTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The two guarantees FR-E2 exists for: tenant isolation on every metrics route, and
/// "editing the status mapping changes the numbers with zero Jira calls".
/// NOTE: this file starts no WireMock server — that absence IS the zero-Jira proof.
/// </summary>
public sealed class MetricsGuaranteesTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public MetricsGuaranteesTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TimeInProgressDto(double? AverageDays, int CompletedIssuesConsidered, int IssuesExcluded);

    private static DateTime Jan(int day) => new(2024, 1, day, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Routes =
    {
        "story-counts", "time-in-progress", "throughput", "status-aging",
    };

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_metrics()
    {
        var (_, tenantA) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenantA);
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.InProgressStatusId, 3m, Jan(1));

        var (clientB, _) = await MetricsTestData.AuthedTenantAsync(_factory);

        // B asks for A's project id on every route -> filtered out -> 404, no existence leak.
        foreach (var route in Routes)
        {
            var resp = await clientB.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/{route}");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Unknown_project_returns_404_on_every_route()
    {
        var (client, _) = await MetricsTestData.AuthedTenantAsync(_factory);
        var missing = Guid.NewGuid();

        foreach (var route in Routes)
        {
            var resp = await client.GetAsync($"/api/projects/{missing}/metrics/{route}");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Editing_the_status_mapping_changes_the_numbers_without_touching_jira()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);

        // "To Do" (Other) -> "In Progress" (Active) on Jan 2 -> "Done" (Done) on Jan 5.
        await MetricsTestData.SeedIssueAsync(ctx, "10001", "ACME-1", "a", ctx.DoneStatusId, 1m, Jan(1));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h1", "To Do", "In Progress", Jan(2));
        await MetricsTestData.SeedStatusChangeAsync(ctx, "10001", "h2", "In Progress", "Done", Jan(5));

        var before = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/time-in-progress"))
            .Content.ReadFromJsonAsync<TimeInProgressDto>();
        Assert.Equal(3.0, before!.AverageDays!.Value, 3);   // Jan 2 -> Jan 5

        // Reclassify "To Do" (JiraStatusId "1") from Other to Active: the issue now becomes
        // Active at CREATION (Jan 1), so the cycle time grows to 4 days. No sync, no Jira call.
        await MetricsTestData.SetSemanticClassAsync(ctx, "1", "Active");

        var after = await (await client.GetAsync($"/api/projects/{ctx.ProjectId}/metrics/time-in-progress"))
            .Content.ReadFromJsonAsync<TimeInProgressDto>();
        Assert.Equal(4.0, after!.AverageDays!.Value, 3);    // Jan 1 -> Jan 5

        Assert.NotEqual(before.AverageDays!.Value, after.AverageDays!.Value);
    }
}
```

- [ ] **Step 2: Run to verify the mapping test's arithmetic**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~MetricsGuaranteesTests`
Expected: **PASS (3 tests)** — the endpoints already exist from Tasks 2–4, so these assert existing behavior. If the mapping test fails, the classification is not happening at query time; fix that before continuing.

- [ ] **Step 3: Add the serving index to the EF configuration**

In `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueChangeHistoryConfiguration.cs`, add this line immediately after the existing `b.HasIndex(x => new { x.JiraIssueId, x.Field, x.ChangedAt });`:

```csharp
        // Serves the FR-E2 project-scoped status-history scan (Field == "status", time-ordered)
        // so metrics reads never table-scan.
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.Field, x.ChangedAt });
```

- [ ] **Step 4: Generate the migration**

Run:

```bash
dotnet ef migrations add AddChangeHistoryStatusIndex \
  --project src/JiraUnpacked.Infrastructure \
  --startup-project src/JiraUnpacked.Infrastructure
```

Expected: a new migration under `src/JiraUnpacked.Infrastructure/Persistence/Migrations/` whose `Up` contains exactly **one** `CreateIndex` on `IssueChangeHistory` over `EndUserId, JiraProjectId, Field, ChangedAt`. **Open the generated file and confirm there is nothing else in it** — any extra operation means unintended model drift; stop and investigate.

- [ ] **Step 5: Verify no drift and run everything**

Run:

```bash
dotnet ef migrations has-pending-model-changes \
  --project src/JiraUnpacked.Infrastructure \
  --startup-project src/JiraUnpacked.Infrastructure
```
Expected: `No changes have been made to the model since the last migration.`

Run: `dotnet test JiraUnpacked.sln`
Expected: full suite green, including the **untouched FR-B2 isolation gate test**.

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueChangeHistoryConfiguration.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        tests/JiraUnpacked.Tests/Integration/MetricsGuaranteesTests.cs
git commit -m "feat: metrics serving index + isolation/mapping-change guarantees (Module E, FR-E2)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green (Spec 1's 155 + 19 new = 174 expected; report the actual number).
- [ ] `dotnet ef migrations has-pending-model-changes …` → "No changes".
- [ ] Confirm by grep that `src/JiraUnpacked.Infrastructure/Reporting/` contains no `IgnoreQueryFilters`, no `FromSql`, no `IPrivilegedDbContextFactory`, and no `.Include(`.
- [ ] Confirm the FR-B2 gate test file is unmodified on this branch (`git diff develop --stat` shows no gate-test changes).
- [ ] Push `feat/report-metrics`; open PR → `develop`; CI green; squash-merge; then the `develop → main` release (merge-commit). Before the release, check `git diff --stat origin/develop origin/main` — a non-empty diff means a follow-up landed on main and must be back-merged first.

## Notes for execution

- **Read-only + tenant-scoped throughout.** Every query goes through the injected `AppDbContext`. Tests may use raw SQL to seed fixtures, but every assertion reads through the API as the owning tenant.
- **The engine takes no clock.** Only `MetricsQueryService` needs `now` (aging), via the injected `IClock`.
- **Do not add navigation properties to `Issue`.** Dictionary names resolve via correlated subqueries, exactly as `IssueQueryService` does.
- Aging tests seed relative to `DateTime.UtcNow` and assert with a tolerance, deliberately avoiding an `IClock` replacement in the shared test host.
