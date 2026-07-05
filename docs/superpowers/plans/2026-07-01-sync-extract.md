# Module D Spec 2 — Sync Extract → Land Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the sync engine's extract half — a Quartz tick and a manual endpoint that incrementally pull a project's Jira issues into `RawIssueSnapshots` and record each run in `SyncRuns`, all tenant-scoped.

**Architecture:** A singleton `AmbientCurrentTenant` (AsyncLocal) unifies HTTP and background tenant context; HTTP sets it via middleware from the JWT claim, background via `BeginScope`. A thin Quartz `SyncTickJob` asks a privileged `DueProjectFinder` (the one deliberate cross-tenant read) which projects are due, then runs a tenant-scoped `SyncOrchestrator` per project. The orchestrator pages `IJiraApiClient.SearchIssuesPageAsync` (incremental JQL) and upserts raw payloads idempotently.

**Tech Stack:** ASP.NET Core 8, EF Core 8 + Npgsql (pinned 8.0.11), Quartz.NET (in-memory), System.Text.Json, xUnit + WireMock.Net.

## Global Constraints

- Target **net8.0**; EF Core + Npgsql pinned **8.0.11**.
- **No new tables / no migration** — writes only `RawIssueSnapshots` and `SyncRuns` (Spec 1 schema).
- Every issue read/write is tenant-scoped (EF global query filter + Postgres RLS via the `app.current_tenant` GUC). The **only** cross-tenant read is `DueProjectFinder`, which reads scheduling metadata (project id, EndUserId, interval, run timestamps) via a **privileged** connection — never issue payloads.
- The Jira token is decrypted only in memory (`ITokenProtector.Unprotect`) for the Jira calls; never logged, never stored in `SyncRun.Error` or any response.
- Idempotency: raw upsert keyed on `(EndUserId, JiraIssueId)`; only `Succeeded` runs advance the watermark (= the project's most recent `Succeeded` run's `StartedAt`).
- Jira HTTP exercised in tests via **WireMock.Net** (per-connection `BaseUrl` → WireMock URL).
- Integration tests run through `JiraUnpackedApiFactory` (Development env; runtime `AppDbContext` uses the RLS-enforced `jiraunpacked_app` role; the privileged factory uses the `Default` = `jiraunpacked` superuser connection).
- One commit per task. Controller pushes before PR.

**Namespaces:** tenancy `JiraUnpacked.Infrastructure.Tenancy` + abstractions `JiraUnpacked.Domain.Abstractions`; Jira client `JiraUnpacked.Infrastructure.Jira` + DTOs `JiraUnpacked.Domain.Abstractions.Jira`; sync services `JiraUnpacked.Infrastructure.Sync` + abstractions `JiraUnpacked.Domain.Abstractions`; API `JiraUnpacked.Api.Controllers` / middleware `JiraUnpacked.Api.Tenancy` / DTOs `JiraUnpacked.Api.Contracts.Sync`.

---

## Task 1 (EXT1): Ambient AsyncLocal tenant  *(critical — opus review; touches the FR-B2-gate-protected HTTP path)*

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/ITenantScope.cs`
- Create: `src/JiraUnpacked.Infrastructure/Tenancy/AmbientCurrentTenant.cs`
- Create: `src/JiraUnpacked.Api/Tenancy/TenantScopeMiddleware.cs`
- Delete: `src/JiraUnpacked.Api/Tenancy/HttpContextCurrentTenant.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` (register ambient tenant)
- Modify: `src/JiraUnpacked.Api/Program.cs` (remove HttpContextCurrentTenant reg; add middleware)
- Test: `tests/JiraUnpacked.Tests/Tenancy/AmbientCurrentTenantTests.cs`

**Interfaces:**
- Produces: `ITenantScope { IDisposable BeginScope(Guid endUserId); }`; `AmbientCurrentTenant : ICurrentTenant, ITenantScope` (singleton). After this task `ICurrentTenant`/`ITenantScope` both resolve to the ambient singleton; HTTP tenant is set by `TenantScopeMiddleware`.

- [ ] **Step 1: Write the failing unit test**

```csharp
// tests/JiraUnpacked.Tests/Tenancy/AmbientCurrentTenantTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Tenancy;

namespace JiraUnpacked.Tests.Tenancy;

public class AmbientCurrentTenantTests
{
    [Fact]
    public void EndUserId_is_null_outside_any_scope()
    {
        var t = new AmbientCurrentTenant();
        Assert.Null(t.EndUserId);
    }

    [Fact]
    public void BeginScope_sets_and_restores_on_dispose()
    {
        var t = new AmbientCurrentTenant();
        var id = Guid.NewGuid();
        using (t.BeginScope(id))
            Assert.Equal(id, t.EndUserId);
        Assert.Null(t.EndUserId);
    }

    [Fact]
    public void Nested_scopes_restore_the_outer_value()
    {
        var t = new AmbientCurrentTenant();
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();
        using (t.BeginScope(outer))
        {
            using (t.BeginScope(inner))
                Assert.Equal(inner, t.EndUserId);
            Assert.Equal(outer, t.EndUserId);
        }
    }

    [Fact]
    public async Task Value_flows_across_async_awaits()
    {
        var t = new AmbientCurrentTenant();
        var id = Guid.NewGuid();
        using (t.BeginScope(id))
        {
            await Task.Yield();
            Assert.Equal(id, t.EndUserId);
        }
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~AmbientCurrentTenantTests`
Expected: FAIL (types not defined).

- [ ] **Step 3: Add the interface + ambient implementation**

```csharp
// src/JiraUnpacked.Domain/Abstractions/ITenantScope.cs
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>Establishes the ambient tenant for a unit of work (HTTP request or background job).</summary>
public interface ITenantScope
{
    IDisposable BeginScope(Guid endUserId);
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Tenancy/AmbientCurrentTenant.cs
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Tenancy;

/// <summary>
/// AsyncLocal-backed tenant context shared by the HTTP pipeline and background jobs.
/// The value flows with the async context; BeginScope pushes/pops it.
/// </summary>
public sealed class AmbientCurrentTenant : ICurrentTenant, ITenantScope
{
    private static readonly AsyncLocal<Guid?> Current = new();

    public Guid? EndUserId => Current.Value;

    public IDisposable BeginScope(Guid endUserId)
    {
        var prior = Current.Value;
        Current.Value = endUserId;
        return new Restore(prior);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Guid? _prior;
        private bool _disposed;
        public Restore(Guid? prior) => _prior = prior;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current.Value = _prior;
        }
    }
}
```

- [ ] **Step 4: Register the ambient tenant in `AddInfrastructure`**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, add near the other registrations (after the DbContext block):

```csharp
services.AddSingleton<AmbientCurrentTenant>();
services.AddSingleton<ICurrentTenant>(sp => sp.GetRequiredService<AmbientCurrentTenant>());
services.AddSingleton<ITenantScope>(sp => sp.GetRequiredService<AmbientCurrentTenant>());
```
Add `using JiraUnpacked.Infrastructure.Tenancy;` if not present. (`TenantConnectionInterceptor` and `AppDbContext` already depend on `ICurrentTenant`; they now get the ambient singleton.)

- [ ] **Step 5: Add the middleware and rewire `Program.cs`**

```csharp
// src/JiraUnpacked.Api/Tenancy/TenantScopeMiddleware.cs
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Api.Tenancy;

/// <summary>Opens the ambient tenant scope for the request from the `endUserId` JWT claim.</summary>
public sealed class TenantScopeMiddleware
{
    private readonly RequestDelegate _next;
    public TenantScopeMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext context, ITenantScope tenantScope)
    {
        var claim = context.User.FindFirst("endUserId")?.Value;
        if (Guid.TryParse(claim, out var endUserId))
        {
            using (tenantScope.BeginScope(endUserId))
                await _next(context);
        }
        else
        {
            await _next(context);
        }
    }
}
```

In `Program.cs`:
- **Remove** the line `builder.Services.AddScoped<ICurrentTenant, HttpContextCurrentTenant>();` and its `using JiraUnpacked.Api.Tenancy;` is retained (the middleware is in that namespace). Leave `AddHttpContextAccessor()` (harmless).
- Insert the middleware **after** `app.UseAuthorization();` and **before** `app.MapControllers();`:
```csharp
app.UseMiddleware<JiraUnpacked.Api.Tenancy.TenantScopeMiddleware>();
```
- Delete `src/JiraUnpacked.Api/Tenancy/HttpContextCurrentTenant.cs`.

- [ ] **Step 6: Run the full suite (this changes tenant resolution globally) + commit**

Run: `dotnet test JiraUnpacked.sln`
Expected: PASS — all existing tests green (esp. `TenantIsolationGateTests`, auth/me, connections, project/query/mapping endpoints), plus the 4 new `AmbientCurrentTenantTests`. The FR-B2 gate proves HTTP isolation still holds through the middleware.

```bash
git add src/JiraUnpacked.Domain/Abstractions/ITenantScope.cs \
        src/JiraUnpacked.Infrastructure/Tenancy/AmbientCurrentTenant.cs \
        src/JiraUnpacked.Api/Tenancy/TenantScopeMiddleware.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        src/JiraUnpacked.Api/Program.cs \
        tests/JiraUnpacked.Tests/Tenancy/AmbientCurrentTenantTests.cs
git rm src/JiraUnpacked.Api/Tenancy/HttpContextCurrentTenant.cs
git commit -m "feat: ambient AsyncLocal tenant + scope middleware (Module D extract)"
```

---

## Task 2 (EXT2): Jira client `SearchIssuesPageAsync` + 429 backoff

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/Jira/SearchPage.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/IJiraApiClient.cs`
- Create: `src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.Search.cs`
- Test: `tests/JiraUnpacked.Tests/Jira/JiraApiClientSearchTests.cs`

**Interfaces:**
- Consumes: `JiraCredentials`, the `partial JiraApiClient` + its `Build`/`Json` helpers, `JiraApiException`.
- Produces: `record RawIssue(string JiraIssueId, string RawJson)`; `record SearchPage(IReadOnlyList<RawIssue> Issues, string? NextPageToken)`; `IJiraApiClient.SearchIssuesPageAsync(JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/JiraUnpacked.Tests/Jira/JiraApiClientSearchTests.cs
using System.Net;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

public sealed class JiraApiClientSearchTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private IJiraApiClient Client() => new JiraApiClient(new HttpClient());
    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "t");
    public void Dispose() => _server.Stop();

    [Fact]
    public async Task Returns_issues_and_next_page_token()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new object[] { new { id = "10001", key = "ACME-1" }, new { id = "10002", key = "ACME-2" } },
                nextPageToken = "TOKEN2"
            }));

        var page = await Client().SearchIssuesPageAsync(Creds(), "project = ACME", null, CancellationToken.None);

        Assert.Equal(2, page.Issues.Count);
        Assert.Equal("10001", page.Issues[0].JiraIssueId);
        Assert.Contains("\"key\":\"ACME-1\"", page.Issues[0].RawJson.Replace(" ", ""));
        Assert.Equal("TOKEN2", page.NextPageToken);
    }

    [Fact]
    public async Task Null_next_page_token_on_last_page()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new object[] { new { id = "10003", key = "ACME-3" } }
                // no nextPageToken
            }));

        var page = await Client().SearchIssuesPageAsync(Creds(), "project = ACME", "TOKEN2", CancellationToken.None);
        Assert.Single(page.Issues);
        Assert.Null(page.NextPageToken);
    }

    [Fact]
    public async Task Posts_the_jql_and_changelog_expand()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { issues = new object[0] }));

        await Client().SearchIssuesPageAsync(Creds(), "project = ACME AND updated >= \"2024-01-01 10:00\"", null, CancellationToken.None);

        var body = Assert.Single(_server.LogEntries).RequestMessage.Body!;
        Assert.Contains("updated >=", body);
        Assert.Contains("changelog", body);
    }

    [Fact]
    public async Task Retries_after_429_then_succeeds()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .InScenario("rl").WillSetStateTo("done")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .InScenario("rl").WhenStateIs("done")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issues = new object[] { new { id = "10004", key = "ACME-4" } }
            }));

        var page = await Client().SearchIssuesPageAsync(Creds(), "project = ACME", null, CancellationToken.None);
        Assert.Single(page.Issues);
    }

    [Fact]
    public async Task Throws_RateLimited_after_exhausting_retries()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));

        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().SearchIssuesPageAsync(Creds(), "project = ACME", null, CancellationToken.None));
        Assert.Equal(JiraErrorKind.RateLimited, ex.Kind);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~JiraApiClientSearchTests`
Expected: FAIL (method/DTOs not defined).

- [ ] **Step 3: Add the DTOs and extend the interface**

```csharp
// src/JiraUnpacked.Domain/Abstractions/Jira/SearchPage.cs
namespace JiraUnpacked.Domain.Abstractions.Jira;

public sealed record RawIssue(string JiraIssueId, string RawJson);
public sealed record SearchPage(IReadOnlyList<RawIssue> Issues, string? NextPageToken);
```
Add to `IJiraApiClient` (after `ParseJqlAsync`):
```csharp
Task<SearchPage> SearchIssuesPageAsync(JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default);
```

- [ ] **Step 4: Implement search (third partial file)**

```csharp
// src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.Search.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient
{
    private const int MaxRateLimitRetries = 3;
    private const int SearchPageSize = 100;

    public async Task<SearchPage> SearchIssuesPageAsync(
        JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = Build(HttpMethod.Post, creds, "/rest/api/3/search/jql");
            req.Content = JsonContent.Create(new
            {
                jql,
                maxResults = SearchPageSize,
                nextPageToken,
                expand = new[] { "changelog" }
            }, options: Json);

            using var resp = await SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                await DelayForRetryAsync(resp, ct);
                continue;
            }
            EnsureOk(resp);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var issues = new List<RawIssue>();
            if (root.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    issues.Add(new RawIssue(el.GetProperty("id").GetString()!, el.GetRawText()));

            var token = root.TryGetProperty("nextPageToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            return new SearchPage(issues, token);
        }
    }

    private static async Task DelayForRetryAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var seconds = 1.0;
        if (resp.Headers.TryGetValues("Retry-After", out var vals) &&
            double.TryParse(vals.FirstOrDefault(), out var parsed))
            seconds = Math.Max(0, parsed);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
    }
}
```

(Reuses `Build`, `SendAsync`, `EnsureOk`, and `Json` from the CSL2/CSL3 partials. `SendAsync` already wraps transport errors as `JiraApiException(Upstream)`; `EnsureOk` maps a final 429 to `JiraApiException(RateLimited)`.)

- [ ] **Step 5: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~JiraApiClientSearchTests` → PASS (5).

```bash
git add src/JiraUnpacked.Domain/Abstractions/Jira/SearchPage.cs \
        src/JiraUnpacked.Domain/Abstractions/IJiraApiClient.cs \
        src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.Search.cs \
        tests/JiraUnpacked.Tests/Jira/JiraApiClientSearchTests.cs
git commit -m "feat: Jira issue-search page fetch + 429 backoff (Module D extract, FR-D2)"
```

---

## Task 3 (EXT3): Privileged factory + `DueProjectFinder`  *(critical — opus review; deliberate RLS bypass)*

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Persistence/IPrivilegedDbContextFactory.cs`
- Create: `src/JiraUnpacked.Infrastructure/Persistence/PrivilegedDbContextFactory.cs`
- Create: `src/JiraUnpacked.Domain/Abstractions/IDueProjectFinder.cs`
- Create: `src/JiraUnpacked.Infrastructure/Sync/DueProjectFinder.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` (register both)
- Test: `tests/JiraUnpacked.Tests/Sync/DueProjectFinderTests.cs`

**Interfaces:**
- Consumes: `AppDbContext`, `NullCurrentTenant`, `IClock`, `SyncRunStatus`, `IConfiguration`.
- Produces: `IPrivilegedDbContextFactory { AppDbContext Create(); }` (builds an `AppDbContext` on the `Default` superuser connection with `NullCurrentTenant` — bypasses RLS); `record DueProject(Guid ProjectId, Guid EndUserId, int SyncIntervalMinutes)`; `IDueProjectFinder { Task<IReadOnlyList<DueProject>> GetDueProjectsAsync(CancellationToken ct = default); }`.

- [ ] **Step 1: Write the failing integration test**

```csharp
// tests/JiraUnpacked.Tests/Sync/DueProjectFinderTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Tenancy;
using JiraUnpacked.Infrastructure.Time;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Sync;

public sealed class DueProjectFinderTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public DueProjectFinderTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // A privileged context (Default superuser conn) for seeding across tenants, mirroring the finder.
    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    private static async Task<Guid> SeedProjectWithQueryAsync(AppDbContext db, Guid tenant, int intervalMinutes)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        db.JiraConnections.Add(new JiraConnection { Id = connId, EndUserId = tenant, BaseUrl = "https://x", Email = "e", ApiTokenEncrypted = "c", CreatedAt = DateTime.UtcNow });
        db.JiraProjects.Add(new JiraProject { Id = projId, EndUserId = tenant, JiraConnectionId = connId, JiraProjectKey = "ACME", JiraProjectId = "1", SyncIntervalMinutes = intervalMinutes, CreatedAt = DateTime.UtcNow });
        db.JiraQueries.Add(new JiraQuery { Id = Guid.NewGuid(), EndUserId = tenant, JiraProjectId = projId, Jql = "project = ACME", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return projId;
    }

    private static DueProjectFinder Finder() =>
        new(new PrivilegedDbContextFactory(_config()), new SystemClock());

    private static Microsoft.Extensions.Configuration.IConfiguration _config() =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Default"] = JiraUnpackedApiFactory.TestConnectionString }).Build();

    [Fact]
    public async Task Project_with_no_successful_run_is_due()
    {
        var tenant = Guid.NewGuid();
        Guid projId;
        await using (var db = Privileged()) projId = await SeedProjectWithQueryAsync(db, tenant, 60);

        var due = await Finder().GetDueProjectsAsync();
        Assert.Contains(due, d => d.ProjectId == projId && d.EndUserId == tenant);
    }

    [Fact]
    public async Task Recently_synced_project_is_not_due()
    {
        var tenant = Guid.NewGuid();
        await using var db = Privileged();
        var projId = await SeedProjectWithQueryAsync(db, tenant, 60);
        db.SyncRuns.Add(new SyncRun { Id = Guid.NewGuid(), EndUserId = tenant, JiraProjectId = projId, StartedAt = DateTime.UtcNow.AddMinutes(-5), FinishedAt = DateTime.UtcNow.AddMinutes(-5), Status = SyncRunStatus.Succeeded, IssuesProcessed = 1 });
        await db.SaveChangesAsync();

        var due = await Finder().GetDueProjectsAsync();
        Assert.DoesNotContain(due, d => d.ProjectId == projId);
    }

    [Fact]
    public async Task Project_with_a_running_run_is_excluded()
    {
        var tenant = Guid.NewGuid();
        await using var db = Privileged();
        var projId = await SeedProjectWithQueryAsync(db, tenant, 60);
        db.SyncRuns.Add(new SyncRun { Id = Guid.NewGuid(), EndUserId = tenant, JiraProjectId = projId, StartedAt = DateTime.UtcNow, Status = SyncRunStatus.Running, IssuesProcessed = 0 });
        await db.SaveChangesAsync();

        var due = await Finder().GetDueProjectsAsync();
        Assert.DoesNotContain(due, d => d.ProjectId == projId);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~DueProjectFinderTests`
Expected: FAIL (types not defined).

- [ ] **Step 3: Add the privileged factory**

```csharp
// src/JiraUnpacked.Infrastructure/Persistence/IPrivilegedDbContextFactory.cs
namespace JiraUnpacked.Infrastructure.Persistence;

/// <summary>
/// Builds an AppDbContext on the privileged (RLS-bypassing) 'Default' connection with no tenant.
/// USE ONLY for cross-tenant scheduling metadata (DueProjectFinder). Never read issue payloads with it.
/// </summary>
public interface IPrivilegedDbContextFactory
{
    AppDbContext Create();
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/PrivilegedDbContextFactory.cs
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JiraUnpacked.Infrastructure.Persistence;

public sealed class PrivilegedDbContextFactory : IPrivilegedDbContextFactory
{
    private readonly string _connectionString;

    public PrivilegedDbContextFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
    }

    public AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }
}
```

- [ ] **Step 4: Add the due-finder**

```csharp
// src/JiraUnpacked.Domain/Abstractions/IDueProjectFinder.cs
namespace JiraUnpacked.Domain.Abstractions;

public sealed record DueProject(Guid ProjectId, Guid EndUserId, int SyncIntervalMinutes);

public interface IDueProjectFinder
{
    Task<IReadOnlyList<DueProject>> GetDueProjectsAsync(CancellationToken ct = default);
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Sync/DueProjectFinder.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

/// <summary>
/// The ONE deliberate cross-tenant read: finds due projects across all tenants using the
/// privileged connection + IgnoreQueryFilters, selecting scheduling metadata only.
/// </summary>
public sealed class DueProjectFinder : IDueProjectFinder
{
    private readonly IPrivilegedDbContextFactory _factory;
    private readonly IClock _clock;

    public DueProjectFinder(IPrivilegedDbContextFactory factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DueProject>> GetDueProjectsAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        await using var db = _factory.Create();

        var candidates = await db.JiraProjects.IgnoreQueryFilters()
            .Where(p => db.JiraQueries.IgnoreQueryFilters().Any(q => q.JiraProjectId == p.Id))
            .Select(p => new
            {
                p.Id,
                p.EndUserId,
                p.SyncIntervalMinutes,
                HasRunning = db.SyncRuns.IgnoreQueryFilters()
                    .Any(r => r.JiraProjectId == p.Id && r.Status == SyncRunStatus.Running),
                LastSuccess = db.SyncRuns.IgnoreQueryFilters()
                    .Where(r => r.JiraProjectId == p.Id && r.Status == SyncRunStatus.Succeeded)
                    .Max(r => (DateTime?)r.StartedAt)
            })
            .ToListAsync(ct);

        return candidates
            .Where(p => !p.HasRunning &&
                        (p.LastSuccess == null || p.LastSuccess.Value.AddMinutes(p.SyncIntervalMinutes) <= now))
            .Select(p => new DueProject(p.Id, p.EndUserId, p.SyncIntervalMinutes))
            .ToList();
    }
}
```

- [ ] **Step 5: Register both + commit**

In `DependencyInjection.AddInfrastructure`:
```csharp
services.AddSingleton<IPrivilegedDbContextFactory, PrivilegedDbContextFactory>();
services.AddScoped<IDueProjectFinder, DueProjectFinder>();
```
Add `using JiraUnpacked.Infrastructure.Sync;` and `using JiraUnpacked.Infrastructure.Persistence;` if needed.

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~DueProjectFinderTests` → PASS (3).

```bash
git add src/JiraUnpacked.Infrastructure/Persistence/IPrivilegedDbContextFactory.cs \
        src/JiraUnpacked.Infrastructure/Persistence/PrivilegedDbContextFactory.cs \
        src/JiraUnpacked.Domain/Abstractions/IDueProjectFinder.cs \
        src/JiraUnpacked.Infrastructure/Sync/DueProjectFinder.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Sync/DueProjectFinderTests.cs
git commit -m "feat: privileged due-project finder (cross-tenant scheduling read) (Module D extract, FR-D1)"
```

---

## Task 4 (EXT4): Sync orchestrator + `POST /sync`

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/ISyncOrchestrator.cs`
- Create: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs`
- Create: `src/JiraUnpacked.Api/Controllers/SyncController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/SyncEndpointTests.cs`

**Interfaces:**
- Consumes: `AppDbContext` (tenant-scoped), `IJiraApiClient.SearchIssuesPageAsync` (EXT2), `ITokenProtector`, `IClock`. **Assumes the ambient tenant is already set by the caller** (middleware for HTTP, tick job for background).
- Produces:
  - `enum SyncOutcome { Completed, AlreadyRunning, ProjectNotFound }`
  - `record SyncRunView(Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt, int IssuesProcessed, string? Error, DateTime? NextRunAt)`
  - `record SyncRunResult(SyncOutcome Outcome, SyncRunView? Run)`
  - `ISyncOrchestrator { Task<SyncRunResult> RunProjectAsync(Guid projectId, CancellationToken ct = default); }`

- [ ] **Step 1: Write the failing integration tests**

```csharp
// tests/JiraUnpacked.Tests/Integration/SyncEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public SyncEndpointTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record SyncRunDto(Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt, int IssuesProcessed, string? Error, DateTime? NextRunAt);

    private void StubValidAndProject()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
    }

    private async Task<(HttpClient client, Guid projectId)> ConfiguredProjectAsync()
    {
        StubValidAndProject();
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        return (client, project.Id);
    }

    private void StubSearchTwoPages()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost().WithBody(b => !b.Contains("TOKEN2")))
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             { issues = new object[] { new { id = "10001", key = "ACME-1" }, new { id = "10002", key = "ACME-2" } }, nextPageToken = "TOKEN2" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost().WithBody(b => b.Contains("TOKEN2")))
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
             { issues = new object[] { new { id = "10003", key = "ACME-3" } } }));
    }

    private async Task<long> RawCountForTenantAsync(Guid projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options; // privileged, bypass RLS for assertion
        await using var db = new AppDbContext(options, new NullCurrentTenant());
        return await db.RawIssueSnapshots.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == projectId);
    }

    [Fact]
    public async Task Sync_lands_all_pages_and_records_a_succeeded_run()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        StubSearchTwoPages();

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var run = (await resp.Content.ReadFromJsonAsync<SyncRunDto>())!;
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(3, run.IssuesProcessed);
        Assert.NotNull(run.NextRunAt);
        Assert.Equal(3, await RawCountForTenantAsync(projectId));
    }

    [Fact]
    public async Task Sync_is_idempotent_on_rerun()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        StubSearchTwoPages();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { });
        await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { });
        Assert.Equal(3, await RawCountForTenantAsync(projectId)); // upsert, not duplicate
    }

    [Fact]
    public async Task Second_run_filters_by_updated_watermark()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        StubSearchTwoPages();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { });
        _jira.Reset();
        StubValidAndProject();
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { issues = new object[0] }));

        await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { });

        var searchCalls = _jira.LogEntries.Where(e => e.RequestMessage.Path == "/rest/api/3/search/jql").ToList();
        Assert.Contains(searchCalls, e => e.RequestMessage.Body!.Contains("updated >="));
    }

    [Fact]
    public async Task Jira_failure_records_a_failed_run()
    {
        var (client, projectId) = await ConfiguredProjectAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(500));

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { });
        var run = (await resp.Content.ReadFromJsonAsync<SyncRunDto>())!;
        Assert.Equal("Failed", run.Status);
        Assert.Equal(0, await RawCountForTenantAsync(projectId));
    }

    [Fact]
    public async Task Sync_unknown_project_returns_404()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var resp = await client.PostAsJsonAsync($"/api/projects/{Guid.NewGuid()}/sync", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncEndpointTests`
Expected: FAIL (orchestrator/endpoint missing).

- [ ] **Step 3: Add the orchestrator interface + result types**

```csharp
// src/JiraUnpacked.Domain/Abstractions/ISyncOrchestrator.cs
namespace JiraUnpacked.Domain.Abstractions;

public enum SyncOutcome { Completed, AlreadyRunning, ProjectNotFound }

public sealed record SyncRunView(
    Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt,
    int IssuesProcessed, string? Error, DateTime? NextRunAt);

public sealed record SyncRunResult(SyncOutcome Outcome, SyncRunView? Run);

public interface ISyncOrchestrator
{
    /// <summary>Runs one project's extract. The caller MUST have established the ambient tenant (project owner).</summary>
    Task<SyncRunResult> RunProjectAsync(Guid projectId, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement the orchestrator**

```csharp
// src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed class SyncOrchestrator : ISyncOrchestrator
{
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;

    public SyncOrchestrator(AppDbContext db, IJiraApiClient jira, ITokenProtector protector, IClock clock)
    {
        _db = db; _jira = jira; _protector = protector; _clock = clock;
    }

    public async Task<SyncRunResult> RunProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return new SyncRunResult(SyncOutcome.ProjectNotFound, null);

        if (await _db.SyncRuns.AnyAsync(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Running, ct))
            return new SyncRunResult(SyncOutcome.AlreadyRunning, null);

        var startedAt = _clock.UtcNow;
        var run = new SyncRun
        {
            Id = Guid.NewGuid(), JiraProjectId = projectId,
            StartedAt = startedAt, Status = SyncRunStatus.Running, IssuesProcessed = 0
        };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        try
        {
            var watermark = await _db.SyncRuns
                .Where(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Succeeded)
                .MaxAsync(r => (DateTime?)r.StartedAt, ct);

            var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == project.JiraConnectionId, ct)
                       ?? throw new InvalidOperationException("Connection not found for project.");
            var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));

            var queries = await _db.JiraQueries.Where(q => q.JiraProjectId == projectId).ToListAsync(ct);

            var processed = 0;
            foreach (var query in queries)
            {
                var jql = watermark is null
                    ? query.Jql
                    : $"({query.Jql}) AND updated >= \"{watermark.Value:yyyy-MM-dd HH:mm}\"";

                string? token = null;
                do
                {
                    var page = await _jira.SearchIssuesPageAsync(creds, jql, token, ct);
                    foreach (var issue in page.Issues)
                    {
                        var existing = await _db.RawIssueSnapshots
                            .FirstOrDefaultAsync(x => x.JiraIssueId == issue.JiraIssueId, ct);
                        if (existing is null)
                        {
                            _db.RawIssueSnapshots.Add(new RawIssueSnapshot
                            {
                                Id = Guid.NewGuid(), JiraProjectId = projectId,
                                JiraIssueId = issue.JiraIssueId, Payload = issue.RawJson, FetchedAt = _clock.UtcNow
                            });
                        }
                        else
                        {
                            existing.Payload = issue.RawJson;
                            existing.FetchedAt = _clock.UtcNow;
                        }
                        processed++;
                    }
                    await _db.SaveChangesAsync(ct);
                    token = page.NextPageToken;
                } while (token is not null);
            }

            run.Status = SyncRunStatus.Succeeded;
            run.IssuesProcessed = processed;
            run.FinishedAt = _clock.UtcNow;
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed;
            run.Error = ex.Message; // JiraApiException/domain messages are safe; the token never appears in them
            run.FinishedAt = _clock.UtcNow;
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }

        return new SyncRunResult(SyncOutcome.Completed, ToView(run));
    }

    private static SyncRunView ToView(SyncRun r) =>
        new(r.Id, r.JiraProjectId, r.Status.ToString(), r.StartedAt, r.FinishedAt, r.IssuesProcessed, r.Error, r.NextRunAt);
}
```

- [ ] **Step 5: Add the controller + DI**

```csharp
// src/JiraUnpacked.Api/Controllers/SyncController.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class SyncController : ControllerBase
{
    private readonly ISyncOrchestrator _orchestrator;
    public SyncController(ISyncOrchestrator orchestrator) => _orchestrator = orchestrator;

    [HttpPost("api/projects/{projectId:guid}/sync")]
    public async Task<IActionResult> Sync(Guid projectId, CancellationToken ct)
    {
        var result = await _orchestrator.RunProjectAsync(projectId, ct);
        return result.Outcome switch
        {
            SyncOutcome.ProjectNotFound => NotFound(),
            SyncOutcome.AlreadyRunning => Conflict(),
            _ => Ok(result.Run)
        };
    }
}
```
Register: `services.AddScoped<ISyncOrchestrator, SyncOrchestrator>();`

- [ ] **Step 6: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncEndpointTests` → PASS (5).

```bash
git add src/JiraUnpacked.Domain/Abstractions/ISyncOrchestrator.cs \
        src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs \
        src/JiraUnpacked.Api/Controllers/SyncController.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Integration/SyncEndpointTests.cs
git commit -m "feat: sync orchestrator + POST /sync (incremental raw landing) (Module D extract, FR-D1/D2/D5)"
```

---

## Task 5 (EXT5): Sync-run status endpoint

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/ISyncRunQuery.cs`
- Create: `src/JiraUnpacked.Infrastructure/Sync/SyncRunQuery.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/SyncController.cs` (add GET)
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/SyncStatusTests.cs`

**Interfaces:**
- Consumes: `AppDbContext` (tenant-scoped), `SyncRunView` (EXT4).
- Produces: `ISyncRunQuery { Task<IReadOnlyList<SyncRunView>> ListAsync(Guid projectId, CancellationToken ct = default); }` — newest first, capped at 50.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/JiraUnpacked.Tests/Integration/SyncStatusTests.cs
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncStatusTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public SyncStatusTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record SyncRunDto(Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt, int IssuesProcessed, string? Error, DateTime? NextRunAt);

    [Fact]
    public async Task Lists_runs_newest_first_after_a_sync()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { issues = new object[] { new { id = "10001", key = "ACME-1" } } }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/queries", new { jql = "project = ACME" });
        await client.PostAsJsonAsync($"/api/projects/{project.Id}/sync", new { });

        var runs = await (await client.GetAsync($"/api/projects/{project.Id}/sync-runs")).Content.ReadFromJsonAsync<List<SyncRunDto>>();
        Assert.NotEmpty(runs!);
        Assert.Equal("Succeeded", runs![0].Status);
        Assert.Equal(1, runs[0].IssuesProcessed);
    }

    [Fact]
    public async Task Unknown_project_returns_empty_list()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var runs = await (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/sync-runs")).Content.ReadFromJsonAsync<List<SyncRunDto>>();
        Assert.Empty(runs!);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncStatusTests`
Expected: FAIL (endpoint missing).

- [ ] **Step 3: Add the query service**

```csharp
// src/JiraUnpacked.Domain/Abstractions/ISyncRunQuery.cs
namespace JiraUnpacked.Domain.Abstractions;

public interface ISyncRunQuery
{
    Task<IReadOnlyList<SyncRunView>> ListAsync(Guid projectId, CancellationToken ct = default);
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Sync/SyncRunQuery.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Sync;

public sealed class SyncRunQuery : ISyncRunQuery
{
    private readonly AppDbContext _db;
    public SyncRunQuery(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<SyncRunView>> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        // Materialize the tenant-scoped rows first, then map (enum.ToString() runs in memory,
        // not in the SQL projection, so there is no LINQ-translation risk).
        var runs = await _db.SyncRuns
            .Where(r => r.JiraProjectId == projectId)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .ToListAsync(ct);

        return runs
            .Select(r => new SyncRunView(r.Id, r.JiraProjectId, r.Status.ToString(),
                r.StartedAt, r.FinishedAt, r.IssuesProcessed, r.Error, r.NextRunAt))
            .ToList();
    }
}
```

- [ ] **Step 4: Add the GET endpoint + DI**

In `SyncController` add a constructor dependency `ISyncRunQuery _runs` (inject alongside `_orchestrator`) and:
```csharp
[HttpGet("api/projects/{projectId:guid}/sync-runs")]
public async Task<IActionResult> Runs(Guid projectId, CancellationToken ct) =>
    Ok(await _runs.ListAsync(projectId, ct));
```
Register: `services.AddScoped<ISyncRunQuery, SyncRunQuery>();`

- [ ] **Step 5: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncStatusTests` → PASS (2).

```bash
git add src/JiraUnpacked.Domain/Abstractions/ISyncRunQuery.cs \
        src/JiraUnpacked.Infrastructure/Sync/SyncRunQuery.cs \
        src/JiraUnpacked.Api/Controllers/SyncController.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Integration/SyncStatusTests.cs
git commit -m "feat: per-project sync-run status endpoint (Module D extract, FR-D7)"
```

---

## Task 6 (EXT6): Quartz tick wiring

**Files:**
- Modify: `src/JiraUnpacked.Api/JiraUnpacked.Api.csproj` (add Quartz packages)
- Create: `src/JiraUnpacked.Infrastructure/Sync/SyncTickJob.cs`
- Create: `src/JiraUnpacked.Infrastructure/Sync/SyncSchedulerOptions.cs`
- Modify: `src/JiraUnpacked.Api/Program.cs` (conditional Quartz registration)
- Modify: `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs` (disable the scheduler in tests)
- Test: `tests/JiraUnpacked.Tests/Sync/SyncTickJobTests.cs`

**Interfaces:**
- Consumes: `IDueProjectFinder` (EXT3), `ITenantScope` (EXT1), `ISyncOrchestrator` (EXT4), `IServiceScopeFactory`.
- Produces: `SyncTickJob : Quartz.IJob`; `SyncSchedulerOptions { bool Enabled }` (config `Sync:SchedulerEnabled`, default true); Quartz registered only when enabled.

- [ ] **Step 1: Add the Quartz packages**

In `src/JiraUnpacked.Api/JiraUnpacked.Api.csproj` add to the package `ItemGroup`:
```xml
<PackageReference Include="Quartz" Version="3.13.1" />
<PackageReference Include="Quartz.Extensions.Hosting" Version="3.13.1" />
```
Run `dotnet restore` and confirm it resolves.

- [ ] **Step 2: Write the failing smoke test**

```csharp
// tests/JiraUnpacked.Tests/Sync/SyncTickJobTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiraUnpacked.Tests.Sync;

public class SyncTickJobTests
{
    private sealed class OneDueFinder : IDueProjectFinder
    {
        private readonly DueProject _p;
        public OneDueFinder(DueProject p) => _p = p;
        public Task<IReadOnlyList<DueProject>> GetDueProjectsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DueProject>>(new[] { _p });
    }

    private sealed class RecordingOrchestrator : ISyncOrchestrator
    {
        public readonly List<(Guid tenant, Guid project)> Calls = new();
        private readonly AmbientCurrentTenant _tenant;
        public RecordingOrchestrator(AmbientCurrentTenant tenant) => _tenant = tenant;
        public Task<SyncRunResult> RunProjectAsync(Guid projectId, CancellationToken ct = default)
        {
            Calls.Add((_tenant.EndUserId ?? Guid.Empty, projectId)); // captures the ambient tenant at call time
            return Task.FromResult(new SyncRunResult(SyncOutcome.Completed, null));
        }
    }

    [Fact]
    public async Task Runs_each_due_project_inside_its_tenant_scope()
    {
        var ambient = new AmbientCurrentTenant();
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var orch = new RecordingOrchestrator(ambient);

        var services = new ServiceCollection();
        services.AddSingleton<ISyncOrchestrator>(orch);
        var provider = services.BuildServiceProvider();

        var job = new SyncTickJob(
            new OneDueFinder(new DueProject(project, tenant, 60)),
            ambient, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SyncTickJob>.Instance);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Contains((tenant, project), orch.Calls); // ran with the ambient tenant set to the owner
    }
}
```

- [ ] **Step 3: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncTickJobTests`
Expected: FAIL (`SyncTickJob` not defined).

- [ ] **Step 4: Add the job + options**

```csharp
// src/JiraUnpacked.Infrastructure/Sync/SyncSchedulerOptions.cs
namespace JiraUnpacked.Infrastructure.Sync;

public sealed class SyncSchedulerOptions
{
    public const string SectionName = "Sync";
    public bool SchedulerEnabled { get; set; } = true;
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Sync/SyncTickJob.cs
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace JiraUnpacked.Infrastructure.Sync;

/// <summary>Thin tick: find due projects, run each inside its tenant scope + a fresh DI scope.</summary>
[DisallowConcurrentExecution]
public sealed class SyncTickJob : IJob
{
    private readonly IDueProjectFinder _finder;
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SyncTickJob> _logger;

    public SyncTickJob(IDueProjectFinder finder, ITenantScope tenantScope,
        IServiceScopeFactory scopeFactory, ILogger<SyncTickJob> logger)
    {
        _finder = finder; _tenantScope = tenantScope; _scopeFactory = scopeFactory; _logger = logger;
    }

    public Task Execute(IJobExecutionContext context) => RunOnceAsync(context.CancellationToken);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var due = await _finder.GetDueProjectsAsync(ct);
        foreach (var p in due)
        {
            try
            {
                using (_tenantScope.BeginScope(p.EndUserId))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var orchestrator = scope.ServiceProvider.GetRequiredService<ISyncOrchestrator>();
                    await orchestrator.RunProjectAsync(p.ProjectId, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync tick failed for project {ProjectId}", p.ProjectId);
            }
        }
    }
}
```

- [ ] **Step 5: Register Quartz conditionally in `Program.cs`**

In `Program.cs`, after `builder.Services.AddInfrastructure(builder.Configuration);` add:

```csharp
builder.Services.Configure<JiraUnpacked.Infrastructure.Sync.SyncSchedulerOptions>(
    builder.Configuration.GetSection(JiraUnpacked.Infrastructure.Sync.SyncSchedulerOptions.SectionName));

if (builder.Configuration.GetValue("Sync:SchedulerEnabled", true))
{
    builder.Services.AddQuartz(q =>
    {
        var key = new Quartz.JobKey("sync-tick");
        q.AddJob<JiraUnpacked.Infrastructure.Sync.SyncTickJob>(key);
        q.AddTrigger(t => t.ForJob(key)
            .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));
    });
    builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
}
```

- [ ] **Step 6: Disable the scheduler in the test harness**

In `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs`, inside `ConfigureWebHost`, before/after `UseEnvironment("Development")`, add an in-memory config override so the Quartz hosted service never fires during tests:

```csharp
builder.ConfigureAppConfiguration((_, config) =>
{
    config.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Sync:SchedulerEnabled"] = "false"
    });
});
```
(Add `using Microsoft.Extensions.Configuration;` if not present.)

- [ ] **Step 7: Run the smoke test + full suite + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncTickJobTests` → PASS (1).
Run: `dotnet test JiraUnpacked.sln` → all green (the scheduler is disabled in tests, so no stray ticks).

```bash
git add src/JiraUnpacked.Api/JiraUnpacked.Api.csproj \
        src/JiraUnpacked.Infrastructure/Sync/SyncTickJob.cs \
        src/JiraUnpacked.Infrastructure/Sync/SyncSchedulerOptions.cs \
        src/JiraUnpacked.Api/Program.cs \
        tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs \
        tests/JiraUnpacked.Tests/Sync/SyncTickJobTests.cs
git commit -m "feat: Quartz tick scheduler for due-project sync (Module D extract, FR-D1)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green.
- [ ] `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes" (this spec adds no schema).
- [ ] Push `feat/sync-extract`; verify `origin == local`; open PR → `develop`; CI green; squash-merge; verify develop has the code; cut the `develop → main` release PR (merge-commit).

## Notes for execution
- **EXT1 is isolation-critical** (rewrites the HTTP tenant path) → opus review; the FR-B2 gate + full suite are the backstop.
- **EXT3 is the one deliberate cross-tenant read** (privileged, RLS-bypassing) → opus review; confirm it reads only scheduling metadata, never issue payloads, and that every issue read/write elsewhere stays tenant-scoped.
- Writes are limited to `RawIssueSnapshots` + `SyncRuns` — no `Issues`/dictionary/`IssueChangeHistory` writes (those are Spec 3). No migration.
- Never log or return the decrypted token; keep `SyncRun.Error` to safe messages.
