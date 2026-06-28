# Block / Unblock End User Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an Admin block/unblock an End User (FR-A5): block sets `Status = Blocked` and revokes the user's refresh tokens so they can no longer authenticate; unblock restores `Status = Active`. Reversible.

**Architecture:** A new `IUserAdminService`/`UserAdminService` performs the status change against `AppDbContext` and bulk-revokes the user's active refresh tokens on block. An Admin-only `AdminUsersController` exposes `block`/`unblock`. Login already requires `Active`, and refresh already re-checks `Active`, so a blocked user is locked out immediately; revoking refresh tokens is belt-and-suspenders + cleanup.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit + `WebApplicationFactory`.

## Global Constraints

- Target framework `net8.0`; EF Core + Npgsql pinned to `8.0.11`.
- Block targets **End Users** only (admins cannot be blocked via this endpoint).
- Every commit message ends with a blank line then `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- Local Docker Postgres must be running for integration tests (`docker compose up -d`).

**Scope:** Plan 8 of the Phase 2 series — issue #9 (FR-A5). The **last Module A (Identity) item**. Pausing scheduled syncs on block is a noted hook for Phase 5 (no sync engine yet). Spec: `docs/superpowers/specs/2026-06-23-auth-approach-design.md` §6.7.

---

## File Structure

- Create `src/JiraUnpacked.Domain/Abstractions/IUserAdminService.cs` — interface + `BlockResult` + `BlockError`.
- Create `src/JiraUnpacked.Infrastructure/Services/UserAdminService.cs` — block/unblock.
- Modify `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` — register `IUserAdminService` (scoped).
- Create `src/JiraUnpacked.Api/Controllers/AdminUsersController.cs` — Admin-only `block`/`unblock`.
- Create `tests/JiraUnpacked.Tests/Admin/BlockUnblockEndpointTests.cs` — integration tests.

Reused: `User` (`Id, Role, Status`), `UserRole.Admin/EndUser`, `UserStatus.Active/Blocked`, `RefreshToken` (`UserId, RevokedAt`), `IClock`, `AppDbContext`, the `Admin` policy, `JiraUnpackedApiFactory`, `IPasswordHasher`/`ITokenService` (test seeding).

---

## Task 1: IUserAdminService + UserAdminService + DI

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IUserAdminService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/UserAdminService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`

**Interfaces:**
- Consumes: `IClock.UtcNow`, `AppDbContext`, `User.Status`/`Role`, `RefreshToken.RevokedAt`, `UserStatus`, `UserRole`.
- Produces: `IUserAdminService.BlockAsync(Guid userId, CancellationToken)`, `UnblockAsync(Guid userId, CancellationToken)`, both returning `BlockResult(bool Succeeded, BlockError Error)`; `enum BlockError { None, NotFound, NotAnEndUser }`; factories `Ok()`, `Fail(BlockError)`.

Behavior is DB-orchestration; validated by the integration tests in Task 3.

- [ ] **Step 1: Create the abstraction**

`src/JiraUnpacked.Domain/Abstractions/IUserAdminService.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

public enum BlockError
{
    None,
    NotFound,
    NotAnEndUser
}

public sealed record BlockResult(bool Succeeded, BlockError Error)
{
    public static BlockResult Ok() => new(true, BlockError.None);
    public static BlockResult Fail(BlockError error) => new(false, error);
}

public interface IUserAdminService
{
    Task<BlockResult> BlockAsync(Guid userId, CancellationToken ct = default);
    Task<BlockResult> UnblockAsync(Guid userId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create the implementation**

`src/JiraUnpacked.Infrastructure/Services/UserAdminService.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class UserAdminService : IUserAdminService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public UserAdminService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<BlockResult> BlockAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return BlockResult.Fail(BlockError.NotFound);
        }
        if (user.Role != UserRole.EndUser)
        {
            return BlockResult.Fail(BlockError.NotAnEndUser);
        }

        var now = _clock.UtcNow;
        user.Status = UserStatus.Blocked;

        // Revoke active refresh tokens so existing sessions can't be refreshed.
        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        await _db.SaveChangesAsync(ct);
        return BlockResult.Ok();
    }

    public async Task<BlockResult> UnblockAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return BlockResult.Fail(BlockError.NotFound);
        }
        if (user.Role != UserRole.EndUser)
        {
            return BlockResult.Fail(BlockError.NotAnEndUser);
        }

        // Restore access; previously revoked refresh tokens stay revoked (the user logs in again).
        user.Status = UserStatus.Active;
        await _db.SaveChangesAsync(ct);
        return BlockResult.Ok();
    }
}
```

- [ ] **Step 3: Register the service**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, register inside `AddInfrastructure` after the `services.AddScoped<IEmailVerificationService, EmailVerificationService>();` line:
```csharp
        services.AddScoped<IUserAdminService, UserAdminService>();
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IUserAdminService.cs src/JiraUnpacked.Infrastructure/Services/UserAdminService.cs src/JiraUnpacked.Infrastructure/DependencyInjection.cs
git commit -F - <<'EOF'
feat: UserAdminService block/unblock (revoke refresh tokens on block; FR-A5)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 2: Admin-only AdminUsersController

**Files:**
- Create: `src/JiraUnpacked.Api/Controllers/AdminUsersController.cs`

**Interfaces:**
- Consumes: `IUserAdminService.BlockAsync`/`UnblockAsync`, `BlockError`.

- [ ] **Step 1: Create the controller**

`src/JiraUnpacked.Api/Controllers/AdminUsersController.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = "Admin")]
public sealed class AdminUsersController : ControllerBase
{
    private readonly IUserAdminService _users;

    public AdminUsersController(IUserAdminService users) => _users = users;

    [HttpPost("{id:guid}/block")]
    public async Task<IActionResult> Block(Guid id, CancellationToken ct)
        => Map(await _users.BlockAsync(id, ct));

    [HttpPost("{id:guid}/unblock")]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken ct)
        => Map(await _users.UnblockAsync(id, ct));

    private IActionResult Map(BlockResult result) => result.Error switch
    {
        BlockError.None => NoContent(),
        BlockError.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: "User not found."),
        BlockError.NotAnEndUser => Problem(
            statusCode: StatusCodes.Status400BadRequest, title: "Only End Users can be blocked or unblocked."),
        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/JiraUnpacked.Api/Controllers/AdminUsersController.cs
git commit -F - <<'EOF'
feat: Admin-only block/unblock endpoints

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 3: Integration tests

**Files:**
- Create: `tests/JiraUnpacked.Tests/Admin/BlockUnblockEndpointTests.cs`

Requires the local Postgres container running. Tests seed users directly, log in for bearer tokens, and exercise the endpoints. A blocked user must lose login + refresh; unblock restores login.

- [ ] **Step 1: Write the tests**

`tests/JiraUnpacked.Tests/Admin/BlockUnblockEndpointTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Admin;

public sealed class BlockUnblockEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public BlockUnblockEndpointTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    private const string Password = "Correct-Horse-9!";

    // Seeds an Active user of the given role; returns the user id + login email.
    private async Task<(Guid id, string email)> SeedUserAsync(UserRole role)
    {
        var unique = Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"user-{unique}@example.com",
            Username = $"user-{unique}",
            PasswordHash = hasher.Hash(Password),
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Id, user.Email);
    }

    private async Task<TokenResponseDto> LoginAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password = Password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<TokenResponseDto>())!;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, email) = await SeedUserAsync(UserRole.Admin);
        var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    [Fact]
    public async Task Block_SetsBlocked_RevokesRefreshTokens_AndStopsAuth()
    {
        var (endUserId, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var endUserClient = _factory.CreateClient();
        var endUserTokens = await LoginAsync(endUserClient, endUserEmail); // active session before block

        var admin = await AdminClientAsync();
        var block = await admin.PostAsync($"/api/admin/users/{endUserId}/block", content: null);
        Assert.Equal(HttpStatusCode.NoContent, block.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == endUserId);
            Assert.Equal(UserStatus.Blocked, user.Status);
            Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == endUserId && t.RevokedAt == null));
        }

        // Blocked user cannot log in...
        var reLogin = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = endUserEmail, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, reLogin.StatusCode);

        // ...and cannot refresh the pre-block session.
        var refresh = await _factory.CreateClient().PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = endUserTokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Unblock_RestoresLogin()
    {
        var (endUserId, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var admin = await AdminClientAsync();

        await admin.PostAsync($"/api/admin/users/{endUserId}/block", content: null);
        var unblock = await admin.PostAsync($"/api/admin/users/{endUserId}/unblock", content: null);
        Assert.Equal(HttpStatusCode.NoContent, unblock.StatusCode);

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = endUserEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Block_NonExistentUser_Returns404()
    {
        var admin = await AdminClientAsync();
        var block = await admin.PostAsync($"/api/admin/users/{Guid.NewGuid()}/block", content: null);
        Assert.Equal(HttpStatusCode.NotFound, block.StatusCode);
    }

    [Fact]
    public async Task Block_AnAdmin_Returns400()
    {
        var (adminId, _) = await SeedUserAsync(UserRole.Admin);
        var admin = await AdminClientAsync();
        var block = await admin.PostAsync($"/api/admin/users/{adminId}/block", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, block.StatusCode);
    }

    [Fact]
    public async Task Block_AsEndUser_Returns403()
    {
        var (targetId, _) = await SeedUserAsync(UserRole.EndUser);
        var (_, endUserEmail) = await SeedUserAsync(UserRole.EndUser);
        var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, endUserEmail);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var block = await client.PostAsync($"/api/admin/users/{targetId}/block", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, block.StatusCode);
    }

    [Fact]
    public async Task Block_Anonymous_Returns401()
    {
        var (targetId, _) = await SeedUserAsync(UserRole.EndUser);
        var block = await _factory.CreateClient().PostAsync($"/api/admin/users/{targetId}/block", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, block.StatusCode);
    }
}
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test --filter FullyQualifiedName~BlockUnblockEndpointTests`
Expected: PASS — 6 passed.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test JiraUnpacked.sln -c Debug`
Expected: 58 passed (52 existing + 6 new).

- [ ] **Step 4: Commit**

```bash
git add tests/JiraUnpacked.Tests/Admin/BlockUnblockEndpointTests.cs
git commit -F - <<'EOF'
test: block/unblock integration tests (revoke + lockout, unblock, authz)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Final verification

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings, 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → 58 passing.
- [ ] **Push the branch** (`git push -u origin feat/block-unblock`) BEFORE the PR — verify `origin` tip equals local HEAD.
- [ ] Open PR → `develop`; confirm CI (Postgres service) is green; merge. This closes #9 and **completes Module A (Identity)**.

---

## Roadmap (next)
- **Module A (Identity) complete.** Next is **Phase 3 — the FR-B2 tenancy gate (#13)**: the global EF Core query filter on `EndUserId` + the integration test proving tenant X cannot read tenant Y's rows through any read path. **This is the hard gate — no Jira data ingestion (Phase 4+) until it is green.**
