# Invite Code Creation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an Admin generate single-use, expiring, shareable invite codes via `POST /api/admin/invite-codes` (FR-A4).

**Architecture:** A new `IInviteCodeService`/`InviteCodeService` generates a high-entropy code, persists an `InviteCode` row (creator = the calling admin, future expiry, unused), and returns the code + expiry. An Admin-only `InviteCodesController` exposes it, reading the admin id from the JWT `sub` claim. Reuses the existing `Admin` authorization policy and integration harness.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit + `WebApplicationFactory`.

**Scope:** Plan 5 of the Phase 2 series — issue #8 (FR-A4). This plan only *creates* codes; *redeeming* them (single-use/expiry enforcement) happens during registration (#6/#7). Spec: `docs/superpowers/specs/2026-06-23-auth-approach-design.md` §6.6.

**Preconditions:** local Docker Postgres running (`docker compose up -d`).

---

## File Structure

- Create `src/JiraUnpacked.Domain/Abstractions/IInviteCodeService.cs` — interface + `InviteCodeResult` record.
- Create `src/JiraUnpacked.Infrastructure/Services/InviteCodeService.cs` — code generation + persistence.
- Modify `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` — register `IInviteCodeService` (scoped).
- Create `src/JiraUnpacked.Api/Contracts/Admin/InviteCodeResponse.cs` — response DTO.
- Create `src/JiraUnpacked.Api/Controllers/InviteCodesController.cs` — Admin-only `POST /api/admin/invite-codes`.
- Create `tests/JiraUnpacked.Tests/Admin/InviteCodesEndpointTests.cs` — integration tests.

Reused: `InviteCode` entity (`Id, Code, CreatedByAdminId, ExpiresAt, UsedByUserId, UsedAt`; unique index on `Code`), `IClock`, `AppDbContext`, `IPasswordHasher` (to seed test users), the `Admin` policy, `JiraUnpackedApiFactory`.

---

## Task 1: IInviteCodeService + InviteCodeService + DI

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IInviteCodeService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/InviteCodeService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`

Behavior is DB-orchestration; validated by the integration tests in Task 3.

- [ ] **Step 1: Create the abstraction**

`src/JiraUnpacked.Domain/Abstractions/IInviteCodeService.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

public sealed record InviteCodeResult(string Code, DateTime ExpiresAtUtc);

public interface IInviteCodeService
{
    Task<InviteCodeResult> CreateAsync(Guid createdByAdminId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create the implementation**

`src/JiraUnpacked.Infrastructure/Services/InviteCodeService.cs`:
```csharp
using System.Security.Cryptography;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.IdentityModel.Tokens;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class InviteCodeService : IInviteCodeService
{
    // Codes expire after this many days. Configurable later if needed (YAGNI for now).
    private const int ExpiryDays = 14;

    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public InviteCodeService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<InviteCodeResult> CreateAsync(Guid createdByAdminId, CancellationToken ct = default)
    {
        // 24 random bytes -> 32-char base64url string (fits the Code varchar(64) column).
        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var expiresAt = _clock.UtcNow.AddDays(ExpiryDays);

        _db.InviteCodes.Add(new InviteCode
        {
            Id = Guid.NewGuid(),
            Code = code,
            CreatedByAdminId = createdByAdminId,
            ExpiresAt = expiresAt
        });
        await _db.SaveChangesAsync(ct);

        return new InviteCodeResult(code, expiresAt);
    }
}
```

- [ ] **Step 3: Register the service**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, register inside `AddInfrastructure` after the `services.AddScoped<AdminSeeder>();` line:
```csharp
        services.AddScoped<IInviteCodeService, InviteCodeService>();
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IInviteCodeService.cs src/JiraUnpacked.Infrastructure/Services/InviteCodeService.cs src/JiraUnpacked.Infrastructure/DependencyInjection.cs
git commit -F - <<'EOF'
feat: InviteCodeService (generate single-use expiring codes, FR-A4)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 2: DTO + Admin-only InviteCodesController

**Files:**
- Create: `src/JiraUnpacked.Api/Contracts/Admin/InviteCodeResponse.cs`
- Create: `src/JiraUnpacked.Api/Controllers/InviteCodesController.cs`

- [ ] **Step 1: Create the response DTO**

`src/JiraUnpacked.Api/Contracts/Admin/InviteCodeResponse.cs`:
```csharp
namespace JiraUnpacked.Api.Contracts.Admin;

public sealed record InviteCodeResponse(string Code, DateTime ExpiresAtUtc);
```

- [ ] **Step 2: Create the controller**

`src/JiraUnpacked.Api/Controllers/InviteCodesController.cs`:
```csharp
using JiraUnpacked.Api.Contracts.Admin;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Route("api/admin/invite-codes")]
[Authorize(Policy = "Admin")]
public sealed class InviteCodesController : ControllerBase
{
    private readonly IInviteCodeService _inviteCodes;

    public InviteCodesController(IInviteCodeService inviteCodes) => _inviteCodes = inviteCodes;

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var adminId = Guid.Parse(User.FindFirst("sub")!.Value);
        var result = await _inviteCodes.CreateAsync(adminId, ct);
        return Ok(new InviteCodeResponse(result.Code, result.ExpiresAtUtc));
    }
}
```
(The `sub` claim is the admin's user id — `MapInboundClaims=false` keeps the raw `sub` claim. The `Admin` policy guarantees an authenticated Admin, so `sub` is present.)

- [ ] **Step 3: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add src/JiraUnpacked.Api/Contracts/Admin/ src/JiraUnpacked.Api/Controllers/InviteCodesController.cs
git commit -F - <<'EOF'
feat: Admin-only invite-code creation endpoint

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 3: Integration tests

**Files:**
- Create: `tests/JiraUnpacked.Tests/Admin/InviteCodesEndpointTests.cs`

Requires the local Postgres container running. Tests seed users directly (unique emails), log in for a bearer token, and exercise the endpoint. Authorization: Admin → 200; EndUser → 403; anonymous → 401.

- [ ] **Step 1: Write the tests**

`tests/JiraUnpacked.Tests/Admin/InviteCodesEndpointTests.cs`:
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

public sealed class InviteCodesEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public InviteCodesEndpointTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    // Seeds an Active user with the given role; returns (login, password).
    private async Task<(string login, string password)> SeedUserAsync(UserRole role)
    {
        const string password = "Correct-Horse-9!";
        var unique = Guid.NewGuid().ToString("N");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"user-{unique}@example.com",
            Username = $"user-{unique}",
            PasswordHash = hasher.Hash(password),
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Email, password);
    }

    private async Task<HttpClient> LoggedInClientAsync(UserRole role)
    {
        var (login, password) = await SeedUserAsync(role);
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });
        var tokens = await loginResponse.Content.ReadFromJsonAsync<TokenResponseDto>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    [Fact]
    public async Task Admin_CanCreateInviteCode()
    {
        var client = await LoggedInClientAsync(UserRole.Admin);

        var response = await client.PostAsync("/api/admin/invite-codes", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InviteCodeBody>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Code));
        Assert.True(body.ExpiresAtUtc > DateTime.UtcNow);

        // Persisted, unused, attributed to an admin.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.InviteCodes.SingleAsync(c => c.Code == body.Code);
        Assert.Null(row.UsedByUserId);
        Assert.NotEqual(Guid.Empty, row.CreatedByAdminId);
    }

    [Fact]
    public async Task EndUser_CannotCreateInviteCode_Returns403()
    {
        var client = await LoggedInClientAsync(UserRole.EndUser);

        var response = await client.PostAsync("/api/admin/invite-codes", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_CannotCreateInviteCode_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/admin/invite-codes", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EachCall_GeneratesUniqueCode()
    {
        var client = await LoggedInClientAsync(UserRole.Admin);

        var first = await (await client.PostAsync("/api/admin/invite-codes", content: null))
            .Content.ReadFromJsonAsync<InviteCodeBody>();
        var second = await (await client.PostAsync("/api/admin/invite-codes", content: null))
            .Content.ReadFromJsonAsync<InviteCodeBody>();

        Assert.NotEqual(first!.Code, second!.Code);
    }

    private sealed record InviteCodeBody(string Code, DateTime ExpiresAtUtc);
}
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test --filter FullyQualifiedName~InviteCodesEndpointTests`
Expected: PASS — 4 passed.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test JiraUnpacked.sln -c Debug`
Expected: 32 passed (28 existing + 4 new).

- [ ] **Step 4: Commit**

```bash
git add tests/JiraUnpacked.Tests/Admin/InviteCodesEndpointTests.cs
git commit -F - <<'EOF'
test: invite-code endpoint integration tests (admin/enduser/anon, uniqueness)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Final verification

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings, 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → 32 passing.
- [ ] **Push the branch** (`git push -u origin feat/invite-codes`) BEFORE the PR — verify `origin` tip equals local HEAD.
- [ ] Open PR → `develop`; confirm CI (Postgres service) is green; merge.

---

## Roadmap (remaining Phase 2 plans)
- **#6/#7 registration + email verification** (FR-A1/A2 — invite-gated; redeems the codes created here, enforcing single-use + expiry; `IEmailSender` dev sender). · **#9 block/unblock** (FR-A5 — block revokes refresh tokens).
