# Module E Spec 1 — View loaded data (FR-E1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an End User browse a project's ingested issues — a paginated, tenant-scoped list with resolved dictionary names, and a single-issue detail with its change history and epic→story hierarchy.

**Architecture:** A read-only `IIssueQueryService` over the tenant-scoped `AppDbContext` resolves dictionary FK names via correlated-subquery projections (no entity navigation properties, no schema change beyond one ordering index). Two `[Authorize]` endpoints on a new `IssuesController`. Offset pagination.

**Tech Stack:** ASP.NET Core 8, EF Core 8 + Npgsql (pinned 8.0.11), xUnit.

## Global Constraints

- Target **net8.0**; EF Core + Npgsql pinned **8.0.11**.
- **Read-only**; no new tenant tables. The only schema change is one index on `Issues` (`EndUserId, JiraProjectId, JiraUpdatedAt`).
- Every read is tenant-scoped through the injected `AppDbContext` (EF global query filter + Postgres RLS). **No `IgnoreQueryFilters` in production code**, no privileged factory.
- Offset pagination: `page` (1-based, clamp `>= 1`) + `pageSize` (default 50, clamp `[1, 100]`); order `JiraUpdatedAt DESC, JiraKey ASC`.
- Unknown/cross-tenant project or issue → **404** (no existence leak). Empty project (exists, no issues) → **200** with an empty page.
- Dictionary FK values are resolved **names** (`null` when the FK is null or the dict row is absent).
- Endpoints are `[Authorize]`; the tenant is the JWT `endUserId` claim (= the user's id).
- Integration tests seed via **raw SQL** (privileged `TestConnectionString`, superuser bypasses RLS) with `EndUserId` = the logged-in user's id, scoped to the test's own unique `JiraProjectId` (parallel tests share the DB and reuse fixed Jira ids — always filter reads by the test's project).
- EF migration commands: both `--project` and `--startup-project` = `src/JiraUnpacked.Infrastructure`.
- One commit per task. Controller pushes before PR.

**Namespaces:** view records in `JiraUnpacked.Domain.Abstractions`; service impl in `JiraUnpacked.Infrastructure.Reporting`; controller in `JiraUnpacked.Api.Controllers`.

---

## Task 1 (RV1): Issue list endpoint + view records + ordering index

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IssueViews.cs` (records)
- Create: `src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs`
- Create: `src/JiraUnpacked.Api/Controllers/IssuesController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueConfiguration.cs` (add index)
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` (register service)
- Create (generated): `src/JiraUnpacked.Infrastructure/Persistence/Migrations/<ts>_AddIssueUpdatedIndex.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/IssueListTests.cs`

**Interfaces:**
- Produces: `record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)`; `record IssueListItem(string JiraIssueId, string JiraKey, string? Summary, string? Status, string? IssueType, string? Priority, string? Assignee, string? Sprint, string? Team, decimal? StoryPoints, string? ParentJiraIssueId, DateTime JiraCreatedAt, DateTime JiraUpdatedAt)`; `IIssueQueryService.ListAsync(Guid projectId, int page, int pageSize, CancellationToken)` → `Task<PagedResult<IssueListItem>?>` (null = project not found).

- [ ] **Step 1: Write the failing integration test**

```csharp
// tests/JiraUnpacked.Tests/Integration/IssueListTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class IssueListTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueListTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
    private sealed record ItemDto(string JiraIssueId, string JiraKey, string? Summary, string? Status, string? IssueType,
        string? Priority, string? Assignee, string? Sprint, string? Team, decimal? StoryPoints, string? ParentJiraIssueId,
        DateTime JiraCreatedAt, DateTime JiraUpdatedAt);
    private sealed record PageDto(List<ItemDto> Items, int Page, int PageSize, int TotalCount);

    // Creates an Active EndUser, returns (authed client, tenantId=userId). The tenant EndUserId == user.Id.
    private async Task<(HttpClient client, Guid tenantId)> AuthedTenantAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User { Id = userId, Email = $"u-{unique}@x.com", Username = $"u-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return (c, userId);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    // Seeds connection + project + a few dicts + two issues (epic ACME-1, story ACME-2 w/ parent) via raw SQL
    // (superuser conn bypasses RLS). Returns the projectId.
    private static async Task<Guid> SeedProjectWithIssuesAsync(Guid tenant)
    {
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        var statusId = Guid.NewGuid(); var typeId = Guid.NewGuid(); var userDictId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'10','In Progress','indeterminate')", ("i", statusId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""IssueTypes""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraIssueTypeId"",""Name"") VALUES (@i,@t,@c,'1','Story')", ("i", typeId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""JiraUsers""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraAccountId"",""DisplayName"") VALUES (@i,@t,@c,'u1','Alice')", ("i", userDictId), ("t", tenant), ("c", connId));
        // epic ACME-1 (older update), story ACME-2 (newer update, parent 10001, resolved dict FKs)
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10001','ACME-1','Epic',timestamptz '2024-01-01 00:00+00',timestamptz '2024-01-01 00:00+00',now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""IssueTypeId"",""AssigneeId"",""StoryPoints"",""ParentJiraIssueId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10002','ACME-2','Story',@s,@ty,@u,3,'10001',timestamptz '2024-01-02 00:00+00',timestamptz '2024-01-03 00:00+00',now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("s", statusId), ("ty", typeId), ("u", userDictId));
        return projId;
    }

    [Fact]
    public async Task Lists_issues_paginated_with_resolved_names_newest_first()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedProjectWithIssuesAsync(tenant);

        var page1 = await (await client.GetAsync($"/api/projects/{projectId}/issues?page=1&pageSize=1")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal(2, page1!.TotalCount);
        Assert.Single(page1.Items);
        Assert.Equal("ACME-2", page1.Items[0].JiraKey);      // newest JiraUpdatedAt first
        Assert.Equal("In Progress", page1.Items[0].Status);  // resolved dict name
        Assert.Equal("Story", page1.Items[0].IssueType);
        Assert.Equal("Alice", page1.Items[0].Assignee);
        Assert.Equal(3m, page1.Items[0].StoryPoints);
        Assert.Equal("10001", page1.Items[0].ParentJiraIssueId);

        var page2 = await (await client.GetAsync($"/api/projects/{projectId}/issues?page=2&pageSize=1")).Content.ReadFromJsonAsync<PageDto>();
        Assert.Equal("ACME-1", page2!.Items[0].JiraKey);
        Assert.Null(page2.Items[0].Status); // epic had no status FK -> null name
    }

    [Fact]
    public async Task Unknown_project_returns_404()
    {
        var (client, _) = await AuthedTenantAsync();
        var resp = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/issues");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Tenant_cannot_list_another_tenants_issues()
    {
        var (_, tenantA) = await AuthedTenantAsync();
        var projectId = await SeedProjectWithIssuesAsync(tenantA);
        var (clientB, _) = await AuthedTenantAsync();
        // B asks for A's project id -> filtered out -> 404 (no existence leak)
        var resp = await clientB.GetAsync($"/api/projects/{projectId}/issues");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueListTests`
Expected: FAIL (endpoint/service/records missing).

- [ ] **Step 3: Add the view records**

```csharp
// src/JiraUnpacked.Domain/Abstractions/IssueViews.cs
namespace JiraUnpacked.Domain.Abstractions;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record IssueListItem(
    string JiraIssueId, string JiraKey, string? Summary,
    string? Status, string? IssueType, string? Priority, string? Assignee, string? Sprint, string? Team,
    decimal? StoryPoints, string? ParentJiraIssueId, DateTime JiraCreatedAt, DateTime JiraUpdatedAt);

public sealed record IssueRef(string JiraIssueId, string JiraKey, string? Summary);

public sealed record ChangeHistoryEntry(string Field, string? FromValue, string? ToValue, string? Author, DateTime ChangedAt);

public sealed record IssueDetail(
    IssueListItem Issue, IssueRef? Parent, IReadOnlyList<IssueRef> Children, IReadOnlyList<ChangeHistoryEntry> ChangeHistory);
```

- [ ] **Step 4: Add the service interface**

```csharp
// src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs
namespace JiraUnpacked.Domain.Abstractions;

public interface IIssueQueryService
{
    /// <summary>Paginated issues for a project, tenant-scoped. Null when the project is not the caller's.</summary>
    Task<PagedResult<IssueListItem>?> ListAsync(Guid projectId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>One issue's detail (with change history + parent/children). Null when not the caller's.</summary>
    Task<IssueDetail?> GetDetailAsync(Guid projectId, string jiraIssueId, CancellationToken ct = default);
}
```

- [ ] **Step 5: Implement the query service (list; detail stubbed for RV2)**

The dictionary names are resolved with **correlated subqueries** inlined in the `Select` (EF Core translates each to a LEFT JOIN / lateral subquery — a null FK or absent dict row yields a null name). Do NOT extract the projection into a method: EF cannot translate a method call inside `Select`.

```csharp
// src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

public sealed class IssueQueryService : IIssueQueryService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;
    private readonly AppDbContext _db;

    public IssueQueryService(AppDbContext db) => _db = db;

    public async Task<PagedResult<IssueListItem>?> ListAsync(Guid projectId, int page, int pageSize, CancellationToken ct = default)
    {
        // Tenant-scoped existence check: a cross-tenant/unknown project is filtered out => null => 404.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return null;

        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var q = _db.Issues.Where(i => i.JiraProjectId == projectId);
        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(i => i.JiraUpdatedAt).ThenBy(i => i.JiraKey)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new IssueListItem(
                i.JiraIssueId, i.JiraKey, i.Summary,
                _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault(),
                _db.Priorities.Where(p => p.Id == i.PriorityId).Select(p => p.Name).FirstOrDefault(),
                _db.JiraUsers.Where(u => u.Id == i.AssigneeId).Select(u => u.DisplayName).FirstOrDefault(),
                _db.Sprints.Where(sp => sp.Id == i.SprintId).Select(sp => sp.Name).FirstOrDefault(),
                _db.Teams.Where(tm => tm.Id == i.TeamId).Select(tm => tm.Name).FirstOrDefault(),
                i.StoryPoints, i.ParentJiraIssueId, i.JiraCreatedAt, i.JiraUpdatedAt))
            .ToListAsync(ct);

        return new PagedResult<IssueListItem>(items, page, pageSize, total);
    }

    public Task<IssueDetail?> GetDetailAsync(Guid projectId, string jiraIssueId, CancellationToken ct = default) =>
        throw new NotImplementedException("RV2");
}
```

- [ ] **Step 6: Add the controller + DI + index**

```csharp
// src/JiraUnpacked.Api/Controllers/IssuesController.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class IssuesController : ControllerBase
{
    private readonly IIssueQueryService _issues;
    public IssuesController(IIssueQueryService issues) => _issues = issues;

    [HttpGet("api/projects/{projectId:guid}/issues")]
    public async Task<IActionResult> List(Guid projectId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var result = await _issues.ListAsync(projectId, page, pageSize, ct);
        return result is null ? NotFound() : Ok(result);
    }
}
```

In `IssueConfiguration.Configure` (add after the existing indexes):
```csharp
b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraUpdatedAt });
```
In `DependencyInjection.AddInfrastructure`:
```csharp
services.AddScoped<IIssueQueryService, JiraUnpacked.Infrastructure.Reporting.IssueQueryService>();
```

- [ ] **Step 7: Generate the index migration**

Run:
```
dotnet ef migrations add AddIssueUpdatedIndex --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```
Inspect: a single `CreateIndex` on `Issues (EndUserId, JiraProjectId, JiraUpdatedAt)`, no table/column changes.

- [ ] **Step 8: Run tests + drift + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueListTests` → PASS (3).
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes".

```bash
git add src/JiraUnpacked.Domain/Abstractions/IssueViews.cs \
        src/JiraUnpacked.Domain/Abstractions/IIssueQueryService.cs \
        src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs \
        src/JiraUnpacked.Api/Controllers/IssuesController.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueConfiguration.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        tests/JiraUnpacked.Tests/Integration/IssueListTests.cs
git commit -m "feat: paginated issue list with resolved dict names + ordering index (Module E, FR-E1)"
```

---

## Task 2 (RV2): Issue detail endpoint (change history + hierarchy)

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs` (implement `GetDetailAsync`)
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs` (add GET detail)
- Test: `tests/JiraUnpacked.Tests/Integration/IssueDetailTests.cs`

**Interfaces:**
- Consumes: `IIssueQueryService.GetDetailAsync` (RV1 stub), `IssueDetail`/`IssueRef`/`ChangeHistoryEntry`/`IssueListItem` (RV1).

- [ ] **Step 1: Write the failing integration test**

```csharp
// tests/JiraUnpacked.Tests/Integration/IssueDetailTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class IssueDetailTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public IssueDetailTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
    private sealed record ItemDto(string JiraIssueId, string JiraKey, string? Summary, string? Status, string? IssueType,
        string? Priority, string? Assignee, string? Sprint, string? Team, decimal? StoryPoints, string? ParentJiraIssueId,
        DateTime JiraCreatedAt, DateTime JiraUpdatedAt);
    private sealed record RefDto(string JiraIssueId, string JiraKey, string? Summary);
    private sealed record HistDto(string Field, string? FromValue, string? ToValue, string? Author, DateTime ChangedAt);
    private sealed record DetailDto(ItemDto Issue, RefDto? Parent, List<RefDto> Children, List<HistDto> ChangeHistory);

    private async Task<(HttpClient client, Guid tenantId)> AuthedTenantAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User { Id = userId, Email = $"u-{unique}@x.com", Username = $"u-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return (c, userId);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    // epic ACME-1 (10001), story ACME-2 (10002, parent 10001) + one status change-history row on the story.
    private static async Task<Guid> SeedAsync(Guid tenant)
    {
        var connId = Guid.NewGuid(); var projId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())", ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())", ("i", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10001','ACME-1','Epic',now(),now(),now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""ParentJiraIssueId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10002','ACME-2','Story','10001',now(),now(),now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") VALUES (@i,@t,@p,'10002','h1','status','To Do','In Progress','Alice',now())", ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        return projId;
    }

    [Fact]
    public async Task Detail_returns_issue_with_history_parent_and_children()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedAsync(tenant);

        // Story: has a parent (epic) + a change-history row.
        var story = await (await client.GetAsync($"/api/projects/{projectId}/issues/10002")).Content.ReadFromJsonAsync<DetailDto>();
        Assert.Equal("ACME-2", story!.Issue.JiraKey);
        Assert.Equal("ACME-1", story.Parent!.JiraKey);
        var h = Assert.Single(story.ChangeHistory);
        Assert.Equal("status", h.Field);
        Assert.Equal("In Progress", h.ToValue);

        // Epic: no parent, one child (the story).
        var epic = await (await client.GetAsync($"/api/projects/{projectId}/issues/10001")).Content.ReadFromJsonAsync<DetailDto>();
        Assert.Null(epic!.Parent);
        Assert.Contains(epic.Children, ch => ch.JiraKey == "ACME-2");
    }

    [Fact]
    public async Task Unknown_issue_returns_404()
    {
        var (client, tenant) = await AuthedTenantAsync();
        var projectId = await SeedAsync(tenant);
        var resp = await client.GetAsync($"/api/projects/{projectId}/issues/99999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueDetailTests`
Expected: FAIL (`GetDetailAsync` throws `NotImplementedException`).

- [ ] **Step 3: Implement `GetDetailAsync`**

Replace the `GetDetailAsync` stub in `IssueQueryService.cs`:

```csharp
    public async Task<IssueDetail?> GetDetailAsync(Guid projectId, string jiraIssueId, CancellationToken ct = default)
    {
        var issue = await _db.Issues
            .Where(i => i.JiraProjectId == projectId && i.JiraIssueId == jiraIssueId)
            .Select(i => new IssueListItem(
                i.JiraIssueId, i.JiraKey, i.Summary,
                _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault(),
                _db.Priorities.Where(p => p.Id == i.PriorityId).Select(p => p.Name).FirstOrDefault(),
                _db.JiraUsers.Where(u => u.Id == i.AssigneeId).Select(u => u.DisplayName).FirstOrDefault(),
                _db.Sprints.Where(sp => sp.Id == i.SprintId).Select(sp => sp.Name).FirstOrDefault(),
                _db.Teams.Where(tm => tm.Id == i.TeamId).Select(tm => tm.Name).FirstOrDefault(),
                i.StoryPoints, i.ParentJiraIssueId, i.JiraCreatedAt, i.JiraUpdatedAt))
            .FirstOrDefaultAsync(ct);
        if (issue is null) return null;

        IssueRef? parent = issue.ParentJiraIssueId is null ? null : await _db.Issues
            .Where(i => i.JiraProjectId == projectId && i.JiraIssueId == issue.ParentJiraIssueId)
            .Select(i => new IssueRef(i.JiraIssueId, i.JiraKey, i.Summary))
            .FirstOrDefaultAsync(ct);

        var children = await _db.Issues
            .Where(i => i.JiraProjectId == projectId && i.ParentJiraIssueId == jiraIssueId)
            .OrderBy(i => i.JiraKey)
            .Select(i => new IssueRef(i.JiraIssueId, i.JiraKey, i.Summary))
            .ToListAsync(ct);

        var history = await _db.IssueChangeHistory
            .Where(h => h.JiraProjectId == projectId && h.JiraIssueId == jiraIssueId)
            .OrderBy(h => h.ChangedAt)
            .Select(h => new ChangeHistoryEntry(h.Field, h.FromValue, h.ToValue, h.Author, h.ChangedAt))
            .ToListAsync(ct);

        return new IssueDetail(issue, parent, children, history);
    }
```

- [ ] **Step 4: Add the controller endpoint**

In `IssuesController`, add:
```csharp
    [HttpGet("api/projects/{projectId:guid}/issues/{jiraIssueId}")]
    public async Task<IActionResult> Detail(Guid projectId, string jiraIssueId, CancellationToken ct = default)
    {
        var detail = await _issues.GetDetailAsync(projectId, jiraIssueId, ct);
        return detail is null ? NotFound() : Ok(detail);
    }
```

- [ ] **Step 5: Run tests + full suite + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~IssueDetailTests` → PASS (2).
Run: `dotnet test JiraUnpacked.sln` → all green.

```bash
git add src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs \
        src/JiraUnpacked.Api/Controllers/IssuesController.cs \
        tests/JiraUnpacked.Tests/Integration/IssueDetailTests.cs
git commit -m "feat: issue detail with change history + parent/children (Module E, FR-E1)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green.
- [ ] `dotnet ef migrations has-pending-model-changes ...` → "No changes".
- [ ] Push `feat/report-view`; verify `origin == local`; open PR → `develop`; CI green; squash-merge; verify develop has the code; cut the `develop → main` release PR (merge-commit). (Before the release, check `git log origin/develop..origin/main` for follow-ups on main and back-merge if any.)

## Notes for execution
- **Read-only + tenant-scoped:** every query goes through the injected `AppDbContext`. No `IgnoreQueryFilters` in production code (tests may use it for assertions only — but these tests assert via the API, so they don't need it).
- The correlated-subquery projection resolves dict names without navigation properties; EF Core translates each to a LEFT JOIN / lateral subquery. Do not add navigation properties to `Issue`.
- Tests seed via raw SQL scoped to a unique `JiraProjectId` and read via the API as the owning tenant — no cross-test contamination.
