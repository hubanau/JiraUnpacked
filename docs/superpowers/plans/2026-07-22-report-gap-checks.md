# Module E Spec 3 — Curated Gap-Checks (FR-E3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A small registry of curated correctness checks over the epic→story hierarchy, exposed as a catalog plus a run-one endpoint, with epics identified by a configurable issue-type classification.

**Architecture:** `GapCheckRunner` performs one tenant-scoped load into a `GapCheckContext` (issues + a `ChildrenByParent` lookup + whether an epic type is configured); each `IGapCheck` is a **pure, synchronous** function over that context, so checks are unit-testable with no database. The runner owns ordering and pagination. Epics are identified by an `IssueTypeClass` column on the existing `IssueTypes` dictionary, so no new tenant table is introduced.

**Tech Stack:** ASP.NET Core 8 Web API, EF Core 8 + Npgsql/PostgreSQL, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-22-report-gap-checks-design.md` (commit afa9caa).
**Branch:** `feat/report-checks`, cut from `develop` @ af9aa0e.

## Global Constraints

Every task's requirements implicitly include these.

- **All reads go through the injected tenant-scoped `AppDbContext`.** No `IgnoreQueryFilters`, no privileged/superuser DbContext factory, no `FromSql`/raw SQL, no `.Include(...)` in production code. (Tests may use raw SQL to seed fixtures.)
- **No new tenant table.** This spec adds one column and no index. The FR-B2 tenant-isolation gate test must remain untouched and green.
- **No new index** is added by this spec.
- **No navigation properties** may be added to `Issue` or `IssueType`; dictionary values resolve via correlated-subquery projection.
- Unknown or cross-tenant project → **404** via `_db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)` over the tenant-filtered set. Unknown `checkId` → **404**. Unknown or cross-tenant connection → **404**.
- Gap-checks **never** read `IssueChangeHistory` and **never** call `StatusTimelineEngine.Build`; they use only the pure `StatusTimelineEngine.Classify`.
- Status classification joins on status **name**, case-insensitive — the semantics dictionary MUST be built with `StringComparer.OrdinalIgnoreCase`. A default dictionary silently makes classification case-sensitive and no test catches it.
- A status name absent from `StatusSemantics` classifies as `StatusClass.Other`, never `Done`.
- Pagination clamps match Specs 1–2 exactly: `page < 1 → 1`; `pageSize < 1 → 50`; `pageSize > 100 → 100`. `Children` on a violation are **not** paginated.
- `PUT issue-types` is **additive**: only the issue types named in the body change; unlisted types keep their current `Class`. Clearing is an explicit `"Other"`.
- Integration tests seed by raw SQL **scoped to the test's own `JiraProjectId`/connection** (parallel tests share one Postgres and reuse fixed Jira ids), and assert **exact** values.
- EF migrations: `dotnet ef migrations add <Name> --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure`.
- The `jiraunpacked_test` Postgres runs in Docker container `jiraunpacked-db`. If tests cannot connect, run `docker start jiraunpacked-db`, wait, and re-run — a connection failure is not a code failure.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Domain/Enums/IssueTypeClass.cs` | `{ Epic, Other }`. |
| `src/JiraUnpacked.Domain/Entities/IssueType.cs` | + `Class` property. |
| `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueTypeConfiguration.cs` | + `Class` column config. |
| `src/JiraUnpacked.Domain/Abstractions/IIssueTypeClassService.cs` | Config service contract + its input/output records. |
| `src/JiraUnpacked.Infrastructure/Services/IssueTypeClassService.cs` | Tenant-scoped read/write of `IssueType.Class`. |
| `src/JiraUnpacked.Api/Contracts/Mappings/IssueTypeClassesRequest.cs` | PUT request DTO. |
| `src/JiraUnpacked.Api/Controllers/IssueTypesController.cs` | Connection-scoped config routes. |
| `src/JiraUnpacked.Domain/Abstractions/GapCheckViews.cs` | API DTOs + `IGapCheckService`. |
| `src/JiraUnpacked.Infrastructure/Reporting/GapChecks/GapCheckContext.cs` | `GapCheckIssue`, `GapCheckContext`, `GapCheckOutcome`, `IGapCheck`, violation helper. |
| `src/JiraUnpacked.Infrastructure/Reporting/GapChecks/OpenEpicAllChildrenDoneCheck.cs` | The FR-E3 flagship. |
| `src/JiraUnpacked.Infrastructure/Reporting/GapChecks/DoneEpicOpenChildrenCheck.cs` | The inverse check. |
| `src/JiraUnpacked.Infrastructure/Reporting/GapCheckRunner.cs` | One tenant-scoped load; ordering + pagination. |
| `src/JiraUnpacked.Api/Controllers/ChecksController.cs` | Catalog + run-one routes. |
| `tests/JiraUnpacked.Tests/Reporting/GapCheckTests.cs` | Check unit tests — **no database**. |
| `tests/JiraUnpacked.Tests/Integration/GapCheckTestData.cs` | Shared raw-SQL seeding fixture. |
| `tests/JiraUnpacked.Tests/Integration/*Tests.cs` | Config, endpoint, and regression tests. |

---

## Task 1 (GC1): Issue-type classification + config endpoints

**Files:**
- Create: `src/JiraUnpacked.Domain/Enums/IssueTypeClass.cs`
- Modify: `src/JiraUnpacked.Domain/Entities/IssueType.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueTypeConfiguration.cs`
- Create: `src/JiraUnpacked.Domain/Abstractions/IIssueTypeClassService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/IssueTypeClassService.cs`
- Create: `src/JiraUnpacked.Api/Contracts/Mappings/IssueTypeClassesRequest.cs`
- Create: `src/JiraUnpacked.Api/Controllers/IssueTypesController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Create: migration `…AddIssueTypeClass.cs` (generated)
- Create: `tests/JiraUnpacked.Tests/Integration/GapCheckTestData.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/IssueTypeClassTests.cs`

**Interfaces:**
- Consumes: existing `IssueType` entity (`Id`, `EndUserId`, `JiraConnectionId`, `JiraIssueTypeId`, `Name`); `AppDbContext.IssueTypes` / `.JiraConnections`.
- Produces: `IssueTypeClass`; `IssueType.Class`; `IIssueTypeClassService`; `GapCheckTestData` (reused by Tasks 3 and 4).

- [ ] **Step 1: Create the shared test fixture**

Create `tests/JiraUnpacked.Tests/Integration/GapCheckTestData.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/GapCheckTestData.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>Ids a seeded gap-check fixture hands back to the test.</summary>
internal sealed record CheckSeed(
    Guid Tenant, Guid ConnectionId, Guid ProjectId,
    Guid EpicTypeId, Guid StoryTypeId,
    Guid ToDoStatusId, Guid DoneStatusId);

/// <summary>
/// Shared auth + raw-SQL seeding for the FR-E3 gap-check tests. Seeding uses the superuser
/// connection (bypasses RLS) purely to build fixtures; every assertion reads through the API
/// as the owning tenant. No WireMock anywhere — gap-checks need zero Jira calls.
/// </summary>
internal static class GapCheckTestData
{
    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

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
    /// Seeds connection + project + two issue types ("Epic" and "Story", both Class='Other'
    /// initially) + two statuses ("To Do", "Done") + their StatusSemantics (Other / Done).
    /// Fresh ids every call, so parallel tests never collide.
    /// </summary>
    internal static async Task<CheckSeed> SeedAsync(Guid tenant)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var epicType = Guid.NewGuid();
        var storyType = Guid.NewGuid();
        var todo = Guid.NewGuid();
        var done = Guid.NewGuid();

        await using var conn = Conn();
        await conn.OpenAsync();

        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())",
            ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())",
            ("i", projId), ("t", tenant), ("c", connId));

        // Both types start unclassified ('Other'): tests opt in by classifying one as Epic.
        await Exec(conn, @"INSERT INTO ""IssueTypes""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraIssueTypeId"",""Name"",""Class"") VALUES (@i,@t,@c,'1','Epic','Other')",
            ("i", epicType), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""IssueTypes""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraIssueTypeId"",""Name"",""Class"") VALUES (@i,@t,@c,'2','Story','Other')",
            ("i", storyType), ("t", tenant), ("c", connId));

        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'1','To Do','new')",
            ("i", todo), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'2','Done','done')",
            ("i", done), ("t", tenant), ("c", connId));

        await Exec(conn, @"INSERT INTO ""StatusSemantics""(""Id"",""EndUserId"",""JiraProjectId"",""JiraStatusId"",""StatusName"",""Class"") VALUES (@i,@t,@p,'1','To Do','Other')",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""StatusSemantics""(""Id"",""EndUserId"",""JiraProjectId"",""JiraStatusId"",""StatusName"",""Class"") VALUES (@i,@t,@p,'2','Done','Done')",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));

        return new CheckSeed(tenant, connId, projId, epicType, storyType, todo, done);
    }

    /// <summary>Classifies an issue type directly in the DB (bypasses the API).</summary>
    internal static async Task SetIssueTypeClassAsync(CheckSeed seed, string jiraIssueTypeId, string cls)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"UPDATE ""IssueTypes"" SET ""Class"" = @c WHERE ""JiraConnectionId"" = @k AND ""JiraIssueTypeId"" = @i",
            ("c", cls), ("k", seed.ConnectionId), ("i", jiraIssueTypeId));
    }

    /// <summary>Repoints a status name to a different class — the FR-E3 "mapping edit" lever.</summary>
    internal static async Task SetStatusClassAsync(CheckSeed seed, string jiraStatusId, string cls)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"UPDATE ""StatusSemantics"" SET ""Class"" = @c WHERE ""JiraProjectId"" = @p AND ""JiraStatusId"" = @i",
            ("c", cls), ("p", seed.ProjectId), ("i", jiraStatusId));
    }

    internal static async Task SeedIssueAsync(
        CheckSeed seed, string jiraIssueId, string jiraKey, string? summary,
        Guid? issueTypeId, Guid? statusId, string? parentJiraIssueId)
    {
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""IssueTypeId"",""StatusId"",""ParentJiraIssueId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,@jid,@k,@s,@ty,@st,@par,now(),now(),now())",
            ("i", Guid.NewGuid()), ("t", seed.Tenant), ("p", seed.ProjectId), ("jid", jiraIssueId),
            ("k", jiraKey), ("s", summary), ("ty", issueTypeId), ("st", statusId), ("par", parentJiraIssueId));
    }
}
```

- [ ] **Step 2: Write the failing config tests**

Create `tests/JiraUnpacked.Tests/Integration/IssueTypeClassTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/IssueTypeClassTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class IssueTypeClassTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueTypeClassTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ItemDto(string JiraIssueTypeId, string Name, string Class);

    [Fact]
    public async Task Lists_issue_types_defaulting_to_other()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        var items = await (await client.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types"))
            .Content.ReadFromJsonAsync<List<ItemDto>>();

        Assert.Equal(2, items!.Count);
        Assert.All(items, i => Assert.Equal("Other", i.Class));
        Assert.Contains(items, i => i.Name == "Epic" && i.JiraIssueTypeId == "1");
    }

    [Fact]
    public async Task Put_sets_a_class_and_is_additive()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        // Classify type "1" as Epic.
        var first = await client.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
            new { issueTypes = new[] { new { jiraIssueTypeId = "1", @class = "Epic" } } });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // A second PUT that names ONLY type "2" must leave type "1" alone (additive, not replace-all).
        var second = await client.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
            new { issueTypes = new[] { new { jiraIssueTypeId = "2", @class = "Other" } } });
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var items = await (await client.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types"))
            .Content.ReadFromJsonAsync<List<ItemDto>>();
        Assert.Equal("Epic", Assert.Single(items!, i => i.JiraIssueTypeId == "1").Class);
        Assert.Equal("Other", Assert.Single(items!, i => i.JiraIssueTypeId == "2").Class);
    }

    [Fact]
    public async Task Unknown_issue_type_id_returns_400()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        var resp = await client.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
            new { issueTypes = new[] { new { jiraIssueTypeId = "does-not-exist", @class = "Epic" } } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Tenant_cannot_read_or_write_another_tenants_issue_types()
    {
        var (_, tenantA) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenantA);
        var (clientB, _) = await GapCheckTestData.AuthedTenantAsync(_factory);

        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
                new { issueTypes = new[] { new { jiraIssueTypeId = "1", @class = "Epic" } } })).StatusCode);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueTypeClassTests`
Expected: FAIL — the `Class` column and the routes do not exist yet.

- [ ] **Step 4: Add the enum, the property, and the column config**

Create `src/JiraUnpacked.Domain/Enums/IssueTypeClass.cs`:

```csharp
namespace JiraUnpacked.Domain.Enums;

/// <summary>Per-connection semantic classification of a Jira issue type.</summary>
public enum IssueTypeClass { Epic, Other }
```

In `src/JiraUnpacked.Domain/Entities/IssueType.cs`, add the `using` and the property:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;
public sealed class IssueType : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraIssueTypeId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public IssueTypeClass Class { get; set; }
}
```

In `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueTypeConfiguration.cs`, add immediately after the `Name` property line (mirroring `StatusSemanticConfiguration`):

```csharp
        b.Property(x => x.Class).IsRequired().HasConversion<string>().HasMaxLength(16);
```

- [ ] **Step 5: Generate the migration**

Run:

```bash
dotnet ef migrations add AddIssueTypeClass \
  --project src/JiraUnpacked.Infrastructure \
  --startup-project src/JiraUnpacked.Infrastructure
```

Expected: one migration under `src/JiraUnpacked.Infrastructure/Persistence/Migrations/` whose `Up` contains exactly **one** `AddColumn<string>` on `IssueTypes` named `Class`. **Open the generated file** and set its `defaultValue` to `"Other"` so existing rows are valid:

```csharp
            migrationBuilder.AddColumn<string>(
                name: "Class",
                table: "IssueTypes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Other");
```

Confirm there is nothing else in the migration — any extra operation means unintended drift; stop and investigate.

- [ ] **Step 6: Add the service contract**

Create `src/JiraUnpacked.Domain/Abstractions/IIssueTypeClassService.cs`:

```csharp
// src/JiraUnpacked.Domain/Abstractions/IIssueTypeClassService.cs
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Domain.Abstractions;

public sealed record IssueTypeClassInfo(string JiraIssueTypeId, string Name, string Class);

public sealed record IssueTypeClassInput(string JiraIssueTypeId, IssueTypeClass Class);

public enum SetIssueTypeClassesOutcome { Ok, ConnectionNotFound, UnknownIssueTypeId }

public sealed record SetIssueTypeClassesResult(
    SetIssueTypeClassesOutcome Outcome, string? UnknownJiraIssueTypeId);

/// <summary>
/// Reads and sets the per-connection issue-type classification used to identify epics.
/// Connection-scoped because IssueTypes are keyed by JiraConnectionId and shared across
/// every project on that connection.
/// </summary>
public interface IIssueTypeClassService
{
    /// <summary>Null when the connection is unknown or belongs to another tenant (=> 404).</summary>
    Task<IReadOnlyList<IssueTypeClassInfo>?> GetAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Additive: only the listed types change; unlisted types keep their current Class.</summary>
    Task<SetIssueTypeClassesResult> SetAsync(
        Guid connectionId, IReadOnlyList<IssueTypeClassInput> items, CancellationToken ct = default);
}
```

- [ ] **Step 7: Implement the service**

Create `src/JiraUnpacked.Infrastructure/Services/IssueTypeClassService.cs`:

```csharp
// src/JiraUnpacked.Infrastructure/Services/IssueTypeClassService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class IssueTypeClassService : IIssueTypeClassService
{
    private readonly AppDbContext _db;
    public IssueTypeClassService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<IssueTypeClassInfo>?> GetAsync(Guid connectionId, CancellationToken ct = default)
    {
        // Tenant-scoped existence check: another tenant's connection is filtered out => null => 404.
        if (!await _db.JiraConnections.AnyAsync(c => c.Id == connectionId, ct)) return null;

        // Materialise then map: the enum is stored via HasConversion<string>, so ToString()
        // is done in memory rather than pushed into SQL.
        var rows = await _db.IssueTypes
            .Where(t => t.JiraConnectionId == connectionId)
            .OrderBy(t => t.Name)
            .Select(t => new { t.JiraIssueTypeId, t.Name, t.Class })
            .ToListAsync(ct);

        return rows.Select(r => new IssueTypeClassInfo(r.JiraIssueTypeId, r.Name, r.Class.ToString())).ToList();
    }

    public async Task<SetIssueTypeClassesResult> SetAsync(
        Guid connectionId, IReadOnlyList<IssueTypeClassInput> items, CancellationToken ct = default)
    {
        if (!await _db.JiraConnections.AnyAsync(c => c.Id == connectionId, ct))
            return new SetIssueTypeClassesResult(SetIssueTypeClassesOutcome.ConnectionNotFound, null);

        var rows = await _db.IssueTypes.Where(t => t.JiraConnectionId == connectionId).ToListAsync(ct);
        var byJiraId = rows.ToDictionary(r => r.JiraIssueTypeId, StringComparer.Ordinal);

        // Validate the whole request before mutating anything: one unknown id rejects the batch.
        foreach (var item in items)
            if (!byJiraId.ContainsKey(item.JiraIssueTypeId))
                return new SetIssueTypeClassesResult(
                    SetIssueTypeClassesOutcome.UnknownIssueTypeId, item.JiraIssueTypeId);

        // Additive: types absent from the request keep whatever class they already have.
        foreach (var item in items) byJiraId[item.JiraIssueTypeId].Class = item.Class;

        await _db.SaveChangesAsync(ct);
        return new SetIssueTypeClassesResult(SetIssueTypeClassesOutcome.Ok, null);
    }
}
```

- [ ] **Step 8: Add the request contract and the controller**

Create `src/JiraUnpacked.Api/Contracts/Mappings/IssueTypeClassesRequest.cs`:

```csharp
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Api.Contracts.Mappings;

public sealed record IssueTypeClassItem(string JiraIssueTypeId, IssueTypeClass Class);
public sealed record IssueTypeClassesRequest(IReadOnlyList<IssueTypeClassItem> IssueTypes);
```

Create `src/JiraUnpacked.Api/Controllers/IssueTypesController.cs`:

```csharp
// src/JiraUnpacked.Api/Controllers/IssueTypesController.cs
using JiraUnpacked.Api.Contracts.Mappings;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

/// <summary>
/// Issue-type classification is CONNECTION-scoped, unlike the project-scoped mapping routes:
/// IssueTypes are keyed by JiraConnectionId and shared by every project on that connection,
/// so a project-scoped route would misrepresent the blast radius.
/// </summary>
[ApiController]
[Authorize]
public sealed class IssueTypesController : ControllerBase
{
    private readonly IIssueTypeClassService _issueTypes;
    public IssueTypesController(IIssueTypeClassService issueTypes) => _issueTypes = issueTypes;

    [HttpGet("api/connections/{connectionId:guid}/issue-types")]
    public async Task<IActionResult> Get(Guid connectionId, CancellationToken ct = default)
    {
        var items = await _issueTypes.GetAsync(connectionId, ct);
        return items is null ? NotFound() : Ok(items);
    }

    [HttpPut("api/connections/{connectionId:guid}/issue-types")]
    public async Task<IActionResult> Put(
        Guid connectionId, IssueTypeClassesRequest request, CancellationToken ct = default)
    {
        var items = request.IssueTypes
            .Select(i => new IssueTypeClassInput(i.JiraIssueTypeId, i.Class)).ToList();

        var result = await _issueTypes.SetAsync(connectionId, items, ct);
        return result.Outcome switch
        {
            SetIssueTypeClassesOutcome.ConnectionNotFound => NotFound(),
            SetIssueTypeClassesOutcome.UnknownIssueTypeId =>
                BadRequest(new { error = $"Unknown jiraIssueTypeId: {result.UnknownJiraIssueTypeId}" }),
            _ => NoContent(),
        };
    }
}
```

- [ ] **Step 9: Register the service**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, next to the other `AddScoped` service registrations, add:

```csharp
        services.AddScoped<IIssueTypeClassService, JiraUnpacked.Infrastructure.Services.IssueTypeClassService>();
```

(If a `using JiraUnpacked.Infrastructure.Services;` is already present in the file, use the short name `IssueTypeClassService` instead.)

- [ ] **Step 10: Run tests, check drift, commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueTypeClassTests` → **4 passing**.
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → `No changes have been made to the model since the last migration.`
Run: `dotnet build JiraUnpacked.sln --nologo` → 0 warnings / 0 errors.

```bash
git add src/JiraUnpacked.Domain/Enums/IssueTypeClass.cs \
        src/JiraUnpacked.Domain/Entities/IssueType.cs \
        src/JiraUnpacked.Domain/Abstractions/IIssueTypeClassService.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueTypeConfiguration.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        src/JiraUnpacked.Infrastructure/Services/IssueTypeClassService.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        src/JiraUnpacked.Api/Contracts/Mappings/IssueTypeClassesRequest.cs \
        src/JiraUnpacked.Api/Controllers/IssueTypesController.cs \
        tests/JiraUnpacked.Tests/Integration/GapCheckTestData.cs \
        tests/JiraUnpacked.Tests/Integration/IssueTypeClassTests.cs
git commit -m "feat: issue-type classification for epic identification (Module E, FR-E3)"
```

---

## Task 2 (GC2): Check model + both checks (pure, no database)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/GapCheckViews.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/GapChecks/GapCheckContext.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/GapChecks/OpenEpicAllChildrenDoneCheck.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/GapChecks/DoneEpicOpenChildrenCheck.cs`
- Test: `tests/JiraUnpacked.Tests/Reporting/GapCheckTests.cs`

**Interfaces:**
- Consumes: `PagedResult<T>` from `src/JiraUnpacked.Domain/Abstractions/IssueViews.cs` (Spec 1); `StatusClass { Active, Done, Other }` from `JiraUnpacked.Domain.Enums`.
- Produces: `GapCheckInfo`, `GapCheckStatus`, `GapCheckChild`, `GapCheckViolation`, `GapCheckResult`, `IGapCheckService`, `GapCheckIssue`, `GapCheckContext`, `GapCheckOutcome`, `IGapCheck`, and both check classes — all consumed by Task 3.

Everything in this task is pure: no EF, no clock, no I/O. The tests need **no database**.

- [ ] **Step 1: Write the failing unit tests**

Create `tests/JiraUnpacked.Tests/Reporting/GapCheckTests.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Reporting.GapChecks;

namespace JiraUnpacked.Tests.Reporting;

public sealed class GapCheckTests
{
    private static GapCheckIssue Epic(string id, string key, StatusClass cls) =>
        new(id, key, "epic", cls.ToString(), cls, "Epic", true, null);

    private static GapCheckIssue Child(string id, string key, StatusClass cls, string parent) =>
        new(id, key, "child", cls.ToString(), cls, "Story", false, parent);

    private static GapCheckContext Ctx(params GapCheckIssue[] issues) => new(issues, hasEpicTypeConfigured: true);

    // ---- flagship: open epic, all children done ----

    [Fact]
    public void Flagship_flags_open_epic_whose_children_are_all_done()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Active),
            Child("2", "ACME-2", StatusClass.Done, "1"),
            Child("3", "ACME-3", StatusClass.Done, "1"));

        var outcome = new OpenEpicAllChildrenDoneCheck().Run(ctx);

        Assert.Equal(GapCheckStatus.Ok, outcome.Status);
        var v = Assert.Single(outcome.Violations);
        Assert.Equal("ACME-1", v.JiraKey);
        Assert.Equal(2, v.Children.Count);          // evidence: all children
    }

    [Fact]
    public void Flagship_does_not_flag_a_childless_epic()
    {
        // "All children are done" is vacuously true with zero children; without the
        // at-least-one guard every childless epic would be reported.
        var outcome = new OpenEpicAllChildrenDoneCheck().Run(Ctx(Epic("1", "ACME-1", StatusClass.Active)));

        Assert.Equal(GapCheckStatus.Ok, outcome.Status);
        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public void Flagship_does_not_flag_when_a_child_is_still_open()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Active),
            Child("2", "ACME-2", StatusClass.Done, "1"),
            Child("3", "ACME-3", StatusClass.Active, "1"));

        Assert.Empty(new OpenEpicAllChildrenDoneCheck().Run(ctx).Violations);
    }

    [Fact]
    public void Flagship_does_not_flag_an_epic_that_is_already_done()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Done),
            Child("2", "ACME-2", StatusClass.Done, "1"));

        Assert.Empty(new OpenEpicAllChildrenDoneCheck().Run(ctx).Violations);
    }

    // ---- inverse: done epic, open children ----

    [Fact]
    public void Inverse_flags_done_epic_and_lists_only_the_open_children()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Done),
            Child("2", "ACME-2", StatusClass.Done, "1"),
            Child("3", "ACME-3", StatusClass.Active, "1"));

        var outcome = new DoneEpicOpenChildrenCheck().Run(ctx);

        var v = Assert.Single(outcome.Violations);
        Assert.Equal("ACME-1", v.JiraKey);
        var child = Assert.Single(v.Children);       // evidence: ONLY the open child
        Assert.Equal("ACME-3", child.JiraKey);
    }

    [Fact]
    public void Inverse_does_not_flag_when_every_child_is_done()
    {
        var ctx = Ctx(
            Epic("1", "ACME-1", StatusClass.Done),
            Child("2", "ACME-2", StatusClass.Done, "1"));

        Assert.Empty(new DoneEpicOpenChildrenCheck().Run(ctx).Violations);
    }

    // ---- shared behaviour ----

    [Fact]
    public void Both_checks_report_not_configured_when_no_epic_type_is_set()
    {
        var ctx = new GapCheckContext(
            new[] { Epic("1", "ACME-1", StatusClass.Active), Child("2", "ACME-2", StatusClass.Done, "1") },
            hasEpicTypeConfigured: false);

        foreach (var check in new IGapCheck[] { new OpenEpicAllChildrenDoneCheck(), new DoneEpicOpenChildrenCheck() })
        {
            var outcome = check.Run(ctx);
            Assert.Equal(GapCheckStatus.NotConfigured, outcome.Status);
            Assert.Empty(outcome.Violations);
        }
    }

    [Fact]
    public void Non_epic_parents_are_ignored_even_when_their_children_are_all_done()
    {
        var parent = new GapCheckIssue("1", "ACME-1", "story", "Active", StatusClass.Active, "Story", false, null);
        var ctx = Ctx(parent, Child("2", "ACME-2", StatusClass.Done, "1"));

        Assert.Empty(new OpenEpicAllChildrenDoneCheck().Run(ctx).Violations);
    }

    [Fact]
    public void Check_ids_are_stable()
    {
        Assert.Equal("open-epic-all-children-done", new OpenEpicAllChildrenDoneCheck().Info.Id);
        Assert.Equal("done-epic-open-children", new DoneEpicOpenChildrenCheck().Info.Id);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~GapCheckTests`
Expected: **compile error** — the gap-check types do not exist.

- [ ] **Step 3: Create the API DTOs and the service contract**

Create `src/JiraUnpacked.Domain/Abstractions/GapCheckViews.cs`:

```csharp
// src/JiraUnpacked.Domain/Abstractions/GapCheckViews.cs
namespace JiraUnpacked.Domain.Abstractions;

public sealed record GapCheckInfo(string Id, string Name, string Description);

/// <summary>Ok = the check ran. NotConfigured = it could not run (no issue type classified Epic),
/// which must never be confused with "no problems found".</summary>
public enum GapCheckStatus { Ok, NotConfigured }

public sealed record GapCheckChild(
    string JiraIssueId, string JiraKey, string? Summary, string? Status, string Class);

public sealed record GapCheckViolation(
    string JiraIssueId, string JiraKey, string? Summary,
    string? Status, string Class, string? IssueType,
    IReadOnlyList<GapCheckChild> Children);

public sealed record GapCheckResult(
    GapCheckInfo Check, string Status, PagedResult<GapCheckViolation> Violations);

public interface IGapCheckService
{
    /// <summary>Null when the project is unknown or belongs to another tenant (=> 404).</summary>
    Task<IReadOnlyList<GapCheckInfo>?> GetCatalogAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Null when the project is unknown/cross-tenant OR the checkId is unknown (=> 404).</summary>
    Task<GapCheckResult?> RunAsync(
        Guid projectId, string checkId, int page, int pageSize, CancellationToken ct = default);
}
```

- [ ] **Step 4: Create the check abstraction**

Create `src/JiraUnpacked.Infrastructure/Reporting/GapChecks/GapCheckContext.cs`:

```csharp
// src/JiraUnpacked.Infrastructure/Reporting/GapChecks/GapCheckContext.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Infrastructure.Reporting.GapChecks;

/// <summary>One project issue, already classified. Current status only — gap-checks never
/// read change history.</summary>
public sealed record GapCheckIssue(
    string JiraIssueId, string JiraKey, string? Summary,
    string? Status, StatusClass Class, string? IssueType, bool IsEpicType, string? ParentJiraIssueId);

/// <summary>
/// Everything the checks need, loaded once per request by GapCheckRunner. Children are grouped
/// in memory into a single lookup, so parent-to-children resolution costs one pass and never
/// issues a query per parent.
/// </summary>
public sealed class GapCheckContext
{
    public GapCheckContext(IReadOnlyList<GapCheckIssue> issues, bool hasEpicTypeConfigured)
    {
        Issues = issues;
        HasEpicTypeConfigured = hasEpicTypeConfigured;
        ChildrenByParent = issues
            .Where(i => i.ParentJiraIssueId is not null)
            .ToLookup(i => i.ParentJiraIssueId!, StringComparer.Ordinal);
    }

    public IReadOnlyList<GapCheckIssue> Issues { get; }
    public ILookup<string, GapCheckIssue> ChildrenByParent { get; }
    public bool HasEpicTypeConfigured { get; }
}

public sealed record GapCheckOutcome(GapCheckStatus Status, IReadOnlyList<GapCheckViolation> Violations);

/// <summary>A curated correctness check. Pure and synchronous: the runner has already loaded
/// everything, so a check performs no I/O and is unit-testable without a database.</summary>
public interface IGapCheck
{
    GapCheckInfo Info { get; }
    GapCheckOutcome Run(GapCheckContext context);
}

internal static class GapCheckViolations
{
    /// <summary>Builds a violation for an issue, carrying the children that justify the finding.</summary>
    internal static GapCheckViolation From(GapCheckIssue issue, IReadOnlyList<GapCheckIssue> children) =>
        new(issue.JiraIssueId, issue.JiraKey, issue.Summary, issue.Status, issue.Class.ToString(), issue.IssueType,
            children
                .Select(c => new GapCheckChild(c.JiraIssueId, c.JiraKey, c.Summary, c.Status, c.Class.ToString()))
                .ToList());

    internal static GapCheckOutcome NotConfigured() =>
        new(GapCheckStatus.NotConfigured, Array.Empty<GapCheckViolation>());
}
```

- [ ] **Step 5: Implement the flagship check**

Create `src/JiraUnpacked.Infrastructure/Reporting/GapChecks/OpenEpicAllChildrenDoneCheck.cs`:

```csharp
// src/JiraUnpacked.Infrastructure/Reporting/GapChecks/OpenEpicAllChildrenDoneCheck.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Infrastructure.Reporting.GapChecks;

/// <summary>The FR-E3 flagship: epics that are not Done even though every child is Done.</summary>
public sealed class OpenEpicAllChildrenDoneCheck : IGapCheck
{
    public GapCheckInfo Info { get; } = new(
        "open-epic-all-children-done",
        "Open epics whose children are all done",
        "Epics that are not Done even though every one of their child issues is Done.");

    public GapCheckOutcome Run(GapCheckContext context)
    {
        if (!context.HasEpicTypeConfigured) return GapCheckViolations.NotConfigured();

        var violations = new List<GapCheckViolation>();
        foreach (var epic in context.Issues)
        {
            if (!epic.IsEpicType || epic.Class == StatusClass.Done) continue;

            var children = context.ChildrenByParent[epic.JiraIssueId].ToList();

            // At least one child is required: "all children are done" is vacuously true for a
            // childless epic, which would flag every one of them.
            if (children.Count == 0) continue;
            if (!children.All(c => c.Class == StatusClass.Done)) continue;

            violations.Add(GapCheckViolations.From(epic, children));
        }

        return new GapCheckOutcome(GapCheckStatus.Ok, violations);
    }
}
```

- [ ] **Step 6: Implement the inverse check**

Create `src/JiraUnpacked.Infrastructure/Reporting/GapChecks/DoneEpicOpenChildrenCheck.cs`:

```csharp
// src/JiraUnpacked.Infrastructure/Reporting/GapChecks/DoneEpicOpenChildrenCheck.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Infrastructure.Reporting.GapChecks;

/// <summary>The inverse gap: epics marked Done that still have unfinished children.</summary>
public sealed class DoneEpicOpenChildrenCheck : IGapCheck
{
    public GapCheckInfo Info { get; } = new(
        "done-epic-open-children",
        "Done epics with open children",
        "Epics marked Done that still have child issues which are not Done.");

    public GapCheckOutcome Run(GapCheckContext context)
    {
        if (!context.HasEpicTypeConfigured) return GapCheckViolations.NotConfigured();

        var violations = new List<GapCheckViolation>();
        foreach (var epic in context.Issues)
        {
            if (!epic.IsEpicType || epic.Class != StatusClass.Done) continue;

            // Evidence is only the children that are NOT done — those are what needs attention.
            var open = context.ChildrenByParent[epic.JiraIssueId]
                .Where(c => c.Class != StatusClass.Done)
                .ToList();

            if (open.Count == 0) continue;

            violations.Add(GapCheckViolations.From(epic, open));
        }

        return new GapCheckOutcome(GapCheckStatus.Ok, violations);
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~GapCheckTests` → **9 passing**.
Run: `dotnet build JiraUnpacked.sln --nologo` → 0 warnings / 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/GapCheckViews.cs \
        src/JiraUnpacked.Infrastructure/Reporting/GapChecks/ \
        tests/JiraUnpacked.Tests/Reporting/GapCheckTests.cs
git commit -m "feat: gap-check model + flagship and inverse checks (Module E, FR-E3)"
```

---

## Task 3 (GC3): Runner + catalog/run endpoints

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/GapCheckRunner.cs`
- Create: `src/JiraUnpacked.Api/Controllers/ChecksController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/GapCheckEndpointTests.cs`

**Interfaces:**
- Consumes (Task 2): `IGapCheckService`, `GapCheckInfo`, `GapCheckResult`, `GapCheckViolation`, `GapCheckIssue`, `GapCheckContext`, `IGapCheck`, both check classes. (Task 1): `IssueTypeClass`, `IssueType.Class`, `GapCheckTestData`.
- Also consumes: `StatusTimelineEngine.Classify(string?, IReadOnlyDictionary<string, StatusClass>)` — the **pure** classifier from Spec 2, in `JiraUnpacked.Infrastructure.Reporting`. Do **not** call `StatusTimelineEngine.Build`.
- Produces: nothing later tasks depend on except the live endpoints Task 4 tests.

- [ ] **Step 1: Write the failing endpoint tests**

Create `tests/JiraUnpacked.Tests/Integration/GapCheckEndpointTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/GapCheckEndpointTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

public sealed class GapCheckEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public GapCheckEndpointTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record InfoDto(string Id, string Name, string Description);
    private sealed record ChildDto(string JiraIssueId, string JiraKey, string? Summary, string? Status, string Class);
    private sealed record ViolationDto(string JiraIssueId, string JiraKey, string? Summary,
        string? Status, string Class, string? IssueType, List<ChildDto> Children);
    private sealed record PageDto(List<ViolationDto> Items, int Page, int PageSize, int TotalCount);
    private sealed record ResultDto(InfoDto Check, string Status, PageDto Violations);

    [Fact]
    public async Task Catalog_lists_both_checks()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        var checks = await (await client.GetAsync($"/api/projects/{seed.ProjectId}/checks"))
            .Content.ReadFromJsonAsync<List<InfoDto>>();

        Assert.Equal(2, checks!.Count);
        Assert.Contains(checks, c => c.Id == "open-epic-all-children-done");
        Assert.Contains(checks, c => c.Id == "done-epic-open-children");
    }

    [Fact]
    public async Task Flagship_reports_the_open_epic_whose_children_are_all_done()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        // Epic ACME-1 is To Do (Other); both children are Done => violation.
        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);
        await GapCheckTestData.SeedIssueAsync(seed, "10002", "ACME-2", "a", seed.StoryTypeId, seed.DoneStatusId, "10001");
        await GapCheckTestData.SeedIssueAsync(seed, "10003", "ACME-3", "b", seed.StoryTypeId, seed.DoneStatusId, "10001");
        // A childless epic must NOT be reported.
        await GapCheckTestData.SeedIssueAsync(seed, "10004", "ACME-4", "lonely", seed.EpicTypeId, seed.ToDoStatusId, null);

        var r = await (await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done"))
            .Content.ReadFromJsonAsync<ResultDto>();

        Assert.Equal("Ok", r!.Status);
        Assert.Equal(1, r.Violations.TotalCount);
        var v = Assert.Single(r.Violations.Items);
        Assert.Equal("ACME-1", v.JiraKey);
        Assert.Equal("Epic", v.IssueType);
        Assert.Equal(2, v.Children.Count);
    }

    [Fact]
    public async Task Inverse_reports_the_done_epic_with_an_open_child()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.DoneStatusId, null);
        await GapCheckTestData.SeedIssueAsync(seed, "10002", "ACME-2", "done", seed.StoryTypeId, seed.DoneStatusId, "10001");
        await GapCheckTestData.SeedIssueAsync(seed, "10003", "ACME-3", "open", seed.StoryTypeId, seed.ToDoStatusId, "10001");

        var r = await (await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/done-epic-open-children"))
            .Content.ReadFromJsonAsync<ResultDto>();

        var v = Assert.Single(r!.Violations.Items);
        Assert.Equal("ACME-1", v.JiraKey);
        var child = Assert.Single(v.Children);   // only the open child is evidence
        Assert.Equal("ACME-3", child.JiraKey);
    }

    [Fact]
    public async Task Reports_not_configured_when_no_issue_type_is_an_epic()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);   // nothing classified Epic
        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);
        await GapCheckTestData.SeedIssueAsync(seed, "10002", "ACME-2", "a", seed.StoryTypeId, seed.DoneStatusId, "10001");

        var r = await (await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done"))
            .Content.ReadFromJsonAsync<ResultDto>();

        Assert.Equal("NotConfigured", r!.Status);          // distinguishable from a clean run
        Assert.Equal(0, r.Violations.TotalCount);
        Assert.Empty(r.Violations.Items);
    }

    [Fact]
    public async Task Empty_project_returns_200_with_no_violations()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        var resp = await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);    // 200, NOT 404

        var r = await resp.Content.ReadFromJsonAsync<ResultDto>();
        Assert.Equal("Ok", r!.Status);
        Assert.Equal(0, r.Violations.TotalCount);
    }

    [Fact]
    public async Task Unknown_check_id_returns_404()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        var resp = await client.GetAsync($"/api/projects/{seed.ProjectId}/checks/no-such-check");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Pagination_params_are_clamped()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        // Two violating epics, each with a single done child.
        for (var n = 1; n <= 2; n++)
        {
            await GapCheckTestData.SeedIssueAsync(seed, $"1000{n}", $"ACME-{n}", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);
            await GapCheckTestData.SeedIssueAsync(seed, $"2000{n}", $"KID-{n}", "child", seed.StoryTypeId, seed.DoneStatusId, $"1000{n}");
        }

        // page < 1 floors to 1; pageSize > 100 caps at 100 (the response echoes the clamped values).
        var r = await (await client.GetAsync(
                $"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done?page=0&pageSize=500"))
            .Content.ReadFromJsonAsync<ResultDto>();

        Assert.Equal(1, r!.Violations.Page);
        Assert.Equal(100, r.Violations.PageSize);
        Assert.Equal(2, r.Violations.TotalCount);
        Assert.Equal(2, r.Violations.Items.Count);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~GapCheckEndpointTests`
Expected: FAIL — the `/checks` routes do not exist (404 / deserialisation failure).

- [ ] **Step 3: Implement the runner**

Create `src/JiraUnpacked.Infrastructure/Reporting/GapCheckRunner.cs`:

```csharp
// src/JiraUnpacked.Infrastructure/Reporting/GapCheckRunner.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.GapChecks;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// Runs the curated FR-E3 checks. Performs ONE tenant-scoped load per request and hands the
/// resulting context to a pure check, then owns ordering and pagination so a new check never
/// has to reimplement them. Reads current status only — no change history.
/// </summary>
public sealed class GapCheckRunner : IGapCheckService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    private readonly AppDbContext _db;
    private readonly IReadOnlyList<IGapCheck> _checks;

    public GapCheckRunner(AppDbContext db, IEnumerable<IGapCheck> checks)
    {
        _db = db;
        _checks = checks.ToList();
    }

    public async Task<IReadOnlyList<GapCheckInfo>?> GetCatalogAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        return _checks.Select(c => c.Info).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<GapCheckResult?> RunAsync(
        Guid projectId, string checkId, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;

        var check = _checks.FirstOrDefault(
            c => string.Equals(c.Info.Id, checkId, StringComparison.OrdinalIgnoreCase));
        if (check is null) return null;   // unknown checkId => 404

        var context = await LoadContextAsync(projectId, ct);
        var outcome = check.Run(context);

        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var ordered = outcome.Violations.OrderBy(v => v.JiraKey, StringComparer.Ordinal).ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new GapCheckResult(
            check.Info,
            outcome.Status.ToString(),
            new PagedResult<GapCheckViolation>(items, page, pageSize, ordered.Count));
    }

    /// <summary>Tenant-scoped existence check: a cross-tenant/unknown project is filtered out => 404.</summary>
    private Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct) =>
        _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct);

    private async Task<GapCheckContext> LoadContextAsync(Guid projectId, CancellationToken ct)
    {
        // Case-insensitive by contract: StatusTimelineEngine.Classify relies on the caller's comparer.
        var semanticRows = await _db.StatusSemantics
            .Where(s => s.JiraProjectId == projectId)
            .Select(s => new { s.StatusName, s.Class })
            .ToListAsync(ct);

        var semantics = new Dictionary<string, StatusClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in semanticRows) semantics[r.StatusName] = r.Class;

        var rows = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Select(i => new
            {
                i.JiraIssueId,
                i.JiraKey,
                i.Summary,
                i.ParentJiraIssueId,
                Status = _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                IssueType = _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault(),
                IsEpicType = _db.IssueTypes.Any(t => t.Id == i.IssueTypeId && t.Class == IssueTypeClass.Epic),
            })
            .ToListAsync(ct);

        var issues = rows
            .Select(r => new GapCheckIssue(
                r.JiraIssueId, r.JiraKey, r.Summary, r.Status,
                StatusTimelineEngine.Classify(r.Status, semantics),
                r.IssueType, r.IsEpicType, r.ParentJiraIssueId))
            .ToList();

        // Epic classification lives on the connection that owns this project.
        var hasEpicType = await _db.IssueTypes.AnyAsync(
            t => t.Class == IssueTypeClass.Epic
                 && _db.JiraProjects.Any(p => p.Id == projectId && p.JiraConnectionId == t.JiraConnectionId),
            ct);

        return new GapCheckContext(issues, hasEpicType);
    }
}
```

- [ ] **Step 4: Add the controller**

Create `src/JiraUnpacked.Api/Controllers/ChecksController.cs`:

```csharp
// src/JiraUnpacked.Api/Controllers/ChecksController.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class ChecksController : ControllerBase
{
    private readonly IGapCheckService _checks;
    public ChecksController(IGapCheckService checks) => _checks = checks;

    [HttpGet("api/projects/{projectId:guid}/checks")]
    public async Task<IActionResult> Catalog(Guid projectId, CancellationToken ct = default)
    {
        var catalog = await _checks.GetCatalogAsync(projectId, ct);
        return catalog is null ? NotFound() : Ok(catalog);
    }

    [HttpGet("api/projects/{projectId:guid}/checks/{checkId}")]
    public async Task<IActionResult> Run(
        Guid projectId, string checkId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await _checks.RunAsync(projectId, checkId, page, pageSize, ct);
        return result is null ? NotFound() : Ok(result);
    }
}
```

- [ ] **Step 5: Register the runner and both checks**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, next to the other reporting registrations, add:

```csharp
        services.AddScoped<IGapCheck, JiraUnpacked.Infrastructure.Reporting.GapChecks.OpenEpicAllChildrenDoneCheck>();
        services.AddScoped<IGapCheck, JiraUnpacked.Infrastructure.Reporting.GapChecks.DoneEpicOpenChildrenCheck>();
        services.AddScoped<IGapCheckService, JiraUnpacked.Infrastructure.Reporting.GapCheckRunner>();
```

Add `using JiraUnpacked.Infrastructure.Reporting.GapChecks;` to the file's usings and shorten the names if the file's style uses short names for the other registrations.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~GapCheckEndpointTests` → **7 passing**.
Run: `dotnet build JiraUnpacked.sln --nologo` → 0 warnings / 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Reporting/GapCheckRunner.cs \
        src/JiraUnpacked.Api/Controllers/ChecksController.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Integration/GapCheckEndpointTests.cs
git commit -m "feat: gap-check runner + catalog/run endpoints (Module E, FR-E3)"
```

---

## Task 4 (GC4): Isolation + the two regression guarantees

**Files:**
- Test: `tests/JiraUnpacked.Tests/Integration/GapCheckGuaranteesTests.cs`

**Interfaces:**
- Consumes: all endpoints from Tasks 1 and 3; `GapCheckTestData` from Task 1.
- Produces: nothing.

This task proves the properties the spec exists for: **tenant isolation**, **classifications survive a sync**, and **re-classifying a status changes results with no Jira call**.

- [ ] **Step 1: Write the tests**

Create `tests/JiraUnpacked.Tests/Integration/GapCheckGuaranteesTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/GapCheckGuaranteesTests.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// The guarantees FR-E3 rests on: tenant isolation on the checks routes, epic classifications
/// surviving a re-derive, and a status re-classification changing results with zero Jira calls.
/// NOTE: this file starts no WireMock server — that absence IS the zero-Jira proof.
/// </summary>
public sealed class GapCheckGuaranteesTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public GapCheckGuaranteesTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ItemDto(string JiraIssueTypeId, string Name, string Class);
    private sealed record ChildDto(string JiraIssueId, string JiraKey, string? Summary, string? Status, string Class);
    private sealed record ViolationDto(string JiraIssueId, string JiraKey, string? Summary,
        string? Status, string Class, string? IssueType, List<ChildDto> Children);
    private sealed record PageDto(List<ViolationDto> Items, int Page, int PageSize, int TotalCount);
    private sealed record ResultDto(string Status, PageDto Violations);

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_checks()
    {
        var (_, tenantA) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenantA);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");
        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);

        var (clientB, _) = await GapCheckTestData.AuthedTenantAsync(_factory);

        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/projects/{seed.ProjectId}/checks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/projects/{seed.ProjectId}/checks/open-epic-all-children-done")).StatusCode);
    }

    [Fact]
    public async Task Unknown_project_returns_404_on_both_routes()
    {
        var (client, _) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var missing = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{missing}/checks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/projects/{missing}/checks/open-epic-all-children-done")).StatusCode);
    }

    [Fact]
    public async Task Epic_classification_survives_a_rederive()
    {
        // The derivation upserts IssueTypes by (JiraConnectionId, JiraIssueTypeId) and assigns only
        // Name on an existing row. If that ever changes, a sync would silently wipe the user's
        // epic classification and every parent-based check would go quiet.
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);

        await client.PutAsJsonAsync($"/api/connections/{seed.ConnectionId}/issue-types",
            new { issueTypes = new[] { new { jiraIssueTypeId = "1", @class = "Epic" } } });

        await client.PostAsJsonAsync($"/api/projects/{seed.ProjectId}/rederive", new { });

        var items = await (await client.GetAsync($"/api/connections/{seed.ConnectionId}/issue-types"))
            .Content.ReadFromJsonAsync<List<ItemDto>>();
        Assert.Equal("Epic", Assert.Single(items!, i => i.JiraIssueTypeId == "1").Class);
    }

    [Fact]
    public async Task Reclassifying_a_status_changes_the_result_without_touching_jira()
    {
        var (client, tenant) = await GapCheckTestData.AuthedTenantAsync(_factory);
        var seed = await GapCheckTestData.SeedAsync(tenant);
        await GapCheckTestData.SetIssueTypeClassAsync(seed, "1", "Epic");

        // Epic ACME-1 sits in "To Do" (Other); its only child sits in "Done" (Done).
        // => epic not Done + at least one child + every child Done => the flagship fires.
        await GapCheckTestData.SeedIssueAsync(seed, "10001", "ACME-1", "epic", seed.EpicTypeId, seed.ToDoStatusId, null);
        await GapCheckTestData.SeedIssueAsync(seed, "10002", "ACME-2", "child", seed.StoryTypeId, seed.DoneStatusId, "10001");

        Assert.Equal(1, (await RunFlagshipAsync(client, seed.ProjectId)).Violations.TotalCount);

        // Re-map the "Done" status to Other: the child stops counting as done, so the
        // violation disappears. No sync, no re-derive, no Jira call.
        await GapCheckTestData.SetStatusClassAsync(seed, "2", "Other");
        Assert.Equal(0, (await RunFlagshipAsync(client, seed.ProjectId)).Violations.TotalCount);

        // Map it back and the violation returns — proving the result tracks the mapping on
        // every request rather than being cached or baked in at derive time.
        await GapCheckTestData.SetStatusClassAsync(seed, "2", "Done");
        var restored = await RunFlagshipAsync(client, seed.ProjectId);
        Assert.Equal(1, restored.Violations.TotalCount);
        Assert.Equal("ACME-1", Assert.Single(restored.Violations.Items).JiraKey);
    }

    private static async Task<ResultDto> RunFlagshipAsync(HttpClient client, Guid projectId) =>
        (await (await client.GetAsync($"/api/projects/{projectId}/checks/open-epic-all-children-done"))
            .Content.ReadFromJsonAsync<ResultDto>())!;
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~GapCheckGuaranteesTests` → **4 passing**.

These assert behaviour that already exists after Tasks 1–3, so they pass on first run rather than going RED first — they are guarantee/regression tests, not new-feature TDD. If the **re-derive** or **mapping-change** test fails, that is a genuine defect: stop and report rather than working around it.

- [ ] **Step 3: Full verification**

Run: `dotnet test JiraUnpacked.sln` → full suite green; report the actual count (expect 178 + 4 + 9 + 7 + 4 = 202).
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → `No changes…`.
Run: `dotnet build JiraUnpacked.sln -c Release --nologo` → 0 warnings / 0 errors.

- [ ] **Step 4: Commit**

```bash
git add tests/JiraUnpacked.Tests/Integration/GapCheckGuaranteesTests.cs
git commit -m "test: gap-check isolation + classification-survival + mapping-change guarantees (Module E, FR-E3)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green (report the count).
- [ ] `dotnet ef migrations has-pending-model-changes …` → "No changes".
- [ ] Grep `src/JiraUnpacked.Infrastructure/Reporting/` and the new controllers for `IgnoreQueryFilters`, `FromSql`, `IPrivilegedDbContextFactory`, `.Include(` → no matches.
- [ ] Confirm the FR-B2 gate test file is unmodified on this branch (`git log develop..HEAD -- tests/JiraUnpacked.Tests/Tenancy/` is empty) and green.
- [ ] Confirm the branch adds exactly one migration and **no** new index.
- [ ] Push `feat/report-checks`; open PR → `develop`; CI green; squash-merge; then the `develop → main` release (merge-commit). Before the release, run `git diff --numstat origin/develop origin/main` — a non-zero deletion count means a follow-up landed on main and must be back-merged first (release merge commits alone are expected and are not a reason to back-merge).

## Notes for execution

- **Read-only + tenant-scoped.** Every production query goes through the injected `AppDbContext`. Tests seed via raw SQL, but every assertion reads through the API as the owning tenant.
- **Checks are pure.** Only `GapCheckRunner` touches the database; a check receives a fully-loaded `GapCheckContext` and performs no I/O. Keep it that way — it is what makes the unit tests DB-free.
- **Never call `StatusTimelineEngine.Build`** here; gap-checks use only the pure `Classify`, and never read `IssueChangeHistory`.
- The `PUT issue-types` endpoint is **additive** on purpose — unlike the existing `PUT status-semantics`, which replaces. Do not "fix" it to match.
