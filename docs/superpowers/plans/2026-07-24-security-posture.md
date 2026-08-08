# Phase 7 Spec 3 — Security Posture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rate-limit the sync endpoints and partition the auth limiter, gate HSTS behind config, stop rederive from leaking infra detail into a tenant-readable field, and clear stuck `Running` runs — closing the v1 build.

**Architecture:** All changes are middleware, a controller attribute, one orchestrator refactor, and one maintenance job. No schema change, no new tenant table, no migration.

**Tech Stack:** ASP.NET Core 8 (rate limiting, HSTS), EF Core 8 + Npgsql, Quartz.NET, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-24-security-posture-design.md` (commit 7d5860b).
**Branch:** `feat/security-posture`, cut from `develop` @ 60bae9d.

## Global Constraints

- **No new tenant table, no migration, no schema change.** The FR-B2 gate `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` stays untouched and green.
- **No `IgnoreQueryFilters`, privileged factory, or `FromSql` in production paths** beyond the sanctioned reaper/retention **metadata reads** — the reaper adds a privileged *reader* and **no privileged writer** (every write runs inside `ITenantScope.BeginScope`).
- **Tokens never logged, returned, or stored in `SyncRun.Error`.** The generic-message path already handles this for sync; SP3 extends it to rederive.
- **The incremental watermark keeps filtering `Kind == Sync && Status == Succeeded`** (`SyncOrchestrator.cs:47`). No task touches it.
- `ITenantScope.BeginScope(Guid)` must be entered **before the first DB access** in the reaper (connection GUC is set on open), matching `SyncTickJob`/`SyncRunRetentionJob`.
- The `jiraunpacked_test` Postgres runs in Docker container `jiraunpacked-db`. If tests can't connect, `docker start jiraunpacked-db`, wait, re-run — a connection failure is not a code failure.

**Existing shapes this plan builds on (verified against source):**
- `Program.cs:74-83` registers one global `AddFixedWindowLimiter("auth", …)`; `app.UseHttpsRedirection()` at `:106`, no `UseHsts`; `UseRateLimiter()` at `:107`. The Quartz block is gated by `if (builder.Configuration.GetValue("Sync:SchedulerEnabled", true))` (`:31-46`) and already registers `SyncTickJob` + `SyncRunRetentionJob`.
- `SyncController` (`api/projects/{projectId:guid}/sync` and `/rederive`) is `[ApiController][Authorize]`; `SyncOrchestrator` injects `ILogger<SyncOrchestrator>`, `IClock _clock`; its `RunProjectAsync` catch (`:106-123`) already does the JiraApiException-vs-generic guard and sets `NextRunAt`; `RunRederiveAsync` catch (`:150-153`) does `run.Error = ex.Message` unconditionally.
- `SyncRunRetentionJob` (`Infrastructure/Retention/`) is the reaper's exact template: `[DisallowConcurrentExecution] IJob`, ctor `(IPrivilegedDbContextFactory, ITenantScope, IServiceScopeFactory, IClock, IOptions<…>, ILogger<…>)`, `Execute → RunOnceAsync`, privileged `(project, tenant)` enumerate then per-tenant `BeginScope` write.
- `SyncSchedulerOptions` (`Infrastructure/Sync/`, section `Sync`) currently holds only `bool SchedulerEnabled = true`.
- `JiraUnpackedApiFactory.ConfigureTestServices` removes all `IConfigureOptions<RateLimiterOptions>` and re-registers a no-op `AddPolicy("auth", …)` limiter.

---

## File Structure

| File | Change |
|---|---|
| `src/JiraUnpacked.Api/Program.cs` | partitioned `auth` + new `sync` limiter; gated `UseHsts`; register `StuckRunReaperJob` |
| `src/JiraUnpacked.Api/Controllers/SyncController.cs` | `[EnableRateLimiting("sync")]` on two actions |
| `src/JiraUnpacked.Api/Security/HstsGate.cs` | the `ShouldEnableHsts` predicate |
| `src/JiraUnpacked.Infrastructure/Security/SecurityOptions.cs` | `HstsEnabled` |
| `src/JiraUnpacked.Infrastructure/Sync/SyncSchedulerOptions.cs` | `+ StuckRunTimeoutMinutes` |
| `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs` | `SetFailure` helper at both catch sites |
| `src/JiraUnpacked.Infrastructure/Sync/StuckRunReaperJob.cs` | the reaper |
| `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs` | no-op the `sync` policy too |
| `tests/…/Integration/RateLimitTests.cs` | dedicated 429 test + own factory |
| `tests/…/Security/HstsGateTests.cs` | predicate unit test |
| `tests/…/Integration/RederiveErrorTests.cs` | leak regression |
| `tests/…/Integration/StuckRunReaperTests.cs` | reaper behaviour |

---

## Task 1 (SP1): Rate limiting

**Files:**
- Modify: `src/JiraUnpacked.Api/Program.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/SyncController.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/RateLimitTests.cs`

**Interfaces:**
- Consumes: existing `AddRateLimiter`/`UseRateLimiter` wiring, `SyncController`.
- Produces: policies `"auth"` (per-IP) and `"sync"` (per-user).

- [ ] **Step 1: Write the failing 429 test**

Create `tests/JiraUnpacked.Tests/Integration/RateLimitTests.cs`. It uses its **own** factory subclass that does NOT override the limiters, so the real per-IP/per-user limits apply:

```csharp
// tests/JiraUnpacked.Tests/Integration/RateLimitTests.cs
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// A factory that keeps the REAL rate limiters (the default JiraUnpackedApiFactory no-ops them
/// so the broad suite isn't throttled). Points at the test DB the same way.
/// </summary>
public sealed class RateLimitedApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Sync:SchedulerEnabled", "false");
        builder.UseSetting("ConnectionStrings:Default", JiraUnpackedApiFactory.TestConnectionString);
        builder.ConfigureTestServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddScoped<JiraUnpacked.Infrastructure.Tenancy.TenantConnectionInterceptor>();
            services.AddDbContext<AppDbContext>((sp, o) =>
                o.UseNpgsql(JiraUnpackedApiFactory.TestAppConnectionString)
                 .AddInterceptors(sp.GetRequiredService<JiraUnpacked.Infrastructure.Tenancy.TenantConnectionInterceptor>()));
        });
    }
}

public sealed class RateLimitTests : IClassFixture<RateLimitedApiFactory>, IAsyncLifetime
{
    private readonly RateLimitedApiFactory _factory;
    public RateLimitTests(RateLimitedApiFactory f) => _factory = f;
    public Task InitializeAsync() => new JiraUnpackedApiFactory().EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    private async Task<(HttpClient client, Guid projectId)> AuthedProjectAsync()
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
        var login = await c.PostAsJsonAsync("/api/auth/login", new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        // A project id that doesn't exist is fine — the limiter runs before the 404, and the point
        // is the 429, not the sync result.
        return (c, Guid.NewGuid());
    }

    [Fact]
    public async Task Sync_endpoint_returns_429_after_the_per_user_limit()
    {
        var (client, projectId) = await AuthedProjectAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++) // limit is 5/min per user
            statuses.Add((await client.PostAsJsonAsync($"/api/projects/{projectId}/sync", new { })).StatusCode);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Auth_endpoint_returns_429_after_the_per_ip_limit()
    {
        var client = _factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++) // limit is 10/min per IP
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login",
                new { emailOrUsername = "nobody@x.com", password = "wrong" })).StatusCode);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~RateLimitTests`
Expected: `Sync_endpoint_returns_429…` FAILS (no `sync` policy yet → the attribute reference won't exist until Step 4, so this compiles only after Step 4; run RED after Step 3's factory change but before Step 4's attribute — see note). Practically: apply Step 3 first, then confirm the sync test fails because no limiter is applied, then Step 4/5. The auth test fails today because the single global bucket is shared — with the current no-op-free real factory it may already 429 globally; that is acceptable, the assertion only requires a 429 to appear.

> Ordering note for the implementer: because the `sync` policy must exist before `[EnableRateLimiting("sync")]` compiles, do Steps 3–5 together, then run the tests. The RED evidence for `sync` is that removing the `[EnableRateLimiting("sync")]` attribute makes `Sync_endpoint_returns_429…` fail — verify that once, then restore.

- [ ] **Step 3: Partition the limiters in `Program.cs`**

Add `using System.Threading.RateLimiting;` to `Program.cs`. Replace the `AddRateLimiter` block (`:74-83`) with:

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Per-IP: a brute-force attempt or one chatty client can't exhaust the window for everyone.
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 10, QueueLimit = 0 }));

    // Per-user: sync/rederive are expensive and call Jira; partition by the authenticated tenant.
    options.AddPolicy("sync", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("endUserId")?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 5, QueueLimit = 0 }));
});
```

- [ ] **Step 4: Apply the `sync` policy to the endpoints**

In `SyncController.cs`, add `using Microsoft.AspNetCore.RateLimiting;` and `[EnableRateLimiting("sync")]` to the `Sync` and `Rederive` actions (not to `Runs`, the GET):

```csharp
    [HttpPost("api/projects/{projectId:guid}/sync")]
    [EnableRateLimiting("sync")]
    public async Task<IActionResult> Sync(Guid projectId, CancellationToken ct)
    // …

    [HttpPost("api/projects/{projectId:guid}/rederive")]
    [EnableRateLimiting("sync")]
    public async Task<IActionResult> Rederive(Guid projectId, CancellationToken ct)
```

- [ ] **Step 5: No-op BOTH policies in the shared test factory**

In `JiraUnpackedApiFactory.ConfigureTestServices`, the block that removes `IConfigureOptions<RateLimiterOptions>` and re-registers `AddRateLimiter(o => o.AddPolicy("auth", …GetNoLimiter…))` must ALSO register the `sync` policy as no-op — otherwise every sync/rederive integration test in the suite would begin returning 429. Update it to:

```csharp
            services.AddRateLimiter(options =>
            {
                options.AddPolicy("auth", _ => RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("sync", _ => RateLimitPartition.GetNoLimiter(string.Empty));
            });
```

Add `using System.Threading.RateLimiting;` to the factory if not present.

- [ ] **Step 6: Run tests + full suite**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~RateLimitTests` → **2 passing**.
Run: `dotnet test JiraUnpacked.sln` → full suite green — in particular the many `SyncEndpointTests`/`DerivationHistoryTests` that POST `/sync` and `/rederive` must NOT start 429ing (proves Step 5 worked). Report the count.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Api/Program.cs src/JiraUnpacked.Api/Controllers/SyncController.cs \
        tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs \
        tests/JiraUnpacked.Tests/Integration/RateLimitTests.cs
git commit -m "feat: per-IP auth limiter + per-user sync/rederive limiter (Phase 7, Spec 3)"
```

---

## Task 2 (SP2): HSTS gated by config

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Security/SecurityOptions.cs`
- Create: `src/JiraUnpacked.Api/Security/HstsGate.cs`
- Modify: `src/JiraUnpacked.Api/Program.cs`
- Test: `tests/JiraUnpacked.Tests/Security/HstsGateTests.cs`

**Interfaces:**
- Produces: `SecurityOptions { HstsEnabled }`, `HstsGate.ShouldEnableHsts(bool, bool)`.

- [ ] **Step 1: Write the failing predicate test**

Create `tests/JiraUnpacked.Tests/Security/HstsGateTests.cs`:

```csharp
using JiraUnpacked.Api.Security;

namespace JiraUnpacked.Tests.Security;

public sealed class HstsGateTests
{
    [Theory]
    [InlineData(false, true, true)]   // production + enabled  -> HSTS on
    [InlineData(false, false, false)] // production + disabled -> off (reverse-proxy escape hatch)
    [InlineData(true, true, false)]   // development           -> never
    [InlineData(true, false, false)]  // development           -> never
    public void ShouldEnableHsts_covers_all_four_cases(bool isDevelopment, bool hstsEnabled, bool expected)
        => Assert.Equal(expected, HstsGate.ShouldEnableHsts(isDevelopment, hstsEnabled));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~HstsGateTests`
Expected: compile error — `HstsGate` does not exist.

- [ ] **Step 3: Create the options and the predicate**

Create `src/JiraUnpacked.Infrastructure/Security/SecurityOptions.cs`:

```csharp
namespace JiraUnpacked.Infrastructure.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Emit HSTS in production. Default on; set false for deployments that terminate TLS at a
    /// reverse proxy and forward plain HTTP (the app then never sees HTTPS).
    /// </summary>
    public bool HstsEnabled { get; set; } = true;
}
```

Create `src/JiraUnpacked.Api/Security/HstsGate.cs`:

```csharp
namespace JiraUnpacked.Api.Security;

public static class HstsGate
{
    /// <summary>HSTS is never sent in Development; in other environments it follows the config flag.</summary>
    public static bool ShouldEnableHsts(bool isDevelopment, bool hstsEnabled) => !isDevelopment && hstsEnabled;
}
```

- [ ] **Step 4: Wire it in `Program.cs`**

Bind the options near the other `Configure` calls:
```csharp
builder.Services.Configure<JiraUnpacked.Infrastructure.Security.SecurityOptions>(
    builder.Configuration.GetSection(JiraUnpacked.Infrastructure.Security.SecurityOptions.SectionName));
```
Resolve the value after `var app = builder.Build();` and gate `UseHsts` before the existing `UseHttpsRedirection()`:
```csharp
var securityOptions = app.Services.GetRequiredService<
    Microsoft.Extensions.Options.IOptions<JiraUnpacked.Infrastructure.Security.SecurityOptions>>().Value;
if (JiraUnpacked.Api.Security.HstsGate.ShouldEnableHsts(app.Environment.IsDevelopment(), securityOptions.HstsEnabled))
{
    app.UseHsts();
}
app.UseHttpsRedirection();   // unchanged
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~HstsGateTests` → **4 passing** (the `[Theory]` cases).
Run: `dotnet build JiraUnpacked.sln --nologo` → 0 warnings / 0 errors.

> HSTS behaviour is intentionally not integration-tested: `WebApplicationFactory`'s `TestServer` serves plain HTTP and `UseHsts` emits the `Strict-Transport-Security` header only on HTTPS, so a header assertion could never pass. The predicate test covers the only branch with logic.

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Security/SecurityOptions.cs \
        src/JiraUnpacked.Api/Security/HstsGate.cs \
        src/JiraUnpacked.Api/Program.cs \
        tests/JiraUnpacked.Tests/Security/HstsGateTests.cs
git commit -m "feat: emit HSTS in production, gated by Security:HstsEnabled (Phase 7, Spec 3)"
```

---

## Task 3 (SP3): Stop rederive leaking infra detail into `SyncRun.Error`

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/RederiveErrorTests.cs`

**Interfaces:**
- Consumes: `SyncOrchestrator`'s existing `_clock`, `_logger`, `JiraApiException`.
- Produces: private `SetFailure(SyncRun, Exception, Guid)`.

- [ ] **Step 1: Write the failing regression test**

Create `tests/JiraUnpacked.Tests/Integration/RederiveErrorTests.cs`. It seeds a malformed `RawIssueSnapshot` (invalid JSON) so `DerivationService`'s `JsonDocument.Parse` throws a `JsonException` (a non-`JiraApiException`), then rederives and reads the run's error:

```csharp
// tests/JiraUnpacked.Tests/Integration/RederiveErrorTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class RederiveErrorTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public RederiveErrorTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record RunDto(Guid Id, Guid JiraProjectId, string Status, DateTime StartedAt, DateTime? FinishedAt, int IssuesProcessed, string? Error, DateTime? NextRunAt, string Kind);

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Rederive_over_malformed_raw_persists_a_generic_error_not_raw_detail()
    {
        // WireMock only for connection/config calls; no sync — we seed raw directly then rederive.
        using var jira = WireMock.Server.WireMockServer.Start();
        jira.Given(WireMock.RequestBuilders.Request.Create().WithPath("/rest/api/3/myself").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        jira.Given(WireMock.RequestBuilders.Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;

        // Resolve tenant, seed a snapshot whose Payload is invalid JSON.
        Guid tenant;
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using (var db = new AppDbContext(opts, new NullCurrentTenant()))
            tenant = await db.JiraProjects.IgnoreQueryFilters().Where(p => p.Id == project.Id).Select(p => p.EndUserId).FirstAsync();

        await using (var raw = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await raw.OpenAsync();
            // Payload is jsonb (other tests cast with ::jsonb), so it must be valid JSON to insert.
            // A bare number '123' is valid jsonb: JsonDocument.Parse succeeds, then DeriveProjectAsync's
            // root.TryGetProperty("fields", ...) throws InvalidOperationException (root is a Number, not
            // an Object) — a non-JiraApiException, which is exactly the leak path under test.
            await Exec(raw, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001',@pay::jsonb,now())",
                ("i", Guid.NewGuid()), ("t", tenant), ("p", project.Id), ("pay", "123"));
        }

        await client.PostAsJsonAsync($"/api/projects/{project.Id}/rederive", new { });

        var runs = await (await client.GetAsync($"/api/projects/{project.Id}/sync-runs")).Content.ReadFromJsonAsync<List<RunDto>>();
        var rederive = Assert.Single(runs!, r => r.Kind == "Rederive");
        Assert.Equal("Failed", rederive.Status);
        Assert.Equal("Sync failed unexpectedly.", rederive.Error);   // generic, not the raw JsonException text
    }
}
```

> **If `'123'` does not throw** (e.g. a future derivation change tolerates a non-object root), the invariant under test is unchanged — seed whatever valid-jsonb payload deterministically throws a non-`JiraApiException` in `DeriveProjectAsync` and keep the assertion: **a non-`JiraApiException` during rederive must yield `Error == "Sync failed unexpectedly."`**. Confirm the throw by running the RED step before applying the fix.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~RederiveErrorTests`
Expected: FAIL — pre-fix, `RunRederiveAsync` sets `run.Error = ex.Message`, so the stored error is the raw parser message, not `"Sync failed unexpectedly."`.

- [ ] **Step 3: Extract the shared `SetFailure` helper**

In `SyncOrchestrator.cs`, add:
```csharp
    // Sets the run's failure fields, keeping raw infrastructure detail out of the tenant-readable
    // Error. JiraApiException messages are curated + token-free; anything else is logged server-side
    // and replaced with a generic message.
    private void SetFailure(SyncRun run, Exception ex, Guid projectId)
    {
        run.Status = SyncRunStatus.Failed;
        run.FinishedAt = _clock.UtcNow;
        if (ex is JiraApiException)
        {
            run.Error = ex.Message;
        }
        else
        {
            _logger.LogError(ex, "Unexpected failure syncing project {ProjectId}", projectId);
            run.Error = "Sync failed unexpectedly.";
        }
    }
```

Replace `RunProjectAsync`'s catch body — which currently sets `Status`/`Error`/`FinishedAt` inline and then `NextRunAt` — with:
```csharp
        catch (Exception ex)
        {
            SetFailure(run, ex, projectId);
            run.NextRunAt = _clock.UtcNow.AddMinutes(project.SyncIntervalMinutes);
            await _db.SaveChangesAsync(ct);
        }
```

Replace `RunRederiveAsync`'s catch body:
```csharp
        catch (Exception ex)
        {
            SetFailure(run, ex, projectId);
            await _db.SaveChangesAsync(ct);
        }
```
(Rederive has no `NextRunAt` — it did not set one before, and does not now.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~RederiveErrorTests` → **1 passing**.
Run: `dotnet test JiraUnpacked.sln` → the existing sync failure-path tests (`SyncOrchestratorTests`/`DerivationHistoryTests`) stay green — the behaviour for `RunProjectAsync` is unchanged. Report the count.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Sync/SyncOrchestrator.cs \
        tests/JiraUnpacked.Tests/Integration/RederiveErrorTests.cs
git commit -m "fix: rederive uses the same safe-error path as sync, no raw detail leak (Phase 7, Spec 3)"
```

---

## Task 4 (SP4): Stuck-run reaper

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Sync/SyncSchedulerOptions.cs`
- Create: `src/JiraUnpacked.Infrastructure/Sync/StuckRunReaperJob.cs`
- Modify: `src/JiraUnpacked.Api/Program.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/StuckRunReaperTests.cs`

**Interfaces:**
- Consumes: `IPrivilegedDbContextFactory`, `ITenantScope`, `IServiceScopeFactory`, `IClock`, `SyncSchedulerOptions`.
- Produces: `StuckRunReaperJob.RunOnceAsync(CancellationToken)`.

- [ ] **Step 1: Add the timeout option**

In `SyncSchedulerOptions.cs`:
```csharp
public sealed class SyncSchedulerOptions
{
    public const string SectionName = "Sync";
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>A Running run older than this is a crashed/stuck sync — the reaper marks it Failed,
    /// unblocking the project (the partial unique index permits one Running run per project).</summary>
    public int StuckRunTimeoutMinutes { get; set; } = 120;
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Integration/StuckRunReaperTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/StuckRunReaperTests.cs
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Sync;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class StuckRunReaperTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public StuckRunReaperTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static AppDbContext Privileged() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options, new NullCurrentTenant());

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

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

    private static async Task SeedRunAsync(Guid tenant, Guid projectId, DateTime startedAt) // Running
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,@s,'Running','Sync',0)",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projectId), ("s", startedAt));
    }

    private StuckRunReaperJob NewJob(IServiceScope scope) =>
        ActivatorUtilities.CreateInstance<StuckRunReaperJob>(scope.ServiceProvider);

    [Fact]
    public async Task Old_running_run_is_marked_failed()
    {
        var (tenant, projectId) = await SeedProjectAsync();
        await SeedRunAsync(tenant, projectId, DateTime.UtcNow.AddMinutes(-300)); // well past 120m default

        using (var scope = _factory.Services.CreateScope())
            await NewJob(scope).RunOnceAsync(CancellationToken.None);

        await using var db = Privileged();
        var run = await db.SyncRuns.IgnoreQueryFilters().SingleAsync(r => r.JiraProjectId == projectId);
        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.NotNull(run.FinishedAt);
        // And the project is unblocked: no Running row remains, so the partial index would admit a new one.
        Assert.False(await db.SyncRuns.IgnoreQueryFilters().AnyAsync(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Running));
    }

    [Fact]
    public async Task Recent_running_run_is_left_alone()
    {
        var (tenant, projectId) = await SeedProjectAsync();
        await SeedRunAsync(tenant, projectId, DateTime.UtcNow.AddMinutes(-5)); // an in-flight sync

        using (var scope = _factory.Services.CreateScope())
            await NewJob(scope).RunOnceAsync(CancellationToken.None);

        await using var db = Privileged();
        var run = await db.SyncRuns.IgnoreQueryFilters().SingleAsync(r => r.JiraProjectId == projectId);
        Assert.Equal(SyncRunStatus.Running, run.Status);   // never kill an in-flight run
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~StuckRunReaperTests`
Expected: compile error — `StuckRunReaperJob` does not exist.

- [ ] **Step 4: Implement the reaper**

Create `src/JiraUnpacked.Infrastructure/Sync/StuckRunReaperJob.cs` (mirrors `SyncRunRetentionJob`'s isolation posture: privileged read of `(project, tenant)`, per-tenant write inside `BeginScope`):

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace JiraUnpacked.Infrastructure.Sync;

/// <summary>
/// Marks stuck Running runs (crashed syncs) as Failed so they stop blocking the project — the
/// partial unique index permits one Running run per project, and retention never purges Running.
/// Enumerates (project, tenant) with the privileged context (metadata only), then updates inside
/// each tenant's scope: a privileged READER, no privileged writer.
/// </summary>
[DisallowConcurrentExecution]
public sealed class StuckRunReaperJob : IJob
{
    private readonly IPrivilegedDbContextFactory _privileged;
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly SyncSchedulerOptions _options;
    private readonly ILogger<StuckRunReaperJob> _logger;

    public StuckRunReaperJob(
        IPrivilegedDbContextFactory privileged, ITenantScope tenantScope,
        IServiceScopeFactory scopeFactory, IClock clock,
        IOptions<SyncSchedulerOptions> options, ILogger<StuckRunReaperJob> logger)
    {
        _privileged = privileged;
        _tenantScope = tenantScope;
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context) => RunOnceAsync(context.CancellationToken);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var cutoff = _clock.UtcNow.AddMinutes(-_options.StuckRunTimeoutMinutes);

        List<(Guid ProjectId, Guid EndUserId)> projects;
        await using (var db = _privileged.Create())
        {
            var rows = await db.JiraProjects.IgnoreQueryFilters()
                .Select(p => new { p.Id, p.EndUserId })
                .ToListAsync(ct);
            projects = rows.Select(x => (x.Id, x.EndUserId)).ToList();
        }

        foreach (var (projectId, endUserId) in projects)
        {
            try
            {
                using (_tenantScope.BeginScope(endUserId))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var stuck = await db.SyncRuns
                        .Where(r => r.JiraProjectId == projectId
                                    && r.Status == SyncRunStatus.Running
                                    && r.StartedAt < cutoff)
                        .ToListAsync(ct);

                    if (stuck.Count == 0) continue;

                    foreach (var run in stuck)
                    {
                        run.Status = SyncRunStatus.Failed;
                        run.Error = "Marked failed: exceeded maximum runtime (stuck-run reaper).";
                        run.FinishedAt = _clock.UtcNow;
                    }
                    await db.SaveChangesAsync(ct);
                    _logger.LogWarning("Reaped {Count} stuck sync run(s) for project {ProjectId}.", stuck.Count, projectId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stuck-run reaper failed for project {ProjectId}.", projectId);
            }
        }
    }
}
```

- [ ] **Step 5: Register it in the Quartz block**

In `Program.cs`, inside the existing `if (builder.Configuration.GetValue("Sync:SchedulerEnabled", true))` block (alongside `sync-tick` and `sync-run-retention`), add:
```csharp
        var reaperKey = new Quartz.JobKey("stuck-run-reaper");
        q.AddJob<JiraUnpacked.Infrastructure.Sync.StuckRunReaperJob>(reaperKey);
        q.AddTrigger(t => t.ForJob(reaperKey)
            .WithSimpleSchedule(s => s.WithIntervalInHours(1).RepeatForever()));
```
Being inside that block means the existing `Sync:SchedulerEnabled=false` test setting keeps it from running during the suite.

- [ ] **Step 6: Run tests + suite**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~StuckRunReaperTests` → **2 passing**.
Run: `dotnet test JiraUnpacked.sln -c Release` → full suite green; report the count.
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → `No changes…` (no schema change).
Run: `dotnet build JiraUnpacked.sln -c Release --nologo` → 0 warnings / 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Sync/SyncSchedulerOptions.cs \
        src/JiraUnpacked.Infrastructure/Sync/StuckRunReaperJob.cs \
        src/JiraUnpacked.Api/Program.cs \
        tests/JiraUnpacked.Tests/Integration/StuckRunReaperTests.cs
git commit -m "feat: reap stuck Running sync runs after a timeout (Phase 7, Spec 3)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green (report the count).
- [ ] `dotnet ef migrations has-pending-model-changes …` → "No changes" (this spec adds none).
- [ ] Grep the new production code for `IgnoreQueryFilters`/`FromSql` — the only new occurrence is the reaper's read-only `(project, tenant)` enumeration; confirm the reaper's WRITE runs inside `BeginScope` (no privileged writer).
- [ ] Confirm `TenantIsolationGateTests.cs` is unmodified (`git log develop..HEAD -- <that path>` empty) and green.
- [ ] Confirm the branch adds **no migration** and **no new tenant table**.
- [ ] Confirm the broad suite's sync/rederive tests did not start 429ing (SP1 Step 5 held).
- [ ] Push `feat/security-posture`; PR → develop; CI green; squash-merge; then `develop → main` (merge-commit). Before the release, `git log --oneline origin/develop..origin/main --no-merges` — non-empty means a direct-to-main follow-up must be back-merged first.

## Notes for execution

- **SP1's test-factory update is load-bearing.** Forgetting it makes dozens of unrelated sync tests fail with 429 — that is a plan-known trap, not a real regression.
- **SP3's helper must not save.** Each caller keeps its own `SaveChangesAsync` (and `RunProjectAsync` keeps setting `NextRunAt`).
- **The reaper never touches raw stores and never writes privileged** — the write is inside `BeginScope`, under RLS.
- **Deferred, not in this spec:** the SR2 EF-catch race-path test, the unreachable 401-`sub` test, the sprint take-last heuristic, the empty-embedded-changelog edge, `RemoveRange → ExecuteDelete`, and reverse-proxy forwarded-headers support.
- This spec **closes Phase 7 and the v1 build.**
