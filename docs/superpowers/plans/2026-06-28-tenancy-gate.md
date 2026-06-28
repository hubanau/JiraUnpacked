# Tenant Isolation Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build two-layer tenant isolation (EF Core global query filter + Postgres RLS) keyed to the `endUserId` claim, on a first tenant-scoped table (`JiraConnection`), and prove it with the FR-B2 any-read-path gate test.

**Architecture:** `ICurrentTenant` resolves the tenant from the JWT `endUserId` claim. Layer 1: `AppDbContext` applies a global query filter on `EndUserId` to every `ITenantOwned` entity and auto-stamps `EndUserId` on insert. Layer 2: the runtime context connects as a non-superuser `NOBYPASSRLS` role (`jiraunpacked_app`) with a Postgres RLS policy reading a per-connection `app.current_tenant` GUC set by an EF connection interceptor. The gate test proves each layer independently.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, Postgres 16 RLS, xUnit + `WebApplicationFactory`.

## Global Constraints

- Target framework `net8.0`; EF Core + Npgsql pinned to `8.0.11`.
- Tenant filtering is enforced centrally (global query filter), never hand-written `WHERE` per query (FR-B2).
- The Jira API token is **not** returned in any API response.
- Column identifiers are EF's PascalCase, quoted (`"EndUserId"`); RLS SQL must use the quoted names.
- Every commit message ends with a blank line then `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- Local Docker Postgres must be running for integration tests (`docker compose up -d`).

**Scope:** Plan 9 — Phase 3 tenancy (#11/#12/#14/#13). Spec: `docs/superpowers/specs/2026-06-28-tenancy-isolation-design.md`. **Task 5 is the hard gate; Phase 4 (Jira ingestion) does not start until it is green.**

---

## File Structure

- Create `src/JiraUnpacked.Domain/Abstractions/ITenantOwned.cs`, `ICurrentTenant.cs`.
- Create `src/JiraUnpacked.Domain/Entities/JiraConnection.cs`.
- Create `src/JiraUnpacked.Infrastructure/Persistence/Configurations/JiraConnectionConfiguration.cs`.
- Create `src/JiraUnpacked.Infrastructure/Tenancy/NullCurrentTenant.cs`, `TenantConnectionInterceptor.cs`.
- Create `src/JiraUnpacked.Api/Tenancy/HttpContextCurrentTenant.cs`.
- Create `src/JiraUnpacked.Infrastructure/Services/JiraConnectionService.cs` + `src/JiraUnpacked.Domain/Abstractions/IJiraConnectionService.cs`.
- Create `src/JiraUnpacked.Api/Contracts/Connections/*.cs`, `src/JiraUnpacked.Api/Controllers/ConnectionsController.cs`.
- Modify `AppDbContext.cs` (tenant ctor, filter, stamp, DbSet), `AppDbContextFactory.cs` (null tenant), `DependencyInjection.cs`, `Program.cs`, `appsettings.Development.json`, the integration test factory.
- Generated migrations: `*_AddJiraConnections`, `*_AddTenantRls`.
- Tests: `tests/.../Tenancy/CurrentTenantStampTests.cs`, `tests/.../Tenancy/TenantIsolationGateTests.cs`.

---

## Task 1: ITenantOwned + JiraConnection entity + migration (#11)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/ITenantOwned.cs`
- Create: `src/JiraUnpacked.Domain/Entities/JiraConnection.cs`
- Create: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/JiraConnectionConfiguration.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs` (add DbSet)
- Generated: `src/JiraUnpacked.Infrastructure/Persistence/Migrations/*_AddJiraConnections.cs`

**Interfaces:**
- Produces: `ITenantOwned { Guid EndUserId { get; set; } }`; `JiraConnection : ITenantOwned` with `Id, EndUserId, BaseUrl, Email, ApiTokenEncrypted, CreatedAt`; `AppDbContext.JiraConnections`.

- [ ] **Step 1: Create the tenant-ownership marker**

`src/JiraUnpacked.Domain/Abstractions/ITenantOwned.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>A row scoped to a single tenant (End User), discriminated by EndUserId.</summary>
public interface ITenantOwned
{
    Guid EndUserId { get; set; }
}
```

- [ ] **Step 2: Create the entity**

`src/JiraUnpacked.Domain/Entities/JiraConnection.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A Jira Cloud connection owned by an End User (tenant). The first tenant-scoped
/// table — pulled forward minimally to enforce + prove isolation (FR-B1/B2).
/// Token encryption + Jira validation arrive in Phase 4.
/// </summary>
public class JiraConnection : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public string BaseUrl { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string ApiTokenEncrypted { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
```

- [ ] **Step 3: Create the configuration**

`src/JiraUnpacked.Infrastructure/Persistence/Configurations/JiraConnectionConfiguration.cs`:
```csharp
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class JiraConnectionConfiguration : IEntityTypeConfiguration<JiraConnection>
{
    public void Configure(EntityTypeBuilder<JiraConnection> builder)
    {
        builder.ToTable("JiraConnections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.BaseUrl).IsRequired().HasMaxLength(2048);
        builder.Property(c => c.Email).IsRequired().HasMaxLength(256);
        builder.Property(c => c.ApiTokenEncrypted).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();

        builder.HasIndex(c => c.EndUserId);
    }
}
```

- [ ] **Step 4: Add the DbSet**

In `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs`, add alongside the other `DbSet`s:
```csharp
    public DbSet<JiraConnection> JiraConnections => Set<JiraConnection>();
```

- [ ] **Step 5: Generate + apply the migration**

```bash
dotnet ef migrations add AddJiraConnections --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure --output-dir Persistence/Migrations
dotnet ef database update --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```
Expected: the `Up()` creates `JiraConnections` (uuid `Id` PK, uuid `EndUserId`, text columns, timestamptz `CreatedAt`, index `IX_JiraConnections_EndUserId`); `database update` → `Done.`

- [ ] **Step 6: Verify build + no drift**

`dotnet build JiraUnpacked.sln -c Debug` → 0 errors; `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → no changes.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/ITenantOwned.cs src/JiraUnpacked.Domain/Entities/JiraConnection.cs src/JiraUnpacked.Infrastructure/Persistence/Configurations/JiraConnectionConfiguration.cs src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs src/JiraUnpacked.Infrastructure/Persistence/Migrations/
git commit -F - <<'EOF'
feat: ITenantOwned + JiraConnection entity + migration (FR-B1)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 2: Current-tenant + EF query filter + auto-stamp (#12 mechanism)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/ICurrentTenant.cs`
- Create: `src/JiraUnpacked.Infrastructure/Tenancy/NullCurrentTenant.cs`
- Create: `src/JiraUnpacked.Api/Tenancy/HttpContextCurrentTenant.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/AppDbContextFactory.cs`
- Modify: `src/JiraUnpacked.Api/Program.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs` (standalone migrate context needs the new ctor arg)
- Test: `tests/JiraUnpacked.Tests/Tenancy/CurrentTenantStampTests.cs`

**Interfaces:**
- Consumes: `ITenantOwned`, `JiraConnection`, `IHttpContextAccessor`.
- Produces: `ICurrentTenant { Guid? EndUserId { get; } }`; `AppDbContext(DbContextOptions<AppDbContext>, ICurrentTenant)`; auto-stamp + global filter on `JiraConnection`.

- [ ] **Step 1: Create the abstraction + implementations**

`src/JiraUnpacked.Domain/Abstractions/ICurrentTenant.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>The tenant (End User) for the current request, or null when unauthenticated.</summary>
public interface ICurrentTenant
{
    Guid? EndUserId { get; }
}
```

`src/JiraUnpacked.Infrastructure/Tenancy/NullCurrentTenant.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Tenancy;

/// <summary>No tenant — used by design-time tooling, migrations, and the seeder.</summary>
public sealed class NullCurrentTenant : ICurrentTenant
{
    public Guid? EndUserId => null;
}
```

`src/JiraUnpacked.Api/Tenancy/HttpContextCurrentTenant.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Api.Tenancy;

/// <summary>Resolves the tenant from the request's `endUserId` JWT claim.</summary>
public sealed class HttpContextCurrentTenant : ICurrentTenant
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentTenant(IHttpContextAccessor accessor) => _accessor = accessor;

    public Guid? EndUserId =>
        Guid.TryParse(_accessor.HttpContext?.User.FindFirst("endUserId")?.Value, out var id)
            ? id
            : null;
}
```

- [ ] **Step 2: Make AppDbContext tenant-aware**

Replace the constructor + `OnModelCreating` region of `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs` and add the SaveChanges overrides. The class becomes:
```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    private readonly ICurrentTenant _tenant;
    private readonly Guid _currentTenantId;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant tenant) : base(options)
    {
        _tenant = tenant;
        _currentTenantId = tenant.EndUserId ?? Guid.Empty;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();
    public DbSet<EmailVerification> EmailVerifications => Set<EmailVerification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<JiraConnection> JiraConnections => Set<JiraConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Layer 1 (primary): central tenant filter on every tenant-owned entity.
        // No current tenant => _currentTenantId is Guid.Empty, which matches no real row.
        modelBuilder.Entity<JiraConnection>().HasQueryFilter(c => c.EndUserId == _currentTenantId);
    }

    public override int SaveChanges()
    {
        StampTenant();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(cancellationToken);
    }

    // Auto-stamp EndUserId on new tenant-owned rows so a caller can never create a
    // row owned by another tenant; writing one with no current tenant fails closed.
    private void StampTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>().Where(e => e.State == EntityState.Added))
        {
            entry.Entity.EndUserId = _tenant.EndUserId
                ?? throw new InvalidOperationException("A current tenant is required to create tenant-owned data.");
        }
    }
}
```

- [ ] **Step 3: Update the design-time factory to pass a null tenant**

In `src/JiraUnpacked.Infrastructure/Persistence/AppDbContextFactory.cs`, change the `return new AppDbContext(options);` line to:
```csharp
        return new AppDbContext(options, new JiraUnpacked.Infrastructure.Tenancy.NullCurrentTenant());
```

- [ ] **Step 3b: Update the test harness's standalone migrate context**

In `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs`, `EnsureDatabaseAsync` constructs an `AppDbContext` directly to migrate before the host builds. The new constructor requires an `ICurrentTenant`, so pass a `NullCurrentTenant`. Add `using JiraUnpacked.Infrastructure.Tenancy;` and change the `new AppDbContext(options)` line to:
```csharp
        await using var db = new AppDbContext(options, new NullCurrentTenant());
```

- [ ] **Step 4: Wire the HTTP tenant resolver in Program.cs**

In `src/JiraUnpacked.Api/Program.cs`, add `using JiraUnpacked.Api.Tenancy;` and `using JiraUnpacked.Domain.Abstractions;`, and register (after `builder.Services.AddInfrastructure(...)`):
```csharp
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentTenant, HttpContextCurrentTenant>();
```

- [ ] **Step 5: Write the unit test**

`tests/JiraUnpacked.Tests/Tenancy/CurrentTenantStampTests.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Tenancy;

public sealed class CurrentTenantStampTests
{
    private sealed class FixedTenant : ICurrentTenant
    {
        public FixedTenant(Guid? id) => EndUserId = id;
        public Guid? EndUserId { get; }
    }

    private static AppDbContext InMemory(ICurrentTenant tenant)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"stamp-{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options, tenant);
    }

    [Fact]
    public async Task SaveChanges_StampsEndUserId_FromCurrentTenant()
    {
        var tenantId = Guid.NewGuid();
        await using var db = InMemory(new FixedTenant(tenantId));

        db.JiraConnections.Add(new JiraConnection
        {
            Id = Guid.NewGuid(), BaseUrl = "https://x.atlassian.net", Email = "a@b.com",
            ApiTokenEncrypted = "t", CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var saved = await db.JiraConnections.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(tenantId, saved.EndUserId);
    }

    [Fact]
    public async Task SaveChanges_WithoutTenant_Throws()
    {
        await using var db = InMemory(new FixedTenant(null));
        db.JiraConnections.Add(new JiraConnection
        {
            Id = Guid.NewGuid(), BaseUrl = "https://x.atlassian.net", Email = "a@b.com",
            ApiTokenEncrypted = "t", CreatedAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
```
This needs the EF in-memory provider in the test project. If `Microsoft.EntityFrameworkCore.InMemory` is not already referenced, add it:
```bash
dotnet add tests/JiraUnpacked.Tests package Microsoft.EntityFrameworkCore.InMemory --version 8.0.11
```

- [ ] **Step 6: Build + run the unit test**

`dotnet build JiraUnpacked.sln -c Debug` then `dotnet test --filter FullyQualifiedName~CurrentTenantStampTests` → 2 passed.

- [ ] **Step 7: Run the full suite** (existing integration tests build the API host, which now requires `ICurrentTenant` — confirm DI resolves)

`dotnet test JiraUnpacked.sln -c Debug` → 60 passed (58 existing + 2 new).

- [ ] **Step 8: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/ICurrentTenant.cs src/JiraUnpacked.Infrastructure/Tenancy/NullCurrentTenant.cs src/JiraUnpacked.Api/Tenancy/HttpContextCurrentTenant.cs src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs src/JiraUnpacked.Infrastructure/Persistence/AppDbContextFactory.cs src/JiraUnpacked.Api/Program.cs tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs tests/JiraUnpacked.Tests/Tenancy/CurrentTenantStampTests.cs tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj
git commit -F - <<'EOF'
feat: tenant query filter + auto-stamp + current-tenant resolver (FR-B2 layer 1)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 3: JiraConnectionService + /api/connections endpoints (#12 read path)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IJiraConnectionService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/JiraConnectionService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Create: `src/JiraUnpacked.Api/Contracts/Connections/CreateConnectionRequest.cs`, `ConnectionResponse.cs`
- Create: `src/JiraUnpacked.Api/Controllers/ConnectionsController.cs`

**Interfaces:**
- Consumes: `AppDbContext.JiraConnections` (tenant-filtered), `IClock`.
- Produces: `IJiraConnectionService.CreateAsync(string baseUrl, string email, string apiToken, CancellationToken) : Task<ConnectionView>`, `ListAsync(CancellationToken) : Task<IReadOnlyList<ConnectionView>>`, `GetAsync(Guid id, CancellationToken) : Task<ConnectionView?>`; `record ConnectionView(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc)`.

- [ ] **Step 1: Create the abstraction**

`src/JiraUnpacked.Domain/Abstractions/IJiraConnectionService.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

public sealed record ConnectionView(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);

public interface IJiraConnectionService
{
    Task<ConnectionView> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectionView>> ListAsync(CancellationToken ct = default);
    Task<ConnectionView?> GetAsync(Guid id, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create the implementation**

`src/JiraUnpacked.Infrastructure/Services/JiraConnectionService.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class JiraConnectionService : IJiraConnectionService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public JiraConnectionService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ConnectionView> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default)
    {
        // EndUserId is auto-stamped from the current tenant on SaveChanges.
        var connection = new JiraConnection
        {
            Id = Guid.NewGuid(),
            BaseUrl = baseUrl,
            Email = email,
            ApiTokenEncrypted = apiToken, // plain for now; KMS-encrypted in Phase 4
            CreatedAt = _clock.UtcNow
        };
        _db.JiraConnections.Add(connection);
        await _db.SaveChangesAsync(ct);
        return ToView(connection);
    }

    public async Task<IReadOnlyList<ConnectionView>> ListAsync(CancellationToken ct = default) =>
        await _db.JiraConnections
            .OrderBy(c => c.CreatedAt)
            .Select(c => new ConnectionView(c.Id, c.BaseUrl, c.Email, c.CreatedAt))
            .ToListAsync(ct);

    public async Task<ConnectionView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await _db.JiraConnections.FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? null : ToView(c);
    }

    private static ConnectionView ToView(JiraConnection c) => new(c.Id, c.BaseUrl, c.Email, c.CreatedAt);
}
```

- [ ] **Step 3: Register the service**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, after the last service registration:
```csharp
        services.AddScoped<IJiraConnectionService, JiraConnectionService>();
```

- [ ] **Step 4: Create the DTOs**

`src/JiraUnpacked.Api/Contracts/Connections/CreateConnectionRequest.cs`:
```csharp
namespace JiraUnpacked.Api.Contracts.Connections;

public sealed record CreateConnectionRequest(string BaseUrl, string Email, string ApiToken);
```
`src/JiraUnpacked.Api/Contracts/Connections/ConnectionResponse.cs`:
```csharp
namespace JiraUnpacked.Api.Contracts.Connections;

public sealed record ConnectionResponse(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
```

- [ ] **Step 5: Create the controller**

`src/JiraUnpacked.Api/Controllers/ConnectionsController.cs`:
```csharp
using JiraUnpacked.Api.Contracts.Connections;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Route("api/connections")]
[Authorize]
public sealed class ConnectionsController : ControllerBase
{
    private readonly IJiraConnectionService _connections;

    public ConnectionsController(IJiraConnectionService connections) => _connections = connections;

    [HttpPost]
    public async Task<IActionResult> Create(CreateConnectionRequest request, CancellationToken ct)
    {
        var view = await _connections.CreateAsync(request.BaseUrl, request.Email, request.ApiToken, ct);
        return CreatedAtAction(nameof(Get), new { id = view.Id }, ToResponse(view));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var views = await _connections.ListAsync(ct);
        return Ok(views.Select(ToResponse));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var view = await _connections.GetAsync(id, ct);
        return view is null ? NotFound() : Ok(ToResponse(view));
    }

    private static ConnectionResponse ToResponse(ConnectionView v) =>
        new(v.Id, v.BaseUrl, v.Email, v.CreatedAtUtc);
}
```

- [ ] **Step 6: Build**

`dotnet build JiraUnpacked.sln -c Debug` → 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IJiraConnectionService.cs src/JiraUnpacked.Infrastructure/Services/JiraConnectionService.cs src/JiraUnpacked.Infrastructure/DependencyInjection.cs src/JiraUnpacked.Api/Contracts/Connections/ src/JiraUnpacked.Api/Controllers/ConnectionsController.cs
git commit -F - <<'EOF'
feat: /api/connections endpoints (tenant-filtered read path)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 4: Postgres RLS layer (#14)

**Files:**
- Generated: `src/JiraUnpacked.Infrastructure/Persistence/Migrations/*_AddTenantRls.cs` (edited to raw SQL)
- Create: `src/JiraUnpacked.Infrastructure/Tenancy/TenantConnectionInterceptor.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Modify: `src/JiraUnpacked.Api/appsettings.Development.json`

**Interfaces:**
- Consumes: `ICurrentTenant`, the `JiraConnections` table.
- Produces: the `jiraunpacked_app` role + RLS policy; the `app.current_tenant` GUC set per connection; runtime `AppDbContext` connects as `jiraunpacked_app`.

- [ ] **Step 1: Generate an empty migration and fill it with RLS SQL**

```bash
dotnet ef migrations add AddTenantRls --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure --output-dir Persistence/Migrations
```
Then replace the `Up` and `Down` bodies of the generated `*_AddTenantRls.cs` with:
```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'jiraunpacked_app') THEN
        CREATE ROLE jiraunpacked_app LOGIN PASSWORD 'localapp' NOBYPASSRLS;
    END IF;
END$$;

GRANT USAGE ON SCHEMA public TO jiraunpacked_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO jiraunpacked_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO jiraunpacked_app;

ALTER TABLE ""JiraConnections"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""JiraConnections"" FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON ""JiraConnections""
    USING (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS tenant_isolation ON ""JiraConnections"";
ALTER TABLE ""JiraConnections"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""JiraConnections"" DISABLE ROW LEVEL SECURITY;
");
        }
```
(The role + grants are intentionally not dropped in `Down` — a login role with grants is environment infrastructure.)

- [ ] **Step 2: Apply the migration (as the privileged role)**

```bash
dotnet ef database update --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```
Expected: `Applying migration '..._AddTenantRls'.` then `Done.` (Runs on the `Default`/privileged connection.)

- [ ] **Step 3: Create the connection interceptor that sets the GUC**

`src/JiraUnpacked.Infrastructure/Tenancy/TenantConnectionInterceptor.cs`:
```csharp
using System.Data.Common;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JiraUnpacked.Infrastructure.Tenancy;

/// <summary>
/// Sets the `app.current_tenant` Postgres session variable on every connection open,
/// so Row-Level Security filters to the current tenant (or to nothing when unset).
/// Setting it on every open also prevents leakage across pooled connections.
/// </summary>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ICurrentTenant _tenant;

    public TenantConnectionInterceptor(ICurrentTenant tenant) => _tenant = tenant;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => SetTenant(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await SetTenantAsync(connection, cancellationToken);

    private void SetTenant(DbConnection connection)
    {
        using var cmd = CreateCommand(connection);
        cmd.ExecuteNonQuery();
    }

    private async Task SetTenantAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant', @tenant, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "tenant";
        p.Value = _tenant.EndUserId?.ToString() ?? string.Empty;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
```

- [ ] **Step 4: Switch the runtime DbContext to the App role + interceptor**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, add `using JiraUnpacked.Infrastructure.Tenancy;`, register the interceptor, and change the `AppDbContext` registration to use the `App` connection string and the interceptor. Replace the existing `services.AddDbContext<AppDbContext>(...)` line with:
```csharp
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) =>
            options.UseNpgsql(configuration.GetConnectionString("App") ?? connectionString)
                   .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
```
This requires `AddInfrastructure` to have access to `IConfiguration`. The method signature is `AddInfrastructure(this IServiceCollection services, string? connectionString)` today. Change it to also accept configuration: `AddInfrastructure(this IServiceCollection services, IConfiguration configuration)`, derive `var connectionString = configuration.GetConnectionString("Default");` at the top (keep the null-guard), and update the single call in `Program.cs` from `AddInfrastructure(builder.Configuration.GetConnectionString("Default"))` to `AddInfrastructure(builder.Configuration)`. Add `using Microsoft.Extensions.Configuration;` to `DependencyInjection.cs`.

(Migrations + the design-time factory continue to use `Default` via `AppDbContextFactory`, which is unchanged and connects as the privileged role.)

- [ ] **Step 5: Add the App connection string for local dev**

In `src/JiraUnpacked.Api/appsettings.Development.json`, add an `App` entry under `ConnectionStrings` (sibling of `Default`):
```json
    "App": "Host=localhost;Port=5432;Database=jiraunpacked;Username=jiraunpacked_app;Password=localapp"
```

- [ ] **Step 6: Build + full suite**

`dotnet build JiraUnpacked.sln -c Debug` → 0 errors. `dotnet test JiraUnpacked.sln -c Debug` → still 60 passing (the integration test factory still targets the privileged role until Task 5 switches it; behavior unchanged).

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Persistence/Migrations/ src/JiraUnpacked.Infrastructure/Tenancy/TenantConnectionInterceptor.cs src/JiraUnpacked.Infrastructure/DependencyInjection.cs src/JiraUnpacked.Api/Program.cs src/JiraUnpacked.Api/appsettings.Development.json
git commit -F - <<'EOF'
feat: Postgres RLS layer — jiraunpacked_app role, policy, tenant GUC interceptor (FR-B2 layer 2)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 5: FR-B2 tenant-isolation gate test (#13) — THE GATE

**Files:**
- Modify: `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs` (runtime context → App role)
- Create: `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs`

**Interfaces:**
- Consumes: `/api/auth/login`, `/api/connections`, the `jiraunpacked_app` role + RLS.

- [ ] **Step 1: Point the test factory's runtime DbContext at the App (RLS-enforced) role**

In `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs`: add an App test connection string constant, and in `ConfigureTestServices` change the `AddDbContext<AppDbContext>` override to use it **plus** the interceptor (resolved from DI). Add a using `using JiraUnpacked.Infrastructure.Tenancy;`. The connection override becomes:
```csharp
            services.AddScoped<TenantConnectionInterceptor>();
            services.AddDbContext<AppDbContext>((sp, options) =>
                options.UseNpgsql(TestAppConnectionString)
                       .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
```
And add the constant near `TestConnectionString`:
```csharp
    // Runtime role: RLS-enforced (set up by the AddTenantRls migration).
    public const string TestAppConnectionString =
        "Host=localhost;Port=5432;Database=jiraunpacked_test;Username=jiraunpacked_app;Password=localapp";
```
`EnsureDatabaseAsync` is unchanged — it migrates `jiraunpacked_test` as the privileged role (which creates the role, grants, and RLS), so the App role exists before any request.

- [ ] **Step 2: Write the gate test**

`tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Tenancy;

public sealed class TenantIsolationGateTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public TenantIsolationGateTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);

    private const string Password = "Correct-Horse-9!";

    private async Task<HttpClient> NewTenantClientAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = $"tenant-{unique}@example.com",
                Username = $"tenant-{unique}",
                PasswordHash = hasher.Hash(Password),
                Role = UserRole.EndUser,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"tenant-{unique}@example.com", password = Password });
        var tokens = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<ConnectionDto> CreateConnectionAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = "https://acme.atlassian.net", email = "ops@acme.com", apiToken = "secret-token" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ConnectionDto>())!;
    }

    [Fact]
    public async Task Tenant_CannotReadAnotherTenantsConnections_ThroughAnyReadPath()
    {
        var a = await NewTenantClientAsync();
        var b = await NewTenantClientAsync();

        var aConn = await CreateConnectionAsync(a);
        var bConn = await CreateConnectionAsync(b);

        // List (both layers): B sees only B's connection.
        var bList = await (await b.GetAsync("/api/connections"))
            .Content.ReadFromJsonAsync<List<ConnectionDto>>();
        Assert.Single(bList!);
        Assert.Equal(bConn.Id, bList![0].Id);
        Assert.DoesNotContain(bList, c => c.Id == aConn.Id);

        // Get-by-id (both layers): B cannot fetch A's connection → 404 (no existence leak).
        var bGetA = await b.GetAsync($"/api/connections/{aConn.Id}");
        Assert.Equal(HttpStatusCode.NotFound, bGetA.StatusCode);

        // RLS proven independently of EF: connect as the app role, set the GUC to B,
        // and query A's row directly — RLS must hide it even with no EF filter involved.
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestAppConnectionString);
        await conn.OpenAsync();
        var bTenantId = await TenantOf(bConn.Id);
        await SetTenantAsync(conn, bTenantId);

        Assert.Equal(0L, await CountConnectionById(conn, aConn.Id)); // A hidden from B at the DB
        Assert.Equal(1L, await CountConnectionById(conn, bConn.Id)); // B sees its own row (sanity)
    }

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant', @t, false)";
        cmd.Parameters.AddWithValue("t", tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    // Reads the owning tenant of a connection using a privileged connection (bypasses RLS for setup only).
    private async Task<Guid> TenantOf(Guid connectionId)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""EndUserId"" FROM ""JiraConnections"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", connectionId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountConnectionById(NpgsqlConnection conn, Guid id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT count(*) FROM ""JiraConnections"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
```

- [ ] **Step 3: Run the gate test**

Run: `dotnet test --filter FullyQualifiedName~TenantIsolationGateTests`
Expected: PASS — 1 passed. **This is the gate.** If it fails, isolation is not proven — stop and fix before anything else.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test JiraUnpacked.sln -c Debug`
Expected: 61 passed (60 + 1).

- [ ] **Step 5: Commit**

```bash
git add tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs
git commit -F - <<'EOF'
test: FR-B2 tenant-isolation gate — no cross-tenant read via any path (both layers)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Final verification

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings, 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → 61 passing (incl. the gate, run as the RLS-enforced role).
- [ ] `dotnet ef migrations has-pending-model-changes ...` → no drift.
- [ ] **Push the branch** before the PR — `origin` tip equals local HEAD.
- [ ] Open PR → `develop`; confirm CI (Postgres service, both migrations incl. the role) is green; merge. This closes #11/#12/#13/#14 and **opens Phase 4** (the gate is green).

---

## Roadmap (next)
- **Phase 4 — Connection & semantic layer** (FR-C1…C5): real connection validation against Jira + KMS-encrypted token (FR-C1), projects per connection, JQL queries, sync frequency, and the field/status semantic mapping. Every new tenant-scoped table implements `ITenantOwned` and inherits both isolation layers established here.
