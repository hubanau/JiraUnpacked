# Phase 7 Spec 1 — Tenant Data Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Account deletion that erases every row of a tenant's data, guaranteed by the schema and proved by a gate test, plus a documented retention policy with a safe `SyncRun` purge.

**Architecture:** Every `ITenantOwned` entity gets an `ON DELETE CASCADE` FK from `EndUserId` to `Users.Id`, wired centrally in `OnModelCreating` so future tables inherit it. Deleting the `User` row inside `ITenantScope.BeginScope(targetUserId)` lets Postgres remove everything under RLS in one transaction. A non-tenant-owned `DeletedAccount` tombstone records that the erasure happened.

**Tech Stack:** ASP.NET Core 8 Web API, EF Core 8 + Npgsql/PostgreSQL, Quartz.NET, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-22-tenant-data-lifecycle-design.md` (commit bdbe6dd).
**Branch:** `feat/tenant-data-lifecycle`, cut from `develop` @ b80a23e.

## Global Constraints

- **No `IgnoreQueryFilters`, privileged factory, `FromSql`, or `.Include(...)` in production READ paths.** Tests may use `IgnoreQueryFilters` for cross-tenant assertions. The retention job's privileged use is a **read only** (§Task 4).
- **The FR-B2 gate test is `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs`** (299 lines). It must stay untouched and green. (`ScaffoldSanityTests.cs` merely mentions FR-B2 in a comment — it is *not* the gate; a prior review misidentified it.)
- **`ITenantScope.BeginScope(Guid endUserId)` returns `IDisposable` and MUST be entered before the first database access** in the unit of work. `TenantConnectionInterceptor` sets the `app.current_tenant` GUC when a connection *opens*; a scope entered afterwards leaves RLS denying the very rows being erased. `SyncTickJob` is the reference pattern: `using (_tenantScope.BeginScope(x)) { using var scope = _scopeFactory.CreateScope(); … }`.
- `User` and `DeletedAccount` are **not** `ITenantOwned`, so neither the query filter nor `StampTenant()` applies to them.
- EF migrations: `dotnet ef migrations add <Name> --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure`.
- The `jiraunpacked_test` Postgres runs in Docker container `jiraunpacked-db`. If tests cannot connect, `docker start jiraunpacked-db`, wait, re-run — a connection failure is not a code failure.

**Three deliberate deviations from the approved spec** (apply these, not the spec's literal text):

1. **`NotAnEndUser` returns 400, not 409.** `AdminUsersController.Map(BlockResult)` already returns **400** for the identical condition ("Only End Users can be blocked or unblocked"). Two endpoints returning different codes for the same semantic condition is worse than being semantically purer on one of them.
2. **There is no `GET /api/auth/me` 500 hazard to fix.** `AuthController.Me()` is **claims-only** — it reads `sub`/`role`/`endUserId` from the JWT and never queries the database, so a deleted user's token cannot produce a 500 there. Spec §4.3 asserted a hazard that does not exist. Task 3 instead **verifies the real post-deletion behaviour**: `/me` still returns 200 from claims, while data endpoints return 404/empty because the tenant's rows are gone. Do **not** add a database lookup to `/me` to force a 401 — that would add a round-trip to every call to satisfy a nicety, for a nil-impact window already bounded by token lifetime.
3. **The completeness gate is split into a model-level check plus a data-level check.** The spec described seeding a row in all 16 tables. A **model-level** assertion over EF's `IModel` (every `ITenantOwned` entity type has a Cascade FK to `User` on `EndUserId`) catches a future 17th table with no seeding at all, is fast, and cannot rot. The data-level test then proves the cascade actually fires end-to-end over a realistic graph. Both are required; neither alone is sufficient.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Infrastructure/Persistence/Configurations/InviteCodeConfiguration.cs` | `UsedByUserId` → `SetNull`. |
| `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs` | Central cascade loop over `ITenantOwned`; `DeletedAccounts` DbSet. |
| `src/JiraUnpacked.Domain/Entities/DeletedAccount.cs` | The tombstone (not tenant-owned). |
| `src/JiraUnpacked.Infrastructure/Persistence/Configurations/DeletedAccountConfiguration.cs` | Table mapping, no FKs. |
| `src/JiraUnpacked.Domain/Abstractions/IAccountDeletionService.cs` | Contract + outcome enum. |
| `src/JiraUnpacked.Infrastructure/Services/AccountDeletionService.cs` | Transactional erase inside the tenant scope. |
| `src/JiraUnpacked.Api/Controllers/AuthController.cs` | `DELETE /api/auth/me`. |
| `src/JiraUnpacked.Api/Controllers/AdminUsersController.cs` | `DELETE /api/admin/users/{id:guid}`. |
| `src/JiraUnpacked.Infrastructure/Retention/RetentionOptions.cs` | `Retention:Enabled`, `Retention:SyncRunDays`. |
| `src/JiraUnpacked.Infrastructure/Retention/SyncRunRetentionJob.cs` | Daily purge; privileged read, tenant-scoped writes. |
| `tests/JiraUnpacked.Tests/Tenancy/TenantDeletionGateTests.cs` | The model-level + data-level completeness gate and over-delete guard. |
| `tests/JiraUnpacked.Tests/Integration/AccountDeletionTests.cs` | Endpoint behaviour. |
| `tests/JiraUnpacked.Tests/Integration/SyncRunRetentionTests.cs` | Purge exclusions + watermark survival. |

---

## Task 1 (TL1): Cascade schema

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/InviteCodeConfiguration.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs`
- Create: migration `…AddTenantCascadeToUsers.cs` (generated)
- Test: `tests/JiraUnpacked.Tests/Tenancy/TenantDeletionGateTests.cs`

**Interfaces:**
- Consumes: `ITenantOwned` (`Guid EndUserId { get; set; }`) from `JiraUnpacked.Domain.Abstractions`; the `User` entity.
- Produces: the cascade relationships every later task relies on; `TenantDeletionGateTests` (Task 2 extends it).

- [ ] **Step 1: Write the failing model-level gate test**

Create `tests/JiraUnpacked.Tests/Tenancy/TenantDeletionGateTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Tenancy/TenantDeletionGateTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Tenancy;

/// <summary>
/// The deletion-completeness gate, sibling to the FR-B2 isolation gate. Erasure is a legal
/// obligation, so completeness cannot rest on someone remembering to wire a new table.
/// </summary>
public sealed class TenantDeletionGateTests
{
    /// <summary>Builds the model without touching a database — Model is constructed lazily from configuration.</summary>
    private static AppDbContext Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void Every_tenant_owned_entity_cascades_from_user()
    {
        using var db = Model();

        var offenders = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            var fk = entity.GetForeignKeys().FirstOrDefault(f =>
                f.PrincipalEntityType.ClrType == typeof(User)
                && f.Properties.Count == 1
                && f.Properties[0].Name == nameof(ITenantOwned.EndUserId));

            if (fk is null)
                offenders.Add($"{entity.ClrType.Name}: no FK from EndUserId to User");
            else if (fk.DeleteBehavior != DeleteBehavior.Cascade)
                offenders.Add($"{entity.ClrType.Name}: delete behaviour is {fk.DeleteBehavior}, expected Cascade");
        }

        // A new tenant-owned table added without cascade wiring fails HERE, in CI,
        // instead of silently leaving personal data behind after an erasure request.
        Assert.Empty(offenders);
    }

    [Fact]
    public void Gate_actually_examines_every_tenant_owned_entity()
    {
        // Guards the guard: if the reflection predicate ever stops matching, the test above
        // would pass vacuously over an empty set.
        using var db = Model();

        var count = db.Model.GetEntityTypes()
            .Count(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType));

        Assert.Equal(16, count);
    }

    [Fact]
    public void Invite_code_redeemer_link_does_not_block_deletion()
    {
        using var db = Model();

        var fk = db.Model.FindEntityType(typeof(InviteCode))!
            .GetForeignKeys()
            .Single(f => f.Properties.Count == 1
                         && f.Properties[0].Name == nameof(InviteCode.UsedByUserId));

        // Restrict here would make every End User who registered via a code undeletable.
        Assert.Equal(DeleteBehavior.SetNull, fk.DeleteBehavior);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~TenantDeletionGateTests`
Expected: `Every_tenant_owned_entity_cascades_from_user` FAILS listing all 16 entities as having no FK; `Invite_code_redeemer_link_does_not_block_deletion` FAILS with `Restrict`. `Gate_actually_examines_every_tenant_owned_entity` should PASS at 16 — if it reports a different number, stop: the entity set changed and this plan's assumptions need revisiting.

- [ ] **Step 3: Flip the invite-code redeemer link**

In `src/JiraUnpacked.Infrastructure/Persistence/Configurations/InviteCodeConfiguration.cs`, replace the redeemer relationship (keep the `CreatedByAdminId` one exactly as it is):

```csharp
        // Redeemer (nullable) — SetNull so an erasure request can proceed. The code row
        // survives as proof it was issued and redeemed; the link to the erased person is cut.
        // (Restrict here made every End User who registered via a code undeletable.)
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(i => i.UsedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
```

- [ ] **Step 4: Add the central cascade loop**

In `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs`, inside `OnModelCreating`, immediately after the final `HasQueryFilter` line (`modelBuilder.Entity<RawChangelog>()…`), add:

```csharp
        // Layer 3 (erasure): every tenant-owned row cascades from its owning User, so account
        // deletion is complete by construction. Applied centrally on purpose — a 17th
        // tenant-owned entity inherits this automatically instead of relying on memory.
        // This complements the Layer 1 filters above; it does not replace them.
        foreach (var entity in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            modelBuilder.Entity(entity.ClrType)
                .HasOne(typeof(User))
                .WithMany()
                .HasForeignKey(nameof(ITenantOwned.EndUserId))
                .OnDelete(DeleteBehavior.Cascade);
        }
```

Add `using JiraUnpacked.Domain.Abstractions;` if it is not already present.

- [ ] **Step 5: Run to verify the model tests pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~TenantDeletionGateTests`
Expected: **3 passing**.

- [ ] **Step 6: Generate the migration**

Run:

```bash
dotnet ef migrations add AddTenantCascadeToUsers \
  --project src/JiraUnpacked.Infrastructure \
  --startup-project src/JiraUnpacked.Infrastructure
```

Expected: a migration whose `Up` adds **16** foreign keys (one per tenant-owned table, `EndUserId` → `Users.Id`, `onDelete: ReferentialAction.Cascade`) and alters the `InviteCodes` redeemer FK to `SetNull`. **Open the file and confirm** it contains only FK work — no unrelated column or index changes. If it contains anything else, stop and report.

If applying the migration fails with a foreign-key violation, that means orphaned tenant rows already exist in that database. **Do not delete them to make it pass** — report it; on a dev box the correct fix is recreating the local test database.

- [ ] **Step 7: Verify drift and full suite**

Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure`
Expected: `No changes have been made to the model since the last migration.`

Run: `dotnet test JiraUnpacked.sln` → full suite green, **including the untouched FR-B2 gate**. Report the count.

- [ ] **Step 8: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/InviteCodeConfiguration.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        tests/JiraUnpacked.Tests/Tenancy/TenantDeletionGateTests.cs
git commit -m "feat: cascade tenant data from Users for erasure (Phase 7, Spec 1)"
```

---

## Task 2 (TL2): Tombstone + deletion service

**Files:**
- Create: `src/JiraUnpacked.Domain/Entities/DeletedAccount.cs`
- Create: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/DeletedAccountConfiguration.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs` (DbSet)
- Create: migration `…AddDeletedAccounts.cs` (generated)
- Create: `src/JiraUnpacked.Domain/Abstractions/IAccountDeletionService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/AccountDeletionService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Tenancy/TenantDeletionGateTests.cs` (extend)

**Interfaces:**
- Consumes (Task 1): the cascade relationships; `ITenantScope.BeginScope(Guid) → IDisposable`.
- Produces: `IAccountDeletionService.DeleteAsync(Guid userId, Guid requestedBy, CancellationToken) → Task<AccountDeletionResult>`; `AccountDeletionOutcome { Deleted, NotFound, NotAnEndUser }`; `DeletedAccount`. Task 3 calls this service.

- [ ] **Step 1: Write the failing data-level gate tests**

Append to `tests/JiraUnpacked.Tests/Tenancy/TenantDeletionGateTests.cs` — a second, DB-backed class in the same file:

```csharp
/// <summary>
/// Data-level half of the gate: proves the cascade actually fires end-to-end, and — just as
/// important — that it does not over-reach into another tenant.
/// </summary>
public sealed class TenantDeletionDataGateTests : IClassFixture<Integration.JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly Integration.JiraUnpackedApiFactory _factory;
    public TenantDeletionDataGateTests(Integration.JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Integration.JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public async Task Deleting_an_account_erases_every_seeded_tenant_row()
    {
        var tenant = await TenantDeletionTestData.SeedUserAsync(_factory);
        var seed = await TenantDeletionTestData.SeedFullGraphAsync(tenant);

        await TenantDeletionTestData.DeleteAccountAsync(_factory, tenant, tenant);

        await using var db = Privileged();
        foreach (var (table, count) in await TenantDeletionTestData.CountsForTenantAsync(db, tenant))
            Assert.True(count == 0, $"{table} still holds {count} row(s) after erasure");
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == tenant));
    }

    [Fact]
    public async Task Deleting_one_account_leaves_another_tenants_rows_untouched()
    {
        var tenantA = await TenantDeletionTestData.SeedUserAsync(_factory);
        await TenantDeletionTestData.SeedFullGraphAsync(tenantA);
        var tenantB = await TenantDeletionTestData.SeedUserAsync(_factory);
        await TenantDeletionTestData.SeedFullGraphAsync(tenantB);

        await using (var before = Privileged())
        {
            var b = await TenantDeletionTestData.CountsForTenantAsync(before, tenantB);
            Assert.All(b, x => Assert.True(x.Count > 0));   // B genuinely has data to lose
        }

        await TenantDeletionTestData.DeleteAccountAsync(_factory, tenantA, tenantA);

        await using var after = Privileged();
        foreach (var (table, count) in await TenantDeletionTestData.CountsForTenantAsync(after, tenantB))
            Assert.True(count > 0, $"tenant B lost rows in {table} — the cascade over-reached");
        Assert.True(await after.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == tenantB));
    }

    [Fact]
    public async Task A_user_who_redeemed_an_invite_code_is_deletable_and_the_code_survives()
    {
        var tenant = await TenantDeletionTestData.SeedUserAsync(_factory);
        var codeId = await TenantDeletionTestData.SeedRedeemedInviteCodeAsync(tenant);

        await TenantDeletionTestData.DeleteAccountAsync(_factory, tenant, tenant);

        await using var db = Privileged();
        var code = await db.InviteCodes.IgnoreQueryFilters().SingleAsync(c => c.Id == codeId);
        Assert.Null(code.UsedByUserId);   // audit fact retained, personal link severed
    }

    [Fact]
    public async Task Tombstone_records_the_erasure_without_personal_data()
    {
        var tenant = await TenantDeletionTestData.SeedUserAsync(_factory);
        await TenantDeletionTestData.DeleteAccountAsync(_factory, tenant, tenant);

        await using var db = Privileged();
        var tomb = await db.DeletedAccounts.SingleAsync(t => t.DeletedUserId == tenant);
        Assert.Equal(tenant, tomb.RequestedBy);          // self-service
        Assert.NotEqual(default, tomb.DeletedAt);
        // The entity carries only ids and a timestamp — there is no field that could hold
        // an email or username, which is the property being asserted.
        Assert.Equal(4, typeof(DeletedAccount).GetProperties().Length);
    }
}
```

Create `tests/JiraUnpacked.Tests/Tenancy/TenantDeletionTestData.cs`:

```csharp
// tests/JiraUnpacked.Tests/Tenancy/TenantDeletionTestData.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Tenancy;

internal sealed record GraphSeed(Guid ConnectionId, Guid ProjectId);

internal static class TenantDeletionTestData
{
    internal static async Task<Guid> SeedUserAsync(JiraUnpackedApiFactory factory)
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.Users.Add(new User
        {
            Id = userId, Email = $"u-{unique}@x.com", Username = $"u-{unique}",
            PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
            Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return userId;
    }

    internal static async Task DeleteAccountAsync(JiraUnpackedApiFactory factory, Guid userId, Guid requestedBy)
    {
        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();
        var result = await svc.DeleteAsync(userId, requestedBy);
        Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
    }

    private static NpgsqlConnection Conn() => new(JiraUnpackedApiFactory.TestConnectionString);

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Seeds a representative multi-table graph for one tenant. The MODEL-level gate proves
    /// every tenant table cascades; this graph proves the cascade genuinely fires across
    /// connection-, project- and issue-scoped tables.
    /// </summary>
    internal static async Task<GraphSeed> SeedFullGraphAsync(Guid tenant)
    {
        var connId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var statusId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var userDictId = Guid.NewGuid();

        await using var conn = Conn();
        await conn.OpenAsync();

        await Exec(conn, @"INSERT INTO ""JiraConnections""(""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"") VALUES (@i,@t,'https://x','e','c',now())",
            ("i", connId), ("t", tenant));
        await Exec(conn, @"INSERT INTO ""JiraProjects""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"") VALUES (@i,@t,@c,'ACME','1',60,now())",
            ("i", projId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"") VALUES (@i,@t,@c,'1','To Do','new')",
            ("i", statusId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""IssueTypes""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraIssueTypeId"",""Name"",""Class"") VALUES (@i,@t,@c,'1','Story','Other')",
            ("i", typeId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""JiraUsers""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraAccountId"",""DisplayName"") VALUES (@i,@t,@c,'u1','Alice')",
            ("i", userDictId), ("t", tenant), ("c", connId));
        await Exec(conn, @"INSERT INTO ""StatusSemantics""(""Id"",""EndUserId"",""JiraProjectId"",""JiraStatusId"",""StatusName"",""Class"") VALUES (@i,@t,@p,'1','To Do','Other')",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""IssueTypeId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"") VALUES (@i,@t,@p,'10001','ACME-1','s',@st,@ty,now(),now(),now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId), ("st", statusId), ("ty", typeId));
        await Exec(conn, @"INSERT INTO ""IssueChangeHistory""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraHistoryId"",""Field"",""FromValue"",""ToValue"",""Author"",""ChangedAt"") VALUES (@i,@t,@p,'10001','h1','status','To Do','Done','Alice',now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""RawIssueSnapshots""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001','{}'::jsonb,now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));
        await Exec(conn, @"INSERT INTO ""RawChangelog""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""Payload"",""FetchedAt"") VALUES (@i,@t,@p,'10001','{}'::jsonb,now())",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projId));

        return new GraphSeed(connId, projId);
    }

    internal static async Task<Guid> SeedRedeemedInviteCodeAsync(Guid redeemer)
    {
        var adminId = Guid.NewGuid();
        var codeId = Guid.NewGuid();
        await using var conn = Conn();
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""Users""(""Id"",""Email"",""Username"",""PasswordHash"",""Role"",""Status"",""CreatedAt"") VALUES (@i,@e,@u,'h','Admin','Active',now())",
            ("i", adminId), ("e", $"a-{adminId:N}@x.com"), ("u", $"a-{adminId:N}"));
        await Exec(conn, @"INSERT INTO ""InviteCodes""(""Id"",""Code"",""CreatedByAdminId"",""UsedByUserId"",""ExpiresAt"",""CreatedAt"") VALUES (@i,@c,@a,@r,now() + interval '30 days',now())",
            ("i", codeId), ("c", $"CODE-{codeId:N}"), ("a", adminId), ("r", redeemer));
        return codeId;
    }

    /// <summary>Row counts per tenant-owned table for one tenant, read privileged for cross-tenant assertions.</summary>
    internal static async Task<IReadOnlyList<(string Table, int Count)>> CountsForTenantAsync(AppDbContext db, Guid tenant)
    {
        return new List<(string, int)>
        {
            ("JiraConnections",   await db.JiraConnections.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("JiraProjects",      await db.JiraProjects.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("Statuses",          await db.Statuses.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("IssueTypes",        await db.IssueTypes.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("JiraUsers",         await db.JiraUsers.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("StatusSemantics",   await db.StatusSemantics.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("Issues",            await db.Issues.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("IssueChangeHistory",await db.IssueChangeHistory.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("RawIssueSnapshots", await db.RawIssueSnapshots.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
            ("RawChangelogs",     await db.RawChangelogs.IgnoreQueryFilters().CountAsync(x => x.EndUserId == tenant)),
        };
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~TenantDeletionDataGateTests`
Expected: compile error — `IAccountDeletionService`, `AccountDeletionOutcome`, `DeletedAccount`, and `db.DeletedAccounts` do not exist.

- [ ] **Step 3: Create the tombstone entity and its configuration**

Create `src/JiraUnpacked.Domain/Entities/DeletedAccount.cs`:

```csharp
namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// Proof that an erasure request was honoured. Deliberately NOT ITenantOwned — it must outlive
/// the tenant it records, so it carries no EndUserId, no query filter and no RLS policy, and
/// the FR-B2 gate is unaffected. Holds no personal data: only opaque ids and a timestamp.
/// </summary>
public sealed class DeletedAccount
{
    public Guid Id { get; set; }

    /// <summary>The erased account's id. The Users row is gone, so this is a dangling reference by design.</summary>
    public Guid DeletedUserId { get; set; }

    /// <summary>Who asked. Equals DeletedUserId for self-service deletion.</summary>
    public Guid RequestedBy { get; set; }

    public DateTime DeletedAt { get; set; }
}
```

Create `src/JiraUnpacked.Infrastructure/Persistence/Configurations/DeletedAccountConfiguration.cs`:

```csharp
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class DeletedAccountConfiguration : IEntityTypeConfiguration<DeletedAccount>
{
    public void Configure(EntityTypeBuilder<DeletedAccount> b)
    {
        b.ToTable("DeletedAccounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.DeletedUserId).IsRequired();
        b.Property(x => x.RequestedBy).IsRequired();
        b.Property(x => x.DeletedAt).IsRequired();
        b.HasIndex(x => x.DeletedUserId);

        // No foreign keys on purpose: both ids may reference rows that no longer exist.
    }
}
```

In `AppDbContext`, add the DbSet next to the others:

```csharp
    public DbSet<DeletedAccount> DeletedAccounts => Set<DeletedAccount>();
```

- [ ] **Step 4: Create the service contract**

Create `src/JiraUnpacked.Domain/Abstractions/IAccountDeletionService.cs`:

```csharp
namespace JiraUnpacked.Domain.Abstractions;

public enum AccountDeletionOutcome { Deleted, NotFound, NotAnEndUser }

public sealed record AccountDeletionResult(AccountDeletionOutcome Outcome);

/// <summary>Irreversibly erases an End User account and every row of tenant data it owns.</summary>
public interface IAccountDeletionService
{
    Task<AccountDeletionResult> DeleteAsync(Guid userId, Guid requestedBy, CancellationToken ct = default);
}
```

- [ ] **Step 5: Implement the service**

Create `src/JiraUnpacked.Infrastructure/Services/AccountDeletionService.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class AccountDeletionService : IAccountDeletionService
{
    private readonly AppDbContext _db;
    private readonly ITenantScope _tenantScope;
    private readonly IClock _clock;

    public AccountDeletionService(AppDbContext db, ITenantScope tenantScope, IClock clock)
    {
        _db = db;
        _tenantScope = tenantScope;
        _clock = clock;
    }

    public async Task<AccountDeletionResult> DeleteAsync(
        Guid userId, Guid requestedBy, CancellationToken ct = default)
    {
        // The tenant scope must be established BEFORE the first database access:
        // TenantConnectionInterceptor sets app.current_tenant when the connection OPENS, so a
        // scope entered later would leave RLS denying the very rows being erased — including
        // when an Admin deletes someone else's account.
        using var _ = _tenantScope.BeginScope(userId);

        // Users is not tenant-owned, so this lookup is unaffected by the scope.
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return new AccountDeletionResult(AccountDeletionOutcome.NotFound);

        // Admin accounts are config-seeded; the startup seeder would recreate them, so
        // deleting one is meaningless.
        if (user.Role != UserRole.EndUser)
            return new AccountDeletionResult(AccountDeletionOutcome.NotAnEndUser);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        _db.Users.Remove(user);   // ON DELETE CASCADE removes all tenant-owned rows
        _db.DeletedAccounts.Add(new DeletedAccount
        {
            Id = Guid.NewGuid(),
            DeletedUserId = userId,
            RequestedBy = requestedBy,
            DeletedAt = _clock.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new AccountDeletionResult(AccountDeletionOutcome.Deleted);
    }
}
```

- [ ] **Step 6: Register the service**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, alongside the other scoped service registrations:

```csharp
        services.AddScoped<IAccountDeletionService, JiraUnpacked.Infrastructure.Services.AccountDeletionService>();
```

- [ ] **Step 7: Generate the tombstone migration**

Run:

```bash
dotnet ef migrations add AddDeletedAccounts \
  --project src/JiraUnpacked.Infrastructure \
  --startup-project src/JiraUnpacked.Infrastructure
```

Expected: one `CreateTable` for `DeletedAccounts` plus its index — **and no RLS statements**, because `DeletedAccount` is not tenant-owned. Confirm nothing else is in the file.

- [ ] **Step 8: Run tests, check drift, commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~TenantDeletion` → **7 passing** (3 model-level from Task 1 + 4 data-level).
Run: `dotnet ef migrations has-pending-model-changes …` → `No changes…`.
Run: `dotnet build JiraUnpacked.sln --nologo` → 0 warnings / 0 errors.

```bash
git add src/JiraUnpacked.Domain/Entities/DeletedAccount.cs \
        src/JiraUnpacked.Domain/Abstractions/IAccountDeletionService.cs \
        src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/DeletedAccountConfiguration.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        src/JiraUnpacked.Infrastructure/Services/AccountDeletionService.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Tenancy/TenantDeletionGateTests.cs \
        tests/JiraUnpacked.Tests/Tenancy/TenantDeletionTestData.cs
git commit -m "feat: account deletion service + erasure tombstone (Phase 7, Spec 1)"
```

---

## Task 3 (TL3): Deletion endpoints

**Files:**
- Modify: `src/JiraUnpacked.Api/Controllers/AuthController.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/AdminUsersController.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/AccountDeletionTests.cs`

**Interfaces:**
- Consumes (Task 2): `IAccountDeletionService.DeleteAsync(Guid userId, Guid requestedBy, CancellationToken)`; `AccountDeletionOutcome { Deleted, NotFound, NotAnEndUser }`.
- Produces: the two endpoints.

`AdminUsersController` is `[Route("api/admin/users")]` with `[Authorize(Policy = "Admin")]` at class level; `AuthController` actions live under `api/auth`. The admin controller already maps `NotAnEndUser` to **400** via `Map(BlockResult)` — match that.

- [ ] **Step 1: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Integration/AccountDeletionTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/AccountDeletionTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Integration;

public sealed class AccountDeletionTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public AccountDeletionTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    private async Task<(HttpClient client, Guid userId, string refreshToken)> AuthedEndUserAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
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
        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return (c, userId, tok.RefreshToken);
    }

    private async Task<HttpClient> AuthedAdminAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(), Email = $"a-{unique}@x.com", Username = $"a-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.Admin,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"a-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return c;
    }

    [Fact]
    public async Task Self_service_delete_removes_the_account_and_severs_the_refresh_chain()
    {
        var (client, _, refreshToken) = await AuthedEndUserAsync();

        var resp = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Refresh tokens cascade away with the user, so the session cannot be extended.
        var refreshed = await _factory.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [Fact]
    public async Task The_still_valid_access_token_cannot_reach_any_tenant_data()
    {
        var (client, _, _) = await AuthedEndUserAsync();
        await client.DeleteAsync("/api/auth/me");

        // /api/auth/me is claims-only — it never queries the database, so it still answers 200.
        // That is the documented, accepted behaviour: the token is valid until it expires.
        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // What matters is that there is no tenant data left to reach, and nothing 500s.
        var connections = await client.GetAsync("/api/connections");
        Assert.Equal(HttpStatusCode.OK, connections.StatusCode);
        Assert.Equal("[]", (await connections.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task Admin_can_delete_an_end_user()
    {
        var (_, endUserId, _) = await AuthedEndUserAsync();
        var admin = await AuthedAdminAsync();

        var resp = await admin.DeleteAsync($"/api/admin/users/{endUserId}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_deleting_an_unknown_user_returns_404()
    {
        var admin = await AuthedAdminAsync();
        var resp = await admin.DeleteAsync($"/api/admin/users/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_accounts_are_not_deletable()
    {
        var admin = await AuthedAdminAsync();

        Guid otherAdminId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            otherAdminId = Guid.NewGuid();
            var unique = Guid.NewGuid().ToString("N");
            db.Users.Add(new User
            {
                Id = otherAdminId, Email = $"a2-{unique}@x.com", Username = $"a2-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.Admin,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resp = await admin.DeleteAsync($"/api/admin/users/{otherAdminId}");
        // 400 matches the existing block/unblock convention for the same condition.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task An_end_user_cannot_delete_another_account_through_the_admin_route()
    {
        var (client, _, _) = await AuthedEndUserAsync();
        var (_, victimId, _) = await AuthedEndUserAsync();

        var resp = await client.DeleteAsync($"/api/admin/users/{victimId}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~AccountDeletionTests`
Expected: FAIL — the two DELETE routes return 404/405 because they do not exist.

- [ ] **Step 3: Add the self-service endpoint**

In `src/JiraUnpacked.Api/Controllers/AuthController.cs`, inject the service (add `IAccountDeletionService` to the constructor and a matching field, following the file's existing style) and add after the `Me()` action:

```csharp
    [HttpDelete("me")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> DeleteMe(CancellationToken ct)
    {
        var claim = User.FindFirst("endUserId")?.Value;
        if (!Guid.TryParse(claim, out var userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid token.");
        }

        var result = await _deletion.DeleteAsync(userId, userId, ct);
        return result.Outcome switch
        {
            AccountDeletionOutcome.Deleted => NoContent(),
            AccountDeletionOutcome.NotFound => Problem(
                statusCode: StatusCodes.Status404NotFound, title: "User not found."),
            _ => Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Only End User accounts can be deleted."),
        };
    }
```

- [ ] **Step 4: Add the admin endpoint**

In `src/JiraUnpacked.Api/Controllers/AdminUsersController.cs`, inject `IAccountDeletionService` alongside `IUserAdminService` and add:

```csharp
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var actor = Guid.TryParse(User.FindFirst("sub")?.Value, out var s) ? s : Guid.Empty;
        var result = await _deletion.DeleteAsync(id, actor, ct);
        return result.Outcome switch
        {
            AccountDeletionOutcome.Deleted => NoContent(),
            AccountDeletionOutcome.NotFound => Problem(
                statusCode: StatusCodes.Status404NotFound, title: "User not found."),
            _ => Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Only End Users can be deleted."),
        };
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~AccountDeletionTests` → **6 passing**.
Run: `dotnet build JiraUnpacked.sln --nologo` → 0 warnings / 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Api/Controllers/AuthController.cs \
        src/JiraUnpacked.Api/Controllers/AdminUsersController.cs \
        tests/JiraUnpacked.Tests/Integration/AccountDeletionTests.cs
git commit -m "feat: self-service and admin account deletion endpoints (Phase 7, Spec 1)"
```

---

## Task 4 (TL4): SyncRun retention

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Retention/RetentionOptions.cs`
- Create: `src/JiraUnpacked.Infrastructure/Retention/SyncRunRetentionJob.cs`
- Modify: `src/JiraUnpacked.Api/Program.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/IPrivilegedDbContextFactory.cs` (doc comment)
- Test: `tests/JiraUnpacked.Tests/Integration/SyncRunRetentionTests.cs`

**Interfaces:**
- Consumes: `IPrivilegedDbContextFactory.Create() → AppDbContext`; `ITenantScope.BeginScope(Guid) → IDisposable`; `IClock.UtcNow`; `SyncRunStatus`, `SyncRunKind`.
- Produces: nothing later tasks depend on.

The job mirrors `SyncTickJob`: privileged read for cross-tenant metadata, then per-tenant work inside `BeginScope`.

- [ ] **Step 1: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Integration/SyncRunRetentionTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/SyncRunRetentionTests.cs
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Retention;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

public sealed class SyncRunRetentionTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public SyncRunRetentionTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static AppDbContext Privileged()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object?)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedRunAsync(
        Guid tenant, Guid projectId, DateTime startedAt, SyncRunStatus status, SyncRunKind kind)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await Exec(conn, @"INSERT INTO ""SyncRuns""(""Id"",""EndUserId"",""JiraProjectId"",""StartedAt"",""Status"",""Kind"",""IssuesProcessed"") VALUES (@i,@t,@p,@s,@st,@k,0)",
            ("i", Guid.NewGuid()), ("t", tenant), ("p", projectId), ("s", startedAt),
            ("st", status.ToString()), ("k", kind.ToString()));
    }

    [Fact]
    public async Task Purge_removes_old_runs_but_keeps_the_watermark_anchor_and_running_runs()
    {
        var tenant = await Tenancy.TenantDeletionTestData.SeedUserAsync(_factory);
        var seed = await Tenancy.TenantDeletionTestData.SeedFullGraphAsync(tenant);
        var now = DateTime.UtcNow;

        // Three old succeeded syncs (well past the cutoff) and one old Running run.
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-400), SyncRunStatus.Succeeded, SyncRunKind.Sync);
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-300), SyncRunStatus.Succeeded, SyncRunKind.Sync);
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-200), SyncRunStatus.Succeeded, SyncRunKind.Sync);
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-350), SyncRunStatus.Running, SyncRunKind.Sync);

        using var scope = _factory.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<SyncRunRetentionJob>(scope.ServiceProvider);
        await job.RunOnceAsync(CancellationToken.None);

        await using var db = Privileged();
        var remaining = await db.SyncRuns.IgnoreQueryFilters()
            .Where(r => r.JiraProjectId == seed.ProjectId)
            .ToListAsync();

        // The newest Succeeded+Sync run is the incremental watermark's anchor: deleting it
        // would make the next sync re-pull everything from the beginning.
        Assert.Contains(remaining, r => r.Status == SyncRunStatus.Succeeded
                                        && r.Kind == SyncRunKind.Sync
                                        && r.StartedAt.Date == now.AddDays(-200).Date);

        // A Running run is never purged at any age — removing it would break the
        // AlreadyRunning guard and orphan an in-flight sync.
        Assert.Contains(remaining, r => r.Status == SyncRunStatus.Running);

        // The two older succeeded runs are gone.
        Assert.DoesNotContain(remaining, r => r.StartedAt.Date == now.AddDays(-400).Date);
        Assert.DoesNotContain(remaining, r => r.StartedAt.Date == now.AddDays(-300).Date);
    }

    [Fact]
    public async Task Purge_leaves_recent_runs_alone()
    {
        var tenant = await Tenancy.TenantDeletionTestData.SeedUserAsync(_factory);
        var seed = await Tenancy.TenantDeletionTestData.SeedFullGraphAsync(tenant);
        var now = DateTime.UtcNow;

        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-5), SyncRunStatus.Succeeded, SyncRunKind.Sync);
        await SeedRunAsync(tenant, seed.ProjectId, now.AddDays(-3), SyncRunStatus.Failed, SyncRunKind.Sync);

        using var scope = _factory.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<SyncRunRetentionJob>(scope.ServiceProvider);
        await job.RunOnceAsync(CancellationToken.None);

        await using var db = Privileged();
        var count = await db.SyncRuns.IgnoreQueryFilters()
            .CountAsync(r => r.JiraProjectId == seed.ProjectId);
        Assert.Equal(2, count);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncRunRetentionTests`
Expected: compile error — `RetentionOptions` and `SyncRunRetentionJob` do not exist.

- [ ] **Step 3: Create the options**

Create `src/JiraUnpacked.Infrastructure/Retention/RetentionOptions.cs`:

```csharp
namespace JiraUnpacked.Infrastructure.Retention;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Age beyond which SyncRun rows are purged. Raw stores and modeled data are NEVER
    /// time-purged: raw snapshots and changelogs are the source for re-derivation.
    /// </summary>
    public int SyncRunDays { get; set; } = 90;
}
```

- [ ] **Step 4: Implement the job**

Create `src/JiraUnpacked.Infrastructure/Retention/SyncRunRetentionJob.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace JiraUnpacked.Infrastructure.Retention;

/// <summary>
/// Trims SyncRun history. Enumerates (project, tenant) pairs with the privileged context —
/// scheduling metadata only — then deletes inside each tenant's scope, so this job adds a
/// privileged READER and no privileged writer: every delete still runs under RLS.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SyncRunRetentionJob : IJob
{
    private readonly IPrivilegedDbContextFactory _privileged;
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly RetentionOptions _options;
    private readonly ILogger<SyncRunRetentionJob> _logger;

    public SyncRunRetentionJob(
        IPrivilegedDbContextFactory privileged, ITenantScope tenantScope,
        IServiceScopeFactory scopeFactory, IClock clock,
        IOptions<RetentionOptions> options, ILogger<SyncRunRetentionJob> logger)
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
        if (!_options.Enabled) return;

        var cutoff = _clock.UtcNow.AddDays(-_options.SyncRunDays);

        // Cross-tenant metadata read only: project id + owning tenant, no issue content.
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
                // Scope BEFORE the DI scope so the connection opens with the right tenant GUC.
                using (_tenantScope.BeginScope(endUserId))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // The watermark anchor: the newest Succeeded + Sync run must survive at any age,
                    // or the next incremental sync loses its "updated >=" floor and re-pulls everything.
                    var anchorId = await db.SyncRuns
                        .Where(r => r.JiraProjectId == projectId
                                    && r.Status == SyncRunStatus.Succeeded
                                    && r.Kind == SyncRunKind.Sync)
                        .OrderByDescending(r => r.StartedAt)
                        .Select(r => (Guid?)r.Id)
                        .FirstOrDefaultAsync(ct);

                    var doomed = await db.SyncRuns
                        .Where(r => r.JiraProjectId == projectId
                                    && r.StartedAt < cutoff
                                    && r.Status != SyncRunStatus.Running   // never orphan an in-flight sync
                                    && (anchorId == null || r.Id != anchorId))
                        .ToListAsync(ct);

                    if (doomed.Count == 0) continue;

                    db.SyncRuns.RemoveRange(doomed);
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "Retention purged {Count} sync runs for project {ProjectId}.", doomed.Count, projectId);
                }
            }
            catch (Exception ex)
            {
                // One tenant's failure must not stop the sweep.
                _logger.LogError(ex, "Retention purge failed for project {ProjectId}.", projectId);
            }
        }
    }
}
```

- [ ] **Step 5: Wire options and the schedule**

In `src/JiraUnpacked.Api/Program.cs`, next to the existing `Configure<SyncSchedulerOptions>` line:

```csharp
builder.Services.Configure<JiraUnpacked.Infrastructure.Retention.RetentionOptions>(
    builder.Configuration.GetSection(JiraUnpacked.Infrastructure.Retention.RetentionOptions.SectionName));
```

Inside the existing `if (builder.Configuration.GetValue("Sync:SchedulerEnabled", true))` block — so tests keep it disabled through the same toggle — register the daily job alongside the sync tick:

```csharp
        var retentionKey = new Quartz.JobKey("sync-run-retention");
        q.AddJob<JiraUnpacked.Infrastructure.Retention.SyncRunRetentionJob>(retentionKey);
        q.AddTrigger(t => t.ForJob(retentionKey)
            .WithSimpleSchedule(s => s.WithIntervalInHours(24).RepeatForever()));
```

- [ ] **Step 6: Update the privileged factory's doc comment**

In `src/JiraUnpacked.Infrastructure/Persistence/IPrivilegedDbContextFactory.cs`, the comment currently names `DueProjectFinder` as the only sanctioned consumer. Update it so the second one is recorded:

```csharp
/// <summary>
/// Builds an AppDbContext on the privileged (RLS-bypassing) 'Default' connection with no tenant.
/// USE ONLY for cross-tenant scheduling metadata: DueProjectFinder, and SyncRunRetentionJob's
/// (project, tenant) enumeration. Never read issue payloads with it, and never WRITE with it —
/// retention deletes inside each tenant's scope so writes stay under RLS.
/// </summary>
```

- [ ] **Step 7: Record the policy in REQUIREMENTS.md**

REQUIREMENTS §4 currently states the obligation but not the policy. Replace the two bullets under **Privacy / data lifecycle (GDPR — EU/Poland)** with:

```markdown
**Privacy / data lifecycle (GDPR — EU/Poland)**
- Storing third parties' business data. Retention policy:
  - **Raw stores** (`RawIssueSnapshots`, `RawChangelog`) — retained indefinitely, never time-purged: they are the source for re-derivation, so ageing them out would destroy the ability to recompute history after a mapping change.
  - **Modeled data** (`Issues`, `IssueChangeHistory`, dictionaries, semantics, mappings) — retained indefinitely; it is the product.
  - **`SyncRun` history** — purged after `Retention:SyncRunDays` (default 90), except the newest successful incremental run per project (the sync watermark's anchor) and any run still `Running`.
- Hard delete of all tenant data on account removal: deleting a `User` cascades to every tenant-owned table, verified by the deletion-completeness gate. Erasure is immediate and irreversible; a non-identifying `DeletedAccounts` tombstone (ids + timestamp only) records that it happened.
```

- [ ] **Step 8: Run tests and the full suite**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncRunRetentionTests` → **2 passing**.
Run: `dotnet test JiraUnpacked.sln` → full suite green; report the count.
Run: `dotnet build JiraUnpacked.sln -c Release --nologo` → 0 warnings / 0 errors.

- [ ] **Step 9: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Retention/ \
        src/JiraUnpacked.Infrastructure/Persistence/IPrivilegedDbContextFactory.cs \
        src/JiraUnpacked.Api/Program.cs \
        REQUIREMENTS.md \
        tests/JiraUnpacked.Tests/Integration/SyncRunRetentionTests.cs
git commit -m "feat: SyncRun retention purge preserving the watermark anchor (Phase 7, Spec 1)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green (report the count).
- [ ] `dotnet ef migrations has-pending-model-changes …` → "No changes".
- [ ] Confirm `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is unmodified on this branch (`git log develop..HEAD -- tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is empty) and green.
- [ ] Grep the new production files for `IgnoreQueryFilters` — the only permitted occurrence is the retention job's **read-only** `(project, tenant)` enumeration. There must be no privileged *write*.
- [ ] Confirm the branch adds exactly two migrations (cascade FKs, tombstone table) and that neither contains RLS statements for `DeletedAccounts`.
- [ ] Push `feat/tenant-data-lifecycle`; open PR → `develop`; CI green; squash-merge; then the `develop → main` release (merge-commit). Before the release run `git log --oneline origin/develop..origin/main --no-merges` — non-empty means a direct-to-main follow-up landed and must be back-merged first (release merge commits alone are expected and are not a reason to back-merge).

## Notes for execution

- **The gate is the point.** If `Every_tenant_owned_entity_cascades_from_user` ever fails, a tenant table was added without erasure wiring — fix the wiring, never the assertion. Likewise `Gate_actually_examines_every_tenant_owned_entity` guards against the reflection predicate silently matching nothing.
- **`BeginScope` before first DB access**, always — in the deletion service and in the retention job.
- **Retention never touches raw stores.** `RawIssueSnapshots` and `RawChangelog` are the source for re-derivation; purging them would break a core capability, not just reclaim space.
- Deletion is irreversible and has no grace period by design; there is no soft-delete state to maintain.
