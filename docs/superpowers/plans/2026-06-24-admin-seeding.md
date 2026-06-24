# Admin Seeding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Seed the first Admin account(s) from configuration at startup (FR-A3) — idempotently, with no admin self-registration path.

**Architecture:** A config-bound `SeedOptions` lists admin accounts (`Email`, `Username`, `Password`). An `AdminSeeder` infrastructure service idempotently upserts each as an `Active` `Admin` user (bcrypt-hashed password) if no user with that email/username exists. `Program.cs` binds the options and runs the seeder in a scope at startup, before serving requests.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, bcrypt (`IPasswordHasher`), xUnit + `WebApplicationFactory`.

**Scope:** Plan 4 of the Phase 2 series — issue #5 (FR-A3). Spec: `docs/superpowers/specs/2026-06-23-auth-approach-design.md` §6.8. After this: #8 invite creation, #6/#7 registration + verification, #9 block/unblock.

**Preconditions:** local Docker Postgres running (`docker compose up -d` → healthy) for the integration test.

---

## File Structure

- Create `src/JiraUnpacked.Infrastructure/Identity/SeedOptions.cs` — config POCO (`SeedAdmin` record + `SeedOptions` with `Admins` list).
- Create `src/JiraUnpacked.Infrastructure/Identity/AdminSeeder.cs` — idempotent seeding service.
- Modify `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` — register `AdminSeeder` (scoped).
- Modify `src/JiraUnpacked.Api/Program.cs` — bind `SeedOptions`; run the seeder in a scope at startup.
- Modify `src/JiraUnpacked.Api/appsettings.Development.json` — add a `Seed` section with a local dev admin.
- Create `tests/JiraUnpacked.Tests/Identity/AdminSeederTests.cs` — integration tests (creates Active Admin, idempotent, seeded admin can log in).

Existing pieces reused: `IPasswordHasher`, `IClock`, `User` (`Id, Email, Username, PasswordHash, Role, Status, CreatedAt`), `UserRole.Admin`, `UserStatus.Active`, `AppDbContext.Users`, `JiraUnpackedApiFactory`.

---

## Task 1: SeedOptions + AdminSeeder + DI registration

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Identity/SeedOptions.cs`
- Create: `src/JiraUnpacked.Infrastructure/Identity/AdminSeeder.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`

Behavior is DB-orchestration, validated by the integration tests in Task 3.

- [ ] **Step 1: Create the options**

`src/JiraUnpacked.Infrastructure/Identity/SeedOptions.cs`:
```csharp
namespace JiraUnpacked.Infrastructure.Identity;

public sealed class SeedAdmin
{
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public List<SeedAdmin> Admins { get; set; } = new();
}
```
(Mutable properties + parameterless types so `IConfiguration` binding works.)

- [ ] **Step 2: Create the seeder**

`src/JiraUnpacked.Infrastructure/Identity/AdminSeeder.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Infrastructure.Identity;

/// <summary>
/// Seeds configured Admin accounts at startup (FR-A3). Idempotent: an admin whose
/// email or username already exists is skipped. There is no admin self-registration.
/// </summary>
public sealed class AdminSeeder
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;
    private readonly SeedOptions _options;

    public AdminSeeder(AppDbContext db, IPasswordHasher hasher, IClock clock, IOptions<SeedOptions> options)
    {
        _db = db;
        _hasher = hasher;
        _clock = clock;
        _options = options.Value;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var admin in _options.Admins)
        {
            if (string.IsNullOrWhiteSpace(admin.Email) || string.IsNullOrWhiteSpace(admin.Username)
                || string.IsNullOrWhiteSpace(admin.Password))
            {
                continue; // skip incomplete entries
            }

            var exists = await _db.Users.AnyAsync(
                u => u.Email == admin.Email || u.Username == admin.Username, ct);
            if (exists)
            {
                continue; // idempotent: already seeded
            }

            _db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = admin.Email,
                Username = admin.Username,
                PasswordHash = _hasher.Hash(admin.Password),
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = _clock.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 3: Register the seeder**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, add `using JiraUnpacked.Infrastructure.Identity;` (with the other usings) and register inside `AddInfrastructure` after the existing `services.AddScoped<IAuthService, AuthService>();` line:
```csharp
        services.AddScoped<AdminSeeder>();
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/Identity/SeedOptions.cs src/JiraUnpacked.Infrastructure/Identity/AdminSeeder.cs src/JiraUnpacked.Infrastructure/DependencyInjection.cs
git commit -F - <<'EOF'
feat: AdminSeeder + SeedOptions (idempotent admin seeding, FR-A3)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 2: Bind options + run seeder at startup

**Files:**
- Modify: `src/JiraUnpacked.Api/Program.cs`
- Modify: `src/JiraUnpacked.Api/appsettings.Development.json`

- [ ] **Step 1: Bind SeedOptions and run the seeder**

In `src/JiraUnpacked.Api/Program.cs`:

(a) Add `using JiraUnpacked.Infrastructure.Identity;` to the using block (with the other `using` directives at the top).

(b) After the `AddOptions<JwtOptions>()...ValidateOnStart();` block and before `var jwt = ...`, add:
```csharp
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
```
Note: `Configure<T>(IConfiguration)` is available in the Api project (web SDK) without extra packages.

(c) Immediately after `var app = builder.Build();`, add a startup seeding block:
```csharp
// Seed configured Admin accounts before serving requests (idempotent; FR-A3).
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AdminSeeder>().SeedAsync();
}
```
(`await` is valid in the top-level Program. `CreateScope`/`GetRequiredService` need `using Microsoft.Extensions.DependencyInjection;` — add it to the using block if not already present.)

- [ ] **Step 2: Add a dev admin to configuration**

In `src/JiraUnpacked.Api/appsettings.Development.json`, add a top-level `Seed` section alongside `ConnectionStrings`/`Jwt`:
```json
  "Seed": {
    "Admins": [
      {
        "Email": "admin@local.dev",
        "Username": "admin",
        "Password": "Local-Dev-Admin-1!"
      }
    ]
  }
```
(Local dev convenience only — a throwaway credential, consistent with the existing dev-only `Jwt:SigningKey`. Production admins come from environment/secrets, not this file.)

- [ ] **Step 3: Verify build + existing tests**

Run: `dotnet build JiraUnpacked.sln -c Debug` then `dotnet test JiraUnpacked.sln -c Debug`
Expected: build succeeds; the existing 25 tests still pass. (The integration harness builds the app, so the startup seeder runs against `jiraunpacked_test` using whatever `Seed` config is present — idempotent and harmless; existing tests seed their own unique users and are unaffected.)

- [ ] **Step 4: Commit**

```bash
git add src/JiraUnpacked.Api/Program.cs src/JiraUnpacked.Api/appsettings.Development.json
git commit -F - <<'EOF'
feat: bind SeedOptions + run AdminSeeder at startup

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 3: Integration tests (seeding)

**Files:**
- Create: `tests/JiraUnpacked.Tests/AssemblyInfo.cs`
- Create: `tests/JiraUnpacked.Tests/Identity/AdminSeederTests.cs`

Requires the local Postgres container running. These tests construct `AdminSeeder` directly with a controlled `SeedOptions` (so they don't depend on the app's `Seed` config) but use the real `AppDbContext` + bcrypt hasher from the factory's DI, and use unique emails so they don't collide with other tests or the dev seed.

- [ ] **Step 1: Serialize test collections (prevents concurrent-migration races)**

This is the second integration-test class. By default xUnit runs different test classes in parallel, so two `WebApplicationFactory` classes could call `MigrateAsync` against an empty `jiraunpacked_test` at the same time on a fresh CI database — a flaky race. Disable cross-collection parallelization with one assembly attribute.

`tests/JiraUnpacked.Tests/AssemblyInfo.cs`:
```csharp
using Xunit;

// Integration test classes each spin up the API and run EF migrations against the
// shared test database; serialize collections so two classes don't migrate concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

- [ ] **Step 2: Write the tests**

`tests/JiraUnpacked.Tests/Identity/AdminSeederTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Identity;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Tests.Identity;

public sealed class AdminSeederTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public AdminSeederTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task RunSeederAsync(SeedOptions options)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var seeder = new AdminSeeder(db, hasher, clock, Options.Create(options));
        await seeder.SeedAsync();
    }

    private static SeedOptions OneAdmin(string email, string username, string password) =>
        new() { Admins = { new SeedAdmin { Email = email, Username = username, Password = password } } };

    [Fact]
    public async Task Seed_CreatesActiveAdmin()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"admin-{unique}@example.com";

        await RunSeederAsync(OneAdmin(email, $"admin-{unique}", "Seeded-Admin-9!"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.NotEqual("Seeded-Admin-9!", user.PasswordHash); // hashed, not plaintext
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        var unique = Guid.NewGuid().ToString("N");
        var options = OneAdmin($"admin-{unique}@example.com", $"admin-{unique}", "Seeded-Admin-9!");

        await RunSeederAsync(options);
        await RunSeederAsync(options); // second run must not create a duplicate

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.Users.CountAsync(u => u.Email == $"admin-{unique}@example.com");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SeededAdmin_CanLogIn()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"admin-{unique}@example.com";
        const string password = "Seeded-Admin-9!";

        await RunSeederAsync(OneAdmin(email, $"admin-{unique}", password));

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 3: Run the new tests**

Run: `dotnet test --filter FullyQualifiedName~AdminSeederTests`
Expected: PASS — 3 passed.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test JiraUnpacked.sln -c Debug`
Expected: 28 passed (25 existing + 3 new).

- [ ] **Step 5: Commit**

```bash
git add tests/JiraUnpacked.Tests/AssemblyInfo.cs tests/JiraUnpacked.Tests/Identity/AdminSeederTests.cs
git commit -F - <<'EOF'
test: AdminSeeder integration tests + serialize test collections

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Final verification

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings, 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → 28 passing.
- [ ] **Push the branch** (`git push -u origin feat/admin-seeding`) BEFORE opening the PR — verify `origin` tip equals local HEAD.
- [ ] Open PR → `develop`; confirm CI (Postgres service) is green; merge.

---

## Roadmap (remaining Phase 2 plans)
- **#8 invite creation** (FR-A4 — Admin-only endpoint generating single-use expiring codes; the seeded admin from this plan is the actor) · **#6/#7 registration + email verification** (FR-A1/A2) · **#9 block/unblock** (FR-A5).
