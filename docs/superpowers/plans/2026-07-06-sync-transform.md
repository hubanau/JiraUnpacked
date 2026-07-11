# Module D Spec 3 — Sync Transform (raw → modeled) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Derive the modeled tables (`Issues` + dictionaries + `IssueChangeHistory` + epic→story hierarchy) from raw, with a Jira-free re-derivation path, completing the sync engine.

**Architecture:** A new `RawChangelog` raw store holds the full paged changelog (populated once, at sync time). A Jira-free `DerivationService` rebuilds all modeled tables from raw (`RawIssueSnapshots` + `RawChangelog`) + `FieldMappings`. The `SyncOrchestrator` runs changelog-ingest → derive after extract; `POST /rederive` runs derive alone.

**Tech Stack:** ASP.NET Core 8, EF Core 8 + Npgsql (pinned 8.0.11), Postgres (jsonb, RLS), System.Text.Json, xUnit + WireMock.Net.

## Global Constraints

- Target **net8.0**; EF Core + Npgsql pinned **8.0.11**.
- **One** new table (`RawChangelog`) + one new column (`SyncRuns.Kind`) → one schema migration + one RLS migration. No other schema change.
- Every new/derived row is tenant-scoped: EF global query filter (`HasQueryFilter(x => x.EndUserId == _currentTenantId)`) + Postgres RLS (ENABLE + FORCE + `tenant_isolation` policy + GRANT to `jiraunpacked_app`). The FR-B2 gate test extends to `RawChangelog`.
- **Re-derivation makes zero Jira calls.** Jira is touched only at sync time (extract + changelog-ingest). `DerivationService` is a pure function of raw + config.
- Idempotency: all modeled writes are upserts keyed on stable Jira ids; `IssueChangeHistory` is append-only + idempotent on `(EndUserId, JiraIssueId, JiraHistoryId, Field)`.
- The Jira token is decrypted only in memory (changelog paging); never logged, returned, or stored in `SyncRun.Error`.
- EF commands: `--project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure`. Integration tests via `JiraUnpackedApiFactory` (Development env; runtime `AppDbContext` on the RLS `jiraunpacked_app` role; privileged reads/seeds via `TestConnectionString`). Jira HTTP via WireMock.Net.
- One commit per task. Controller pushes before PR.

**Namespaces:** entities `JiraUnpacked.Domain.Entities`; enums `JiraUnpacked.Domain.Enums`; EF config/migrations `JiraUnpacked.Infrastructure.Persistence.*`; Jira client `JiraUnpacked.Infrastructure.Jira` + DTOs `JiraUnpacked.Domain.Abstractions.Jira`; sync services `JiraUnpacked.Infrastructure.Sync` + abstractions `JiraUnpacked.Domain.Abstractions`; API `JiraUnpacked.Api.Controllers`.

**Key existing shapes (do not redefine):** `Issue(Id,EndUserId,JiraProjectId,JiraIssueId,JiraKey,IssueTypeId?,StatusId?,PriorityId?,AssigneeId?,SprintId?,TeamId?,StoryPoints?,ParentJiraIssueId?,ParentIssueId?,Summary?,JiraCreatedAt,JiraUpdatedAt,LastSyncedAt)`; dict entities `Status(JiraConnectionId,JiraStatusId,Name,StatusCategory?)`, `IssueType(JiraConnectionId,JiraIssueTypeId,Name)`, `Priority(JiraConnectionId,JiraPriorityId,Name)`, `JiraUser(JiraConnectionId,JiraAccountId,DisplayName)`, `Sprint(JiraProjectId,JiraSprintId,Name,State?,StartDate?,EndDate?)`, `Team(JiraProjectId,JiraTeamId,Name)`; `IssueChangeHistory(JiraProjectId,JiraIssueId,JiraHistoryId,Field,FromValue?,ToValue?,Author?,ChangedAt)` unique `(EndUserId,JiraIssueId,JiraHistoryId,Field)`; `FieldMapping(JiraProjectId,Role,JiraFieldId)` with `FieldRole { Team, Assignee, Sprint, StoryPoints, Status }`; `SyncRun(...,Status,IssuesProcessed,Error?,NextRunAt?)`; `SyncOrchestrator.RunProjectAsync`; `JiraApiClient` partial with `Build`/`SendAsync`/`EnsureOk`/`DelayForRetryAsync`/`MaxRateLimitRetries`.

---

## Task 1 (TR1): `RawChangelog` + `SyncRun.Kind` — migration, RLS, gate  *(critical — opus review)*

**Files:**
- Create: `src/JiraUnpacked.Domain/Entities/RawChangelog.cs`, `src/JiraUnpacked.Domain/Enums/SyncRunKind.cs`
- Modify: `src/JiraUnpacked.Domain/Entities/SyncRun.cs` (add `Kind`)
- Create: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/RawChangelogConfiguration.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/SyncRunConfiguration.cs`, `AppDbContext.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/ISyncOrchestrator.cs` (`SyncRunView` gains `Kind`), `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` + `SyncRunQuery.cs` (map `Kind`)
- Create (generated): migrations `<ts>_AddRawChangelog.cs`, `<ts>_AddRawChangelogRls.cs`
- Modify: `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs`
- Test: `tests/JiraUnpacked.Tests/Persistence/RawChangelogModelTests.cs`

**Interfaces:**
- Produces: `RawChangelog(Id, EndUserId, JiraProjectId, JiraIssueId, Payload jsonb, FetchedAt)` unique `(EndUserId, JiraIssueId)`; `enum SyncRunKind { Sync, Rederive }`; `SyncRun.Kind` (string, default `Sync`); `SyncRunView` gains a `string Kind` field (last positional).

- [ ] **Step 1: Write the failing model test**

```csharp
// tests/JiraUnpacked.Tests/Persistence/RawChangelogModelTests.cs
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Persistence;

public class RawChangelogModelTests
{
    private static AppDbContext Ctx()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p").Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void RawChangelog_is_jsonb_and_unique_per_issue()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.RawChangelog))!;
        Assert.Equal("jsonb", e.FindProperty(nameof(Domain.Entities.RawChangelog.Payload))!.GetColumnType());
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "EndUserId") && i.Properties.Any(p => p.Name == "JiraIssueId"));
    }

    [Fact]
    public void SyncRun_Kind_is_stored_as_string()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.SyncRun))!;
        Assert.Equal(typeof(string), e.FindProperty(nameof(Domain.Entities.SyncRun.Kind))!.GetProviderClrType());
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~RawChangelogModelTests`
Expected: FAIL (types not defined).

- [ ] **Step 3: Add the entity + enum + SyncRun.Kind**

```csharp
// src/JiraUnpacked.Domain/Entities/RawChangelog.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class RawChangelog : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraIssueId { get; set; } = default!;
    public string Payload { get; set; } = default!; // jsonb: full assembled changelog
    public DateTime FetchedAt { get; set; }
}
```
```csharp
// src/JiraUnpacked.Domain/Enums/SyncRunKind.cs
namespace JiraUnpacked.Domain.Enums;
public enum SyncRunKind { Sync, Rederive }
```
In `src/JiraUnpacked.Domain/Entities/SyncRun.cs` add (after `Status`):
```csharp
    public SyncRunKind Kind { get; set; }
```
(add `using JiraUnpacked.Domain.Enums;` — already present.)

- [ ] **Step 4: Configs + AppDbContext**

```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/RawChangelogConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class RawChangelogConfiguration : IEntityTypeConfiguration<RawChangelog>
{
    public void Configure(EntityTypeBuilder<RawChangelog> b)
    {
        b.ToTable("RawChangelog");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraIssueId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Payload).IsRequired().HasColumnType("jsonb");
        b.Property(x => x.FetchedAt).IsRequired();
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraIssueId }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
```
In `SyncRunConfiguration.Configure` add:
```csharp
        b.Property(x => x.Kind).IsRequired().HasConversion<string>().HasMaxLength(16);
```
In `AppDbContext`: add `public DbSet<RawChangelog> RawChangelogs => Set<RawChangelog>();` and, in `OnModelCreating`, `modelBuilder.Entity<RawChangelog>().HasQueryFilter(x => x.EndUserId == _currentTenantId);`

- [ ] **Step 5: Surface `Kind` in `SyncRunView`**

In `src/JiraUnpacked.Domain/Abstractions/ISyncOrchestrator.cs`, change the `SyncRunView` record to add `string Kind` as the LAST positional parameter:
```csharp
public sealed record SyncRunView(
    Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt,
    int IssuesProcessed, string? Error, DateTime? NextRunAt, string Kind);
```
In `SyncOrchestrator.ToView`:
```csharp
    private static SyncRunView ToView(SyncRun r) =>
        new(r.Id, r.JiraProjectId, r.Status.ToString(), r.StartedAt, r.FinishedAt, r.IssuesProcessed, r.Error, r.NextRunAt, r.Kind.ToString());
```
In `SyncRunQuery.ListAsync`'s in-memory `.Select`, add `r.Kind.ToString()` as the last argument to `new SyncRunView(...)`.

- [ ] **Step 6: Generate migrations**

```
dotnet ef migrations add AddRawChangelog --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```
Inspect: it creates the `RawChangelog` table (Payload `jsonb`, unique `(EndUserId, JiraIssueId)`) AND adds the `Kind` column to `SyncRuns`. **Set a default for the new non-null column** so existing rows migrate: in the generated `AddColumn<string>(name: "Kind", table: "SyncRuns", ...)` add `defaultValue: "Sync"` (and `nullable: false`). Then:
```
dotnet ef migrations add AddRawChangelogRls --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```
Replace its Up/Down with the single-table RLS SQL (mirror `AddSyncEngineRls`):
```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql(@"GRANT SELECT, INSERT, UPDATE, DELETE ON ""RawChangelog"" TO jiraunpacked_app;");
    migrationBuilder.Sql(@"
ALTER TABLE ""RawChangelog"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""RawChangelog"" FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON ""RawChangelog""
    USING (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation ON ""RawChangelog"";
ALTER TABLE ""RawChangelog"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""RawChangelog"" DISABLE ROW LEVEL SECURITY;
");
}
```

- [ ] **Step 7: Extend the gate to `RawChangelog`**

Append a `[Fact]` to `TenantIsolationGateTests` proving RLS on `RawChangelog` (mirror the existing `Issues`/`JiraProjects` facts). Add a `SeedRawChangelogAsync` helper that inserts a `JiraConnection → JiraProject → RawChangelog` chain via the privileged `TestConnectionString` (raw Npgsql), returning `(tenant, rawChangelogId)`, then as `jiraunpacked_app` with the GUC set to tenant B assert A's row count = 0 and B's own = 1. `RawChangelog` columns: `(Id, EndUserId, JiraProjectId, JiraIssueId, Payload, FetchedAt)` — Payload is jsonb, insert `'{}'::jsonb` or `'{}'`.

```csharp
[Fact]
public async Task Tenant_cannot_read_another_tenants_raw_changelog_via_rls()
{
    var (tenantA, rowA) = await SeedRawChangelogAsync();
    var (tenantB, rowB) = await SeedRawChangelogAsync();

    await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestAppConnectionString);
    await conn.OpenAsync();
    await SetTenantAsync(conn, tenantB);

    Assert.Equal(0L, await CountByIdAsync(conn, "RawChangelog", rowA));
    Assert.Equal(1L, await CountByIdAsync(conn, "RawChangelog", rowB));
}

private async Task<(Guid tenant, Guid row)> SeedRawChangelogAsync()
{
    var tenant = Guid.NewGuid();
    var connId = Guid.NewGuid();
    var projId = Guid.NewGuid();
    var rowId = Guid.NewGuid();
    await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
    await conn.OpenAsync();
    await ExecAsync(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@id,@t,'https://x','e','c',now())",
        ("id", connId), ("t", tenant));
    await ExecAsync(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@id,@t,@c,'ACME','1',60,now())",
        ("id", projId), ("t", tenant), ("c", connId));
    await ExecAsync(conn, @"INSERT INTO ""RawChangelog""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@id,@t,@p,'10001','{}'::jsonb,now())",
        ("id", rowId), ("t", tenant), ("p", projId));
    return (tenant, rowId);
}

// Add these small helpers to the file if not already present:
private static async Task ExecAsync(Npgsql.NpgsqlConnection conn, string sql, params (string, object)[] ps)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
    await cmd.ExecuteNonQueryAsync();
}
private static async Task<long> CountByIdAsync(Npgsql.NpgsqlConnection conn, string table, Guid id)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $@"SELECT count(*) FROM ""{table}"" WHERE ""Id"" = @id";
    cmd.Parameters.AddWithValue("id", id);
    return (long)(await cmd.ExecuteScalarAsync())!;
}
```
(If the file already has a `SetTenantAsync`, reuse it. Ensure `using Npgsql;` or fully-qualify.)

- [ ] **Step 8: Run tests + drift + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~RawChangelogModelTests|FullyQualifiedName~TenantIsolationGateTests"` → PASS (2 model + 4 gate facts).
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes".
Run: `dotnet test JiraUnpacked.sln` → all green (SyncRunView shape change is additive; existing tests deserialize a subset).

```bash
git add src/JiraUnpacked.Domain/Entities/RawChangelog.cs src/JiraUnpacked.Domain/Enums/SyncRunKind.cs \
        src/JiraUnpacked.Domain/Entities/SyncRun.cs src/JiraUnpacked.Domain/Abstractions/ISyncOrchestrator.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/ \
        src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs src/JiraUnpacked.Infrastructure/Sync/SyncRunQuery.cs \
        tests/JiraUnpacked.Tests/Persistence/RawChangelogModelTests.cs \
        tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs
git commit -m "feat: RawChangelog store + SyncRun.Kind; RLS + gate covers RawChangelog (Module D transform)"
```

---

## Task 2 (TR2): Jira changelog paging — `GetIssueChangelogPageAsync`

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/Jira/ChangelogPage.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/IJiraApiClient.cs`
- Create: `src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.Changelog.cs`
- Test: `tests/JiraUnpacked.Tests/Jira/JiraApiClientChangelogTests.cs`

**Interfaces:**
- Consumes: `JiraCredentials`, the `partial JiraApiClient` (`Build`/`SendAsync`/`EnsureOk`/`DelayForRetryAsync`/`MaxRateLimitRetries`).
- Produces: `record ChangelogPage(IReadOnlyList<string> ValuesJson, int Total, bool IsLast)`; `IJiraApiClient.GetIssueChangelogPageAsync(JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/JiraUnpacked.Tests/Jira/JiraApiClientChangelogTests.cs
using System.Net;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

public sealed class JiraApiClientChangelogTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private IJiraApiClient Client() => new JiraApiClient(new HttpClient());
    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "t");
    public void Dispose() => _server.Stop();

    [Fact]
    public async Task Reads_values_total_and_islast()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                startAt = 0, maxResults = 100, total = 1, isLast = true,
                values = new object[] { new { id = "h1", created = "2024-01-01T00:00:00.000+0000",
                    items = new object[] { new { field = "status", fromString = "To Do", toString = "Done" } } } }
            }));

        var page = await Client().GetIssueChangelogPageAsync(Creds(), "10001", 0, CancellationToken.None);

        Assert.Single(page.ValuesJson);
        Assert.Contains("\"id\":\"h1\"", page.ValuesJson[0].Replace(" ", ""));
        Assert.Equal(1, page.Total);
        Assert.True(page.IsLast);
    }

    [Fact]
    public async Task Passes_startAt_and_returns_not_last()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet().WithParam("startAt", "100"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            { startAt = 100, maxResults = 100, total = 250, isLast = false, values = new object[] { new { id = "h2" } } }));

        var page = await Client().GetIssueChangelogPageAsync(Creds(), "10001", 100, CancellationToken.None);
        Assert.False(page.IsLast);
        Assert.Equal(250, page.Total);
    }

    [Fact]
    public async Task Retries_after_429()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet())
            .InScenario("rl").WillSetStateTo("ok")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));
        _server.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet())
            .InScenario("rl").WhenStateIs("ok")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            { startAt = 0, maxResults = 100, total = 0, isLast = true, values = new object[0] }));

        var page = await Client().GetIssueChangelogPageAsync(Creds(), "10001", 0, CancellationToken.None);
        Assert.True(page.IsLast);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~JiraApiClientChangelogTests` → FAIL.

- [ ] **Step 3: DTO + interface**

```csharp
// src/JiraUnpacked.Domain/Abstractions/Jira/ChangelogPage.cs
namespace JiraUnpacked.Domain.Abstractions.Jira;
public sealed record ChangelogPage(IReadOnlyList<string> ValuesJson, int Total, bool IsLast);
```
Add to `IJiraApiClient` (after `SearchIssuesPageAsync`):
```csharp
Task<ChangelogPage> GetIssueChangelogPageAsync(JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct = default);
```

- [ ] **Step 4: Implement (new partial)**

```csharp
// src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.Changelog.cs
using System.Net;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient
{
    private const int ChangelogPageSize = 100;

    public async Task<ChangelogPage> GetIssueChangelogPageAsync(
        JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct = default)
    {
        var path = $"/rest/api/3/issue/{Uri.EscapeDataString(jiraIssueId)}/changelog?startAt={startAt}&maxResults={ChangelogPageSize}";
        for (var attempt = 0; ; attempt++)
        {
            using var req = Build(HttpMethod.Get, creds, path);
            using var resp = await SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                await DelayForRetryAsync(resp, ct);
                continue;
            }
            EnsureOk(resp);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var values = new List<string>();
            if (root.TryGetProperty("values", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    values.Add(el.GetRawText());

            var total = root.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : values.Count;
            var isLast = root.TryGetProperty("isLast", out var il) && il.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? il.GetBoolean()
                : startAt + values.Count >= total;

            return new ChangelogPage(values, total, isLast);
        }
    }
}
```

- [ ] **Step 5: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~JiraApiClientChangelogTests` → PASS (3).

```bash
git add src/JiraUnpacked.Domain/Abstractions/Jira/ChangelogPage.cs \
        src/JiraUnpacked.Domain/Abstractions/IJiraApiClient.cs \
        src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.Changelog.cs \
        tests/JiraUnpacked.Tests/Jira/JiraApiClientChangelogTests.cs
git commit -m "feat: Jira issue-changelog paging (Module D transform, FR-D3)"
```

---

## Task 3 (TR3): Changelog ingest → `RawChangelog`

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IChangelogIngestService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Sync/ChangelogIngestService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Sync/ChangelogIngestTests.cs`

**Interfaces:**
- Consumes: `AppDbContext` (tenant-scoped `RawIssueSnapshots`/`RawChangelogs`), `IJiraApiClient.GetIssueChangelogPageAsync`, `IClock`, `JiraCredentials`.
- Produces: `IChangelogIngestService { Task IngestProjectAsync(Guid projectId, JiraCredentials creds, CancellationToken ct = default); }`. For each raw issue: reads embedded `changelog`; if `histories.length >= total` uses it, else pages the dedicated endpoint; upserts `RawChangelog.Payload` = `{ "histories": [ ...all entries... ] }` by `(EndUserId, JiraIssueId)`.

- [ ] **Step 1: Write the failing integration tests**

```csharp
// tests/JiraUnpacked.Tests/Sync/ChangelogIngestTests.cs
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Time;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Sync;

public sealed class ChangelogIngestTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public ChangelogIngestTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    // Runs the ingest service under a fresh DI scope with the ambient tenant set (mirrors the tick job).
    private async Task IngestAsync(Guid tenant, Guid projectId, JiraCredentials creds)
    {
        var scopeSetter = _factory.Services.GetRequiredService<ITenantScope>();
        using (scopeSetter.BeginScope(tenant))
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IChangelogIngestService>();
            await svc.IngestProjectAsync(projectId, creds, CancellationToken.None);
        }
    }

    private async Task<long> RawChangelogCount(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new JiraUnpacked.Infrastructure.Tenancy.NullCurrentTenant());
        return await db.RawChangelogs.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == projectId);
    }

    // Seeds connection+project+one RawIssueSnapshot (raw SQL) with the given embedded changelog JSON; returns (tenant, projectId, connId).
    private async Task<(Guid tenant, Guid projectId)> SeedIssueAsync(string embeddedChangelogJson)
    {
        var tenant = Guid.NewGuid(); var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        var payload = $"{{\"id\":\"10001\",\"key\":\"ACME-1\",\"changelog\":{embeddedChangelogJson}}}";
        await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@id,@t,'https://x','e','c',now())", ("id", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@id,@t,@c,'ACME','1',60,now())", ("id", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@id,@t,@p,'10001',@payload::jsonb,now())", ("id", Guid.NewGuid()), ("t", tenant), ("p", projId), ("payload", payload));
        return (tenant, projId);
    }
    private static async Task Exec(Npgsql.NpgsqlConnection conn, string sql, params (string, object)[] ps)
    { await using var c = conn.CreateCommand(); c.CommandText = sql; foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v); await c.ExecuteNonQueryAsync(); }

    [Fact]
    public async Task Embedded_complete_changelog_is_ingested_without_a_dedicated_call()
    {
        // total == histories.length -> complete; no dedicated endpoint stubbed, so a call would 404/throw.
        var embedded = "{\"startAt\":0,\"maxResults\":100,\"total\":1,\"histories\":[{\"id\":\"h1\",\"items\":[{\"field\":\"status\"}]}]}";
        var (tenant, projId) = await SeedIssueAsync(embedded);

        await IngestAsync(tenant, projId, new JiraCredentials(_jira.Url!, "e@x.com", "t"));

        Assert.Equal(1, await RawChangelogCount(projId));
        Assert.Empty(_jira.LogEntries); // no dedicated changelog call
    }

    [Fact]
    public async Task Truncated_changelog_is_paged_from_the_dedicated_endpoint()
    {
        // embedded says total=2 but only 1 history -> truncated -> page dedicated endpoint.
        var embedded = "{\"startAt\":0,\"maxResults\":1,\"total\":2,\"histories\":[{\"id\":\"h1\"}]}";
        var (tenant, projId) = await SeedIssueAsync(embedded);
        _jira.Given(Request.Create().WithPath("/rest/api/3/issue/10001/changelog").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             { startAt = 0, maxResults = 100, total = 2, isLast = true,
               values = new object[] { new { id = "h1" }, new { id = "h2" } } }));

        await IngestAsync(tenant, projId, new JiraCredentials(_jira.Url!, "e@x.com", "t"));

        Assert.Equal(1, await RawChangelogCount(projId));
        Assert.NotEmpty(_jira.LogEntries); // dedicated endpoint used
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~ChangelogIngestTests` → FAIL.

- [ ] **Step 3: Interface**

```csharp
// src/JiraUnpacked.Domain/Abstractions/IChangelogIngestService.cs
using JiraUnpacked.Domain.Abstractions.Jira;
namespace JiraUnpacked.Domain.Abstractions;
public interface IChangelogIngestService
{
    Task IngestProjectAsync(Guid projectId, JiraCredentials creds, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement**

```csharp
// src/JiraUnpacked.Infrastructure/Sync/ChangelogIngestService.cs
using System.Text;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed class ChangelogIngestService : IChangelogIngestService
{
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly IClock _clock;

    public ChangelogIngestService(AppDbContext db, IJiraApiClient jira, IClock clock)
    { _db = db; _jira = jira; _clock = clock; }

    public async Task IngestProjectAsync(Guid projectId, JiraCredentials creds, CancellationToken ct = default)
    {
        var snapshots = await _db.RawIssueSnapshots.Where(r => r.JiraProjectId == projectId).ToListAsync(ct);
        foreach (var snap in snapshots)
        {
            var histories = await AssembleHistoriesJsonAsync(snap, creds, ct);
            var payload = "{\"histories\":[" + string.Join(",", histories) + "]}";

            var existing = await _db.RawChangelogs.FirstOrDefaultAsync(x => x.JiraIssueId == snap.JiraIssueId, ct);
            if (existing is null)
                _db.RawChangelogs.Add(new RawChangelog
                {
                    Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = snap.JiraIssueId,
                    Payload = payload, FetchedAt = _clock.UtcNow
                });
            else { existing.Payload = payload; existing.FetchedAt = _clock.UtcNow; }
            await _db.SaveChangesAsync(ct);
        }
    }

    // Returns each history entry as a raw JSON string, from the embedded changelog if complete, else by paging.
    private async Task<List<string>> AssembleHistoriesJsonAsync(RawIssueSnapshot snap, JiraCredentials creds, CancellationToken ct)
    {
        var result = new List<string>();
        using var doc = JsonDocument.Parse(snap.Payload);
        var root = doc.RootElement;

        int total = 0;
        if (root.TryGetProperty("changelog", out var cl) && cl.ValueKind == JsonValueKind.Object)
        {
            total = cl.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : 0;
            if (cl.TryGetProperty("histories", out var hs) && hs.ValueKind == JsonValueKind.Array)
                foreach (var h in hs.EnumerateArray()) result.Add(h.GetRawText());
        }

        if (result.Count >= total) return result; // embedded is complete

        // Truncated: page the dedicated endpoint from the start (authoritative full list).
        result.Clear();
        var startAt = 0;
        while (true)
        {
            var page = await _jira.GetIssueChangelogPageAsync(creds, snap.JiraIssueId, startAt, ct);
            result.AddRange(page.ValuesJson);
            if (page.IsLast || page.ValuesJson.Count == 0) break;
            startAt += page.ValuesJson.Count;
        }
        return result;
    }
}
```

- [ ] **Step 5: Register + run + commit**

Register in `DependencyInjection.AddInfrastructure`: `services.AddScoped<IChangelogIngestService, ChangelogIngestService>();`

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~ChangelogIngestTests` → PASS (2).

```bash
git add src/JiraUnpacked.Domain/Abstractions/IChangelogIngestService.cs \
        src/JiraUnpacked.Infrastructure/Sync/ChangelogIngestService.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Sync/ChangelogIngestTests.cs
git commit -m "feat: changelog ingest -> RawChangelog (embedded or paged) (Module D transform, FR-D3)"
```

---

## Task 4 (TR4): Derivation core (dictionaries + Issues) + `POST /rederive`

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IDerivationService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/ISyncOrchestrator.cs` (add `RunRederiveAsync`), `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` (implement it; inject `IDerivationService`)
- Modify: `src/JiraUnpacked.Api/Controllers/SyncController.cs` (add `POST /rederive`), `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/RederiveTests.cs`

**Interfaces:**
- Consumes: `AppDbContext` (tenant-scoped), `IClock`, the modeled entities + `FieldMapping`/`FieldRole`; the existing `SyncRunResult`/`SyncOutcome`/`SyncRunView` types.
- Produces: `IDerivationService { Task DeriveProjectAsync(Guid projectId, CancellationToken ct = default); }` (Jira-free; reads `RawIssueSnapshots` + `RawChangelog` + `FieldMappings`, upserts dictionaries + `Issues`) — declared `sealed partial` (TR5 adds change-history + hierarchy in a second file). Also `ISyncOrchestrator.RunRederiveAsync(Guid projectId, CancellationToken)` → derive-only under a `SyncRun(Kind=Rederive)`, and `POST /api/projects/{projectId}/rederive` calling it.

- [ ] **Step 1: Write the failing integration test**

```csharp
// tests/JiraUnpacked.Tests/Integration/RederiveTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class RederiveTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public RederiveTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record SyncRunDto(Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt, int IssuesProcessed, string? Error, DateTime? NextRunAt, string Kind);

    private void StubConfig()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
    }

    private void StubSearchOneIssue()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             {
                 issues = new object[] { new {
                     id = "10001", key = "ACME-1",
                     fields = new {
                         summary = "First", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000",
                         issuetype = new { id = "1", name = "Story" },
                         priority = new { id = "3", name = "High" },
                         status = new { id = "10", name = "Done", statusCategory = new { name = "Done" } },
                         assignee = new { accountId = "u1", displayName = "Alice" }
                     },
                     changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] }
                 } }
             }));
    }

    private async Task<(HttpClient client, Guid projectId)> ConfiguredProjectAsync()
    {
        StubConfig();
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        // Map status + assignee to the standard fields so derivation resolves them.
        await client.PutAsJsonAsync($"/api/projects/{project.Id}/field-mappings", new { mappings = new[] {
            new { role = "Status", jiraFieldId = "status" }, new { role = "Assignee", jiraFieldId = "assignee" } } });
        return (client, project.Id);
    }

    private async Task<Domain.Entities.Issue?> IssueAsync(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        return await db.Issues.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.JiraProjectId == projectId);
    }

    [Fact]
    public async Task Rederive_builds_issues_and_dictionaries_from_raw_without_calling_jira()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        StubSearchOneIssue();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { }); // lands raw (+ derives once TR6 wires it)

        _jira.Reset(); // any Jira call during rederive would now 404
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/rederive", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var run = (await resp.Content.ReadFromJsonAsync<SyncRunDto>())!;
        Assert.Equal("Rederive", run.Kind);
        Assert.Equal("Succeeded", run.Status);
        Assert.Empty(_jira.LogEntries); // zero Jira calls on rederive

        var issue = await IssueAsync(projectId);
        Assert.NotNull(issue);
        Assert.Equal("ACME-1", issue!.JiraKey);
        Assert.Equal("First", issue.Summary);
        Assert.NotNull(issue.StatusId);    // resolved via the status dict
        Assert.NotNull(issue.IssueTypeId); // standard issuetype dict
        Assert.NotNull(issue.PriorityId);
        Assert.NotNull(issue.AssigneeId);
    }

    [Fact]
    public async Task Rederive_unknown_project_returns_404()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var resp = await client.PostAsJsonAsync($"/api/projects/{Guid.NewGuid()}/rederive", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~RederiveTests` → FAIL (no `/rederive`, no derivation). (Note: until TR6 wires derive into `/sync`, the first test relies on `/rederive` doing the derivation; that is exactly what this task adds.)

- [ ] **Step 3: Derivation interface**

```csharp
// src/JiraUnpacked.Domain/Abstractions/IDerivationService.cs
namespace JiraUnpacked.Domain.Abstractions;
public interface IDerivationService
{
    /// <summary>Jira-free: rebuilds modeled tables for a project from raw + field mappings. Caller sets the ambient tenant.</summary>
    Task DeriveProjectAsync(Guid projectId, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement derivation (dictionaries + Issues)**

```csharp
// src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs
using System.Globalization;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed partial class DerivationService : IDerivationService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    public DerivationService(AppDbContext db, IClock clock) { _db = db; _clock = clock; }

    public async Task DeriveProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return;
        var connectionId = project.JiraConnectionId;

        var mappings = await _db.FieldMappings.Where(m => m.JiraProjectId == projectId).ToListAsync(ct);
        string FieldFor(FieldRole role, string standard) => mappings.FirstOrDefault(m => m.Role == role)?.JiraFieldId ?? standard;
        var statusField = FieldFor(FieldRole.Status, "status");
        var assigneeField = FieldFor(FieldRole.Assignee, "assignee");
        var sprintField = mappings.FirstOrDefault(m => m.Role == FieldRole.Sprint)?.JiraFieldId;
        var pointsField = mappings.FirstOrDefault(m => m.Role == FieldRole.StoryPoints)?.JiraFieldId;
        var teamField = mappings.FirstOrDefault(m => m.Role == FieldRole.Team)?.JiraFieldId;

        var snapshots = await _db.RawIssueSnapshots.Where(r => r.JiraProjectId == projectId).ToListAsync(ct);

        // Dictionary caches (jiraId -> surrogate Id) so we resolve each once.
        var statusCache = new Dictionary<string, Guid>();
        var typeCache = new Dictionary<string, Guid>();
        var prioCache = new Dictionary<string, Guid>();
        var userCache = new Dictionary<string, Guid>();
        var sprintCache = new Dictionary<string, Guid>();
        var teamCache = new Dictionary<string, Guid>();

        foreach (var snap in snapshots)
        {
            using var doc = JsonDocument.Parse(snap.Payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object) continue;

            var issue = await _db.Issues.FirstOrDefaultAsync(i => i.JiraIssueId == snap.JiraIssueId, ct)
                        ?? new Issue { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = snap.JiraIssueId };

            issue.JiraKey = GetString(root, "key") ?? issue.JiraKey ?? snap.JiraIssueId;
            issue.Summary = GetString(f, "summary");
            issue.JiraCreatedAt = GetDate(f, "created") ?? issue.JiraCreatedAt;
            issue.JiraUpdatedAt = GetDate(f, "updated") ?? issue.JiraUpdatedAt;
            issue.ParentJiraIssueId = f.TryGetProperty("parent", out var par) && par.ValueKind == JsonValueKind.Object
                ? GetString(par, "id") : null;
            issue.LastSyncedAt = _clock.UtcNow;

            issue.IssueTypeId = await ResolveTypeAsync(f, "issuetype", connectionId, typeCache, ct);
            issue.PriorityId = await ResolvePriorityAsync(f, "priority", connectionId, prioCache, ct);
            issue.StatusId = await ResolveStatusAsync(f, statusField, connectionId, statusCache, ct);
            issue.AssigneeId = await ResolveUserAsync(f, assigneeField, connectionId, userCache, ct);
            issue.SprintId = sprintField is null ? null : await ResolveSprintAsync(f, sprintField, projectId, sprintCache, ct);
            issue.TeamId = teamField is null ? null : await ResolveTeamAsync(f, teamField, projectId, teamCache, ct);
            issue.StoryPoints = pointsField is null ? null : GetDecimal(f, pointsField);

            if (_db.Entry(issue).State == EntityState.Detached) _db.Issues.Add(issue);
        }
        await _db.SaveChangesAsync(ct);

        // (TR5 appends: change-history from RawChangelog + hierarchy second pass, here.)
    }

    // ---- JSON helpers ----
    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? GetDecimal(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
    private static DateTime? GetDate(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
           ? dt : null;

    // ---- dictionary resolvers (upsert by grain unique key, cache by jiraId) ----
    private async Task<Guid?> ResolveStatusAsync(JsonElement fields, string field, Guid connId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var jid = GetString(s, "id"); if (jid is null) return null;
        if (cache.TryGetValue(jid, out var id)) return id;
        var name = GetString(s, "name") ?? jid;
        var category = s.TryGetProperty("statusCategory", out var sc) && sc.ValueKind == JsonValueKind.Object ? GetString(sc, "name") : null;
        var row = await _db.Statuses.FirstOrDefaultAsync(x => x.JiraConnectionId == connId && x.JiraStatusId == jid, ct);
        if (row is null) { row = new Status { Id = Guid.NewGuid(), JiraConnectionId = connId, JiraStatusId = jid, Name = name, StatusCategory = category }; _db.Statuses.Add(row); }
        else { row.Name = name; row.StatusCategory = category; }
        cache[jid] = row.Id; return row.Id;
    }
    private async Task<Guid?> ResolveTypeAsync(JsonElement fields, string field, Guid connId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var jid = GetString(s, "id"); if (jid is null) return null;
        if (cache.TryGetValue(jid, out var id)) return id;
        var name = GetString(s, "name") ?? jid;
        var row = await _db.IssueTypes.FirstOrDefaultAsync(x => x.JiraConnectionId == connId && x.JiraIssueTypeId == jid, ct);
        if (row is null) { row = new IssueType { Id = Guid.NewGuid(), JiraConnectionId = connId, JiraIssueTypeId = jid, Name = name }; _db.IssueTypes.Add(row); }
        else row.Name = name;
        cache[jid] = row.Id; return row.Id;
    }
    private async Task<Guid?> ResolvePriorityAsync(JsonElement fields, string field, Guid connId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var jid = GetString(s, "id"); if (jid is null) return null;
        if (cache.TryGetValue(jid, out var id)) return id;
        var name = GetString(s, "name") ?? jid;
        var row = await _db.Priorities.FirstOrDefaultAsync(x => x.JiraConnectionId == connId && x.JiraPriorityId == jid, ct);
        if (row is null) { row = new Priority { Id = Guid.NewGuid(), JiraConnectionId = connId, JiraPriorityId = jid, Name = name }; _db.Priorities.Add(row); }
        else row.Name = name;
        cache[jid] = row.Id; return row.Id;
    }
    private async Task<Guid?> ResolveUserAsync(JsonElement fields, string field, Guid connId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var jid = GetString(s, "accountId"); if (jid is null) return null;
        if (cache.TryGetValue(jid, out var id)) return id;
        var name = GetString(s, "displayName") ?? jid;
        var row = await _db.JiraUsers.FirstOrDefaultAsync(x => x.JiraConnectionId == connId && x.JiraAccountId == jid, ct);
        if (row is null) { row = new JiraUser { Id = Guid.NewGuid(), JiraConnectionId = connId, JiraAccountId = jid, DisplayName = name }; _db.JiraUsers.Add(row); }
        else row.DisplayName = name;
        cache[jid] = row.Id; return row.Id;
    }
    // Sprint field is typically an array; take the last (most recent) sprint object.
    private async Task<Guid?> ResolveSprintAsync(JsonElement fields, string field, Guid projectId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s)) return null;
        JsonElement sprint;
        if (s.ValueKind == JsonValueKind.Array) { if (s.GetArrayLength() == 0) return null; sprint = s[s.GetArrayLength() - 1]; }
        else if (s.ValueKind == JsonValueKind.Object) sprint = s;
        else return null;
        var jid = sprint.TryGetProperty("id", out var idv)
            ? (idv.ValueKind == JsonValueKind.Number ? idv.GetInt64().ToString() : GetString(sprint, "id")) : null;
        if (jid is null) return null;
        if (cache.TryGetValue(jid, out var cid)) return cid;
        var name = GetString(sprint, "name") ?? jid;
        var state = GetString(sprint, "state");
        var row = await _db.Sprints.FirstOrDefaultAsync(x => x.JiraProjectId == projectId && x.JiraSprintId == jid, ct);
        if (row is null) { row = new Sprint { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraSprintId = jid, Name = name, State = state }; _db.Sprints.Add(row); }
        else { row.Name = name; row.State = state; }
        cache[jid] = row.Id; return row.Id;
    }
    // Team field: object {id, value|name} or a scalar string.
    private async Task<Guid?> ResolveTeamAsync(JsonElement fields, string field, Guid projectId, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (!fields.TryGetProperty(field, out var s)) return null;
        string? jid, name;
        if (s.ValueKind == JsonValueKind.Object) { jid = GetString(s, "id") ?? GetString(s, "value"); name = GetString(s, "value") ?? GetString(s, "name") ?? jid; }
        else if (s.ValueKind == JsonValueKind.String) { jid = s.GetString(); name = jid; }
        else return null;
        if (jid is null) return null;
        if (cache.TryGetValue(jid, out var cid)) return cid;
        var row = await _db.Teams.FirstOrDefaultAsync(x => x.JiraProjectId == projectId && x.JiraTeamId == jid, ct);
        if (row is null) { row = new Team { Id = Guid.NewGuid(), JiraProjectId = projectId, JiraTeamId = jid, Name = name! }; _db.Teams.Add(row); }
        else row.Name = name!;
        cache[jid] = row.Id; return row.Id;
    }
}
```

- [ ] **Step 5: `RunRederiveAsync` on the orchestrator + `POST /rederive` + DI**

Register: `services.AddScoped<IDerivationService, DerivationService>();`

Add to `ISyncOrchestrator` (after `RunProjectAsync`):
```csharp
/// <summary>Re-derive modeled tables from existing raw (no Jira). Caller sets the ambient tenant.</summary>
Task<SyncRunResult> RunRederiveAsync(Guid projectId, CancellationToken ct = default);
```
In `SyncOrchestrator`, inject `IDerivationService` (add a field + ctor param `IDerivationService derivation` → `_derivation`) and add — reusing the same `SyncRun` lifecycle + result types as the extract path, but derive-only and `Kind = Rederive`:
```csharp
public async Task<SyncRunResult> RunRederiveAsync(Guid projectId, CancellationToken ct = default)
{
    var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
    if (project is null) return new SyncRunResult(SyncOutcome.ProjectNotFound, null);
    if (await _db.SyncRuns.AnyAsync(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Running, ct))
        return new SyncRunResult(SyncOutcome.AlreadyRunning, null);

    var run = new SyncRun
    {
        Id = Guid.NewGuid(), JiraProjectId = projectId, StartedAt = _clock.UtcNow,
        Status = SyncRunStatus.Running, Kind = SyncRunKind.Rederive, IssuesProcessed = 0
    };
    _db.SyncRuns.Add(run);
    await _db.SaveChangesAsync(ct);
    try
    {
        await _derivation.DeriveProjectAsync(projectId, ct);
        run.Status = SyncRunStatus.Succeeded; run.FinishedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
    catch (Exception ex)
    {
        run.Status = SyncRunStatus.Failed; run.Error = ex.Message; run.FinishedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
    return new SyncRunResult(SyncOutcome.Completed, ToView(run));
}
```
(add `using JiraUnpacked.Domain.Enums;` if not present — it is.)

Add the endpoint to `SyncController` (uses the existing `_orchestrator` field):
```csharp
[HttpPost("api/projects/{projectId:guid}/rederive")]
public async Task<IActionResult> Rederive(Guid projectId, CancellationToken ct)
{
    var result = await _orchestrator.RunRederiveAsync(projectId, ct);
    return result.Outcome switch
    {
        SyncOutcome.ProjectNotFound => NotFound(),
        SyncOutcome.AlreadyRunning => Conflict(),
        _ => Ok(result.Run)
    };
}
```

- [ ] **Step 6: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~RederiveTests` → PASS (2).

```bash
git add src/JiraUnpacked.Domain/Abstractions/IDerivationService.cs \
        src/JiraUnpacked.Domain/Abstractions/ISyncOrchestrator.cs \
        src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs \
        src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs \
        src/JiraUnpacked.Api/Controllers/SyncController.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Integration/RederiveTests.cs
git commit -m "feat: Jira-free derivation (dictionaries + Issues) + POST /rederive (Module D transform, FR-D5/D6)"
```

---

## Task 5 (TR5): Derivation — change history + hierarchy

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Sync/DerivationService.History.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs` (call the two new passes at the end of `DeriveProjectAsync`)
- Test: `tests/JiraUnpacked.Tests/Integration/DerivationHistoryTests.cs`

**Interfaces:**
- Consumes: `RawChangelog` (from TR3), the `Issue` rows upserted in TR4, `IssueChangeHistory` (unique `(EndUserId, JiraIssueId, JiraHistoryId, Field)`).
- Produces: `DerivationService.DeriveChangeHistoryAsync` + `DeriveHierarchyAsync` (partial), invoked at the end of `DeriveProjectAsync`.

- [ ] **Step 1: Write the failing integration test**

```csharp
// tests/JiraUnpacked.Tests/Integration/DerivationHistoryTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class DerivationHistoryTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public DerivationHistoryTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    // Two issues: epic ACME-1 (10001) and story ACME-2 (10002, parent 10001); the story has one changelog entry.
    private void StubSearchEpicAndStory()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             {
                 issues = new object[] {
                     new { id = "10001", key = "ACME-1", fields = new { summary = "Epic", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-01T00:00:00.000+0000" },
                           changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } },
                     new { id = "10002", key = "ACME-2", fields = new { summary = "Story", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", parent = new { id = "10001", key = "ACME-1" } },
                           changelog = new { startAt = 0, maxResults = 100, total = 1, histories = new object[] {
                               new { id = "h1", author = new { displayName = "Alice" }, created = "2024-01-02T09:00:00.000+0000",
                                     items = new object[] { new { field = "status", fromString = "To Do", toString = "In Progress" } } } } } }
                 }
             }));
    }

    private async Task<(HttpClient client, Guid projectId)> ConfiguredAsync()
    {
        StubSearchEpicAndStory();
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        return (client, project.Id);
    }

    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public async Task Sync_derives_change_history_and_resolves_hierarchy()
    {
        var (client, projectId) = await ConfiguredAsync();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { }); // TR6 wires ingest+derive into /sync

        await using var db = Privileged();
        var history = await db.IssueChangeHistory.IgnoreQueryFilters().Where(h => h.JiraProjectId == projectId).ToListAsync();
        Assert.Contains(history, h => h.JiraIssueId == "10002" && h.Field == "status" && h.ToValue == "In Progress" && h.JiraHistoryId == "h1");

        var story = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraIssueId == "10002");
        var epic = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraIssueId == "10001");
        Assert.Equal(epic.Id, story.ParentIssueId); // hierarchy resolved

        // Idempotent: rederive doesn't duplicate history.
        await client.PostAsJsonAsync($"/api/projects/{projectId}/rederive", new { });
        await using var db2 = Privileged();
        var count = await db2.IssueChangeHistory.IgnoreQueryFilters().CountAsync(h => h.JiraProjectId == projectId);
        Assert.Equal(1, count);
    }
}
```

*(This test depends on TR6 wiring ingest+derive into `/sync`; if run before TR6 the `history`/parent assertions fail. The subagent implements TR5 then TR6; the controller runs this test green after TR6. During TR5 in isolation, verify the two new passes via a direct-service unit path if preferred — but the plan orders TR5 before TR6 and lets the full suite confirm at TR6. To keep TR5 independently green, this task's Step 4 also adds a focused test that seeds `RawChangelog` + `Issues` via raw SQL and calls `/rederive`; see below.)*

- [ ] **Step 2: Add a TR5-independent focused test (raw-SQL seeded, no TR6 needed)**

Add to `DerivationHistoryTests` a second fact that seeds directly and uses only `/rederive`:

```csharp
    [Fact]
    public async Task Rederive_builds_history_and_parent_from_seeded_raw()
    {
        // Configure a project (no sync); seed two RawIssueSnapshots + the story's RawChangelog via raw SQL, then rederive.
        StubSearchEpicAndStory(); // only for connection validation/config calls
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;

        // resolve the tenant (EndUserId) of this project via privileged read
        Guid tenant;
        await using (var db = Privileged())
            tenant = await db.JiraProjects.IgnoreQueryFilters().Where(p => p.Id == project.Id).Select(p => p.EndUserId).FirstAsync();

        await using (var conn2 = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await conn2.OpenAsync();
            string epic = "{\"id\":\"10001\",\"key\":\"ACME-1\",\"fields\":{\"summary\":\"Epic\",\"created\":\"2024-01-01T00:00:00.000+0000\",\"updated\":\"2024-01-01T00:00:00.000+0000\"}}";
            string story = "{\"id\":\"10002\",\"key\":\"ACME-2\",\"fields\":{\"summary\":\"Story\",\"created\":\"2024-01-01T00:00:00.000+0000\",\"updated\":\"2024-01-02T00:00:00.000+0000\",\"parent\":{\"id\":\"10001\"}}}";
            string cl = "{\"histories\":[{\"id\":\"h1\",\"author\":{\"displayName\":\"Alice\"},\"created\":\"2024-01-02T09:00:00.000+0000\",\"items\":[{\"field\":\"status\",\"fromString\":\"To Do\",\"toString\":\"In Progress\"}]}]}";
            await Exec(conn2, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001',@j::jsonb,now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", project.Id), ("j", epic));
            await Exec(conn2, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10002',@j::jsonb,now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", project.Id), ("j", story));
            await Exec(conn2, @"INSERT INTO ""RawChangelog""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10002',@j::jsonb,now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", project.Id), ("j", cl));
        }

        await client.PostAsJsonAsync($"/api/projects/{project.Id}/rederive", new { });

        await using var vdb = Privileged();
        Assert.Contains(await vdb.IssueChangeHistory.IgnoreQueryFilters().Where(h => h.JiraProjectId == project.Id).ToListAsync(),
            h => h.JiraIssueId == "10002" && h.ToValue == "In Progress");
        var story2 = await vdb.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraIssueId == "10002");
        var epic2 = await vdb.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraIssueId == "10001");
        Assert.Equal(epic2.Id, story2.ParentIssueId);
    }

    private static async Task Exec(Npgsql.NpgsqlConnection conn, string sql, params (string, object)[] ps)
    { await using var c = conn.CreateCommand(); c.CommandText = sql; foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v); await c.ExecuteNonQueryAsync(); }
```

- [ ] **Step 3: Run, verify the raw-SQL fact fails**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~DerivationHistoryTests.Rederive_builds_history_and_parent_from_seeded_raw` → FAIL (no history/hierarchy passes yet).

- [ ] **Step 4: Implement the two passes**

```csharp
// src/JiraUnpacked.Infrastructure/Sync/DerivationService.History.cs
using System.Globalization;
using System.Text.Json;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed partial class DerivationService
{
    // Append-only, idempotent: skip (issue, historyId, field) already present.
    private async Task DeriveChangeHistoryAsync(Guid projectId, CancellationToken ct)
    {
        var logs = await _db.RawChangelogs.Where(r => r.JiraProjectId == projectId).ToListAsync(ct);
        var existing = (await _db.IssueChangeHistory.Where(h => h.JiraProjectId == projectId)
                .Select(h => new { h.JiraIssueId, h.JiraHistoryId, h.Field }).ToListAsync(ct))
            .Select(x => (x.JiraIssueId, x.JiraHistoryId, x.Field)).ToHashSet();

        foreach (var log in logs)
        {
            using var doc = JsonDocument.Parse(log.Payload);
            if (!doc.RootElement.TryGetProperty("histories", out var hs) || hs.ValueKind != JsonValueKind.Array) continue;
            foreach (var h in hs.EnumerateArray())
            {
                var historyId = GetString(h, "id"); if (historyId is null) continue;
                var author = h.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? GetString(a, "displayName") : null;
                var changedAt = GetDate(h, "created") ?? _clock.UtcNow;
                if (!h.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var it in items.EnumerateArray())
                {
                    var fieldName = GetString(it, "field"); if (fieldName is null) continue;
                    if (!existing.Add((log.JiraIssueId, historyId, fieldName))) continue; // dedup
                    _db.IssueChangeHistory.Add(new IssueChangeHistory
                    {
                        Id = Guid.NewGuid(), JiraProjectId = projectId, JiraIssueId = log.JiraIssueId,
                        JiraHistoryId = historyId, Field = fieldName,
                        FromValue = GetString(it, "fromString"), ToValue = GetString(it, "toString"),
                        Author = author, ChangedAt = changedAt
                    });
                }
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    // Second pass: resolve ParentIssueId (self-FK) by matching ParentJiraIssueId to a sibling in the project.
    private async Task DeriveHierarchyAsync(Guid projectId, CancellationToken ct)
    {
        var issues = await _db.Issues.Where(i => i.JiraProjectId == projectId).ToListAsync(ct);
        var byJiraId = issues.ToDictionary(i => i.JiraIssueId, i => i.Id);
        var changed = false;
        foreach (var issue in issues)
        {
            var target = issue.ParentJiraIssueId is not null && byJiraId.TryGetValue(issue.ParentJiraIssueId, out var pid)
                ? pid : (Guid?)null;
            if (issue.ParentIssueId != target) { issue.ParentIssueId = target; changed = true; }
        }
        if (changed) await _db.SaveChangesAsync(ct);
    }
}
```
In `DerivationService.DeriveProjectAsync`, replace the trailing comment `// (TR5 appends...)` with:
```csharp
        await DeriveChangeHistoryAsync(projectId, ct);
        await DeriveHierarchyAsync(projectId, ct);
```

- [ ] **Step 5: Run the focused fact + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~DerivationHistoryTests.Rederive_builds_history_and_parent_from_seeded_raw` → PASS.
(The other fact, `Sync_derives_...`, goes green after TR6.)

```bash
git add src/JiraUnpacked.Infrastructure/Sync/DerivationService.History.cs \
        src/JiraUnpacked.Infrastructure/Sync/DerivationService.cs \
        tests/JiraUnpacked.Tests/Integration/DerivationHistoryTests.cs
git commit -m "feat: derive change history + epic->story hierarchy (Module D transform, FR-D3/D4)"
```

---

## Task 6 (TR6): Wire ingest + derive into `SyncOrchestrator`  *(critical — opus review)*

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs`
- Test: (the `Sync_derives_change_history_and_resolves_hierarchy` fact from TR5 now goes green) + `tests/JiraUnpacked.Tests/Integration/SyncPipelineTests.cs`

**Interfaces:**
- Consumes: `IChangelogIngestService` (TR3) — inject it into `SyncOrchestrator` (`IDerivationService` was already injected in TR4 for `RunRederiveAsync`; reuse that field).
- Produces: after a successful extract, in the same tenant scope, `SyncOrchestrator` runs `IngestProjectAsync(projectId, creds)` then `DeriveProjectAsync(projectId)`; a failure in either marks the run `Failed` (raw stays landed). The `SyncRun` Kind stays `Sync` (default) on this path.

- [ ] **Step 1: Write the failing pipeline test**

```csharp
// tests/JiraUnpacked.Tests/Integration/SyncPipelineTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncPipelineTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public SyncPipelineTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    [Fact]
    public async Task Sync_lands_raw_and_produces_modeled_issue_and_changelog()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
        {
            issues = new object[] { new { id = "10001", key = "ACME-1",
                fields = new { summary = "S", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" } },
                changelog = new { startAt = 0, maxResults = 100, total = 1, histories = new object[] {
                    new { id = "h1", created = "2024-01-02T00:00:00.000+0000", items = new object[] { new { field = "status", toString = "Done" } } } } } } }
        }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        await client.PutAsJsonAsync($"/api/projects/{project.Id}/field-mappings", new { mappings = new[] { new { role = "Status", jiraFieldId = "status" } } });

        var resp = await client.PostAsJsonAsync($"/api/projects/{project.Id}/sync", new { });
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        Assert.Equal(1, await db.RawChangelogs.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == project.Id));
        var issue = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id);
        Assert.NotNull(issue.StatusId);
        Assert.Contains(await db.IssueChangeHistory.IgnoreQueryFilters().Where(h => h.JiraProjectId == project.Id).ToListAsync(),
            h => h.Field == "status" && h.ToValue == "Done");
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncPipelineTests` → FAIL (sync doesn't derive yet).

- [ ] **Step 3: Wire ingest + derive into the orchestrator**

Inject the two services (add fields + ctor params `IChangelogIngestService ingest`, `IDerivationService derivation`). In `RunProjectAsync`, inside the `try`, AFTER the extract page loop completes (right before setting `run.Status = Succeeded`), add:

```csharp
            // Transform: ingest the full changelog to raw, then derive the modeled tables (Jira-free).
            await _ingest.IngestProjectAsync(projectId, creds, ct);
            await _derivation.DeriveProjectAsync(projectId, ct);
```

`run.Kind` stays default (`Sync`). If ingest/derive throw, the existing `catch` marks the run `Failed` (raw is already saved). No other change.

- [ ] **Step 4: Run pipeline + the TR5 sync-fact + full suite + drift + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~SyncPipelineTests|FullyQualifiedName~DerivationHistoryTests"` → PASS (pipeline 1 + history 2).
Run: `dotnet test JiraUnpacked.sln` → all green.
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes".

```bash
git add src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs \
        tests/JiraUnpacked.Tests/Integration/SyncPipelineTests.cs
git commit -m "feat: run changelog-ingest + derivation after extract in the sync (Module D transform, FR-D5)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green.
- [ ] `dotnet ef migrations has-pending-model-changes ...` → "No changes".
- [ ] Push `feat/sync-transform`; verify `origin == local`; open PR → `develop`; CI green; squash-merge; verify develop; cut the `develop → main` release PR (merge-commit). **This completes Module D** — Module E (reporting) is next.

## Notes for execution
- **TR1 is schema+RLS+gate** → opus review; the FR-B2 gate must cover `RawChangelog`.
- **TR6 wires the transform into the sync path** → opus review; confirm re-derivation makes zero Jira calls, ingest/derive run in the same tenant scope, and a derive failure leaves raw intact.
- Re-derivation (`POST /rederive`) and derivation must touch **no** Jira. Only extract (Spec 2) + changelog-ingest (TR3) call Jira.
- No writes bypass the tenant scope; `RawChangelog` + all modeled tables are RLS-protected.
- If the "extract follow-ups" branch (generic `SyncRun.Error`, remove `AddHttpContextAccessor`) merged into develop first, rebase this branch onto it before the PR.
