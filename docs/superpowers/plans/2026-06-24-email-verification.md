# Email Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete FR-A1 — `POST /api/auth/verify` flips a `PendingVerification` account to `Active` on the correct, unexpired, attempt-limited code; `POST /api/auth/verify/resend` issues a fresh code (throttled).

**Architecture:** A new `IEmailVerificationService`/`EmailVerificationService` operates on the `EmailVerification` rows created at registration, reusing `IClock` and `IEmailSender`. An `Attempts` counter (new column + migration) caps guesses per code. `AuthController` gains anonymous, rate-limited `verify` and `verify/resend` actions.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit + `WebApplicationFactory`.

## Global Constraints

- Target framework `net8.0`; EF Core + Npgsql pinned to `8.0.11`.
- Email is matched via `User.NormalizeEmail` (trim + lowercase).
- Verification codes are compared against the stored hash (`EmailVerification.HashCode`); plaintext codes never stored.
- Generic failure responses — do not reveal whether the email exists or which check failed (except the attempt-cap, which returns 429).
- Every commit message ends with a blank line then `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- Local Docker Postgres must be running for integration tests (`docker compose up -d`).

**Scope:** Plan 7 of the Phase 2 series — verification half of FR-A1 (#6). Finishes #6 (registration #29 delivered the first half). Spec: `docs/superpowers/specs/2026-06-23-auth-approach-design.md` §6.2.

---

## File Structure

- Modify `src/JiraUnpacked.Domain/Entities/EmailVerification.cs` — add `int Attempts` property. (No EF configuration change: `EmailVerificationConfiguration` already exists and the non-nullable `int` maps by convention — only a migration is needed.)
- Generated: `src/JiraUnpacked.Infrastructure/Persistence/Migrations/*_AddEmailVerificationAttempts.cs`.
- Create `src/JiraUnpacked.Domain/Abstractions/IEmailVerificationService.cs` — interface + `VerificationResult` + `VerificationError`.
- Create `src/JiraUnpacked.Infrastructure/Services/EmailVerificationService.cs` — verify + resend.
- Modify `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` — register `IEmailVerificationService`.
- Create `src/JiraUnpacked.Api/Contracts/Auth/VerifyRequest.cs` and `ResendVerificationRequest.cs`.
- Modify `src/JiraUnpacked.Api/Controllers/AuthController.cs` — inject the service; add `verify` + `verify/resend`.
- Create `tests/JiraUnpacked.Tests/Auth/VerificationEndpointTests.cs`.

Reused: `User` (`NormalizeEmail`, `Status`), `UserStatus.PendingVerification`/`Active`, `EmailVerification` (`Id, UserId, CodeHash, ExpiresAt, ConsumedAt`, static `HashCode`), `IClock`, `IEmailSender`, `AppDbContext`, the `"auth"` rate-limiter, `JiraUnpackedApiFactory`, `CapturingEmailSender`.

---

## Task 1: EmailVerification.Attempts + migration

**Files:**
- Modify: `src/JiraUnpacked.Domain/Entities/EmailVerification.cs`
- Generated: `src/JiraUnpacked.Infrastructure/Persistence/Migrations/*_AddEmailVerificationAttempts.cs`

**Interfaces:**
- Produces: `EmailVerification.Attempts` (`int`, default 0).

- [ ] **Step 1: Add the Attempts property**

In `src/JiraUnpacked.Domain/Entities/EmailVerification.cs`, add this property after `ConsumedAt` (and before the `HashCode` static method):
```csharp
    /// <summary>Number of incorrect code submissions; capped to throttle guessing.</summary>
    public int Attempts { get; set; }
```

- [ ] **Step 2: Generate the migration**

Run:
```bash
dotnet ef migrations add AddEmailVerificationAttempts --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure --output-dir Persistence/Migrations
```
Expected: `Done.` and a new `*_AddEmailVerificationAttempts.cs`. Open it and confirm `Up()` does `migrationBuilder.AddColumn<int>(name: "Attempts", table: "EmailVerifications", ... defaultValue: 0)` and nothing else.

- [ ] **Step 3: Apply it to local Postgres**

Run:
```bash
dotnet ef database update --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```
Expected: `Applying migration '..._AddEmailVerificationAttempts'.` then `Done.`

- [ ] **Step 4: Verify no drift + build**

Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure`
Expected: `No changes have been made to the model since the last migration.`
Then `dotnet build JiraUnpacked.sln -c Debug` → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Entities/EmailVerification.cs src/JiraUnpacked.Infrastructure/Persistence/Migrations/
git commit -F - <<'EOF'
feat: EmailVerification.Attempts column + migration

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 2: IEmailVerificationService + EmailVerificationService + DI

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IEmailVerificationService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/EmailVerificationService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`

**Interfaces:**
- Consumes: `EmailVerification.HashCode`, `EmailVerification.Attempts`, `User.NormalizeEmail`, `IClock.UtcNow`, `IEmailSender.SendVerificationCodeAsync`.
- Produces: `IEmailVerificationService.VerifyAsync(string email, string code, CancellationToken)`, `ResendAsync(string email, CancellationToken)`; `VerificationResult(bool Succeeded, VerificationError Error)` with `Ok()`/`Fail(VerificationError)`; `enum VerificationError { None, Invalid, TooManyAttempts }`.

Behavior is DB-orchestration; validated by the integration tests in Task 4.

- [ ] **Step 1: Create the abstraction**

`src/JiraUnpacked.Domain/Abstractions/IEmailVerificationService.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

public enum VerificationError
{
    None,
    Invalid,
    TooManyAttempts
}

public sealed record VerificationResult(bool Succeeded, VerificationError Error)
{
    public static VerificationResult Ok() => new(true, VerificationError.None);
    public static VerificationResult Fail(VerificationError error) => new(false, error);
}

public interface IEmailVerificationService
{
    Task<VerificationResult> VerifyAsync(string email, string code, CancellationToken ct = default);
    Task ResendAsync(string email, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create the implementation**

`src/JiraUnpacked.Infrastructure/Services/EmailVerificationService.cs`:
```csharp
using System.Security.Cryptography;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class EmailVerificationService : IEmailVerificationService
{
    private const int MaxAttempts = 5;
    private const int CodeTtlMinutes = 15;

    private readonly AppDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly IClock _clock;

    public EmailVerificationService(AppDbContext db, IEmailSender emailSender, IClock clock)
    {
        _db = db;
        _emailSender = emailSender;
        _clock = clock;
    }

    public async Task<VerificationResult> VerifyAsync(string email, string code, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var normalizedEmail = User.NormalizeEmail(email);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);
        if (user is null || user.Status != UserStatus.PendingVerification)
        {
            return VerificationResult.Fail(VerificationError.Invalid);
        }

        var verification = await _db.EmailVerifications
            .Where(v => v.UserId == user.Id && v.ConsumedAt == null)
            .OrderByDescending(v => v.ExpiresAt)
            .FirstOrDefaultAsync(ct);

        if (verification is null || verification.ExpiresAt <= now)
        {
            return VerificationResult.Fail(VerificationError.Invalid);
        }

        if (verification.Attempts >= MaxAttempts)
        {
            return VerificationResult.Fail(VerificationError.TooManyAttempts);
        }

        if (verification.CodeHash != EmailVerification.HashCode(code))
        {
            verification.Attempts++;
            await _db.SaveChangesAsync(ct);
            return VerificationResult.Fail(VerificationError.Invalid);
        }

        user.Status = UserStatus.Active;
        verification.ConsumedAt = now;
        await _db.SaveChangesAsync(ct);
        return VerificationResult.Ok();
    }

    public async Task ResendAsync(string email, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var normalizedEmail = User.NormalizeEmail(email);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);
        if (user is null || user.Status != UserStatus.PendingVerification)
        {
            return; // silent no-op — don't reveal whether the account exists/is pending
        }

        // Invalidate any outstanding codes, then issue a fresh one.
        await _db.EmailVerifications
            .Where(v => v.UserId == user.Id && v.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ConsumedAt, now), ct);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _db.EmailVerifications.Add(new EmailVerification
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CodeHash = EmailVerification.HashCode(code),
            ExpiresAt = now.AddMinutes(CodeTtlMinutes)
        });
        await _db.SaveChangesAsync(ct);

        await _emailSender.SendVerificationCodeAsync(normalizedEmail, code, ct);
    }
}
```

- [ ] **Step 3: Register the service**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, register inside `AddInfrastructure` after the `services.AddScoped<IRegistrationService, RegistrationService>();` line:
```csharp
        services.AddScoped<IEmailVerificationService, EmailVerificationService>();
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IEmailVerificationService.cs src/JiraUnpacked.Infrastructure/Services/EmailVerificationService.cs src/JiraUnpacked.Infrastructure/DependencyInjection.cs
git commit -F - <<'EOF'
feat: EmailVerificationService (verify -> Active, attempt cap, resend; FR-A1)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 3: DTOs + verify/resend endpoints

**Files:**
- Create: `src/JiraUnpacked.Api/Contracts/Auth/VerifyRequest.cs`
- Create: `src/JiraUnpacked.Api/Contracts/Auth/ResendVerificationRequest.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/AuthController.cs`

**Interfaces:**
- Consumes: `IEmailVerificationService.VerifyAsync`, `ResendAsync`, `VerificationError`.

- [ ] **Step 1: Create the DTOs**

`src/JiraUnpacked.Api/Contracts/Auth/VerifyRequest.cs`:
```csharp
namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record VerifyRequest(string Email, string Code);
```

`src/JiraUnpacked.Api/Contracts/Auth/ResendVerificationRequest.cs`:
```csharp
namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record ResendVerificationRequest(string Email);
```

- [ ] **Step 2: Inject the service into AuthController**

In `src/JiraUnpacked.Api/Controllers/AuthController.cs`, the constructor currently takes `(IAuthService auth, IRegistrationService registration)`. Add `IEmailVerificationService`:
```csharp
    private readonly IAuthService _auth;
    private readonly IRegistrationService _registration;
    private readonly IEmailVerificationService _verification;

    public AuthController(
        IAuthService auth, IRegistrationService registration, IEmailVerificationService verification)
    {
        _auth = auth;
        _registration = registration;
        _verification = verification;
    }
```

- [ ] **Step 3: Add the verify + resend actions**

Add to `AuthController` (e.g. after the `register` action). The usings (`Microsoft.AspNetCore.Authorization`, `Microsoft.AspNetCore.Mvc`, `Microsoft.AspNetCore.RateLimiting`, `JiraUnpacked.Api.Contracts.Auth`, `JiraUnpacked.Domain.Abstractions`) are already present:
```csharp
    [HttpPost("verify")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Verify(VerifyRequest request, CancellationToken ct)
    {
        var result = await _verification.VerifyAsync(request.Email, request.Code, ct);
        return result.Error switch
        {
            VerificationError.None => Ok(new { message = "Email verified. You can now sign in." }),
            VerificationError.TooManyAttempts => Problem(
                statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts. Request a new code."),
            _ => Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid or expired verification code.")
        };
    }

    [HttpPost("verify/resend")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResendVerification(ResendVerificationRequest request, CancellationToken ct)
    {
        await _verification.ResendAsync(request.Email, ct);
        // Always 202 — never reveal whether the account exists or is pending.
        return Accepted(new { message = "If the account is awaiting verification, a new code has been sent." });
    }
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Api/Contracts/Auth/VerifyRequest.cs src/JiraUnpacked.Api/Contracts/Auth/ResendVerificationRequest.cs src/JiraUnpacked.Api/Controllers/AuthController.cs
git commit -F - <<'EOF'
feat: POST /api/auth/verify + /verify/resend endpoints

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 4: Integration tests (full register -> verify -> login)

**Files:**
- Create: `tests/JiraUnpacked.Tests/Auth/VerificationEndpointTests.cs`

Requires the local Postgres container running. Tests register a real user (so a real hashed code is created), read the plaintext code from `CapturingEmailSender`, and exercise verify/resend.

- [ ] **Step 1: Write the tests**

`tests/JiraUnpacked.Tests/Auth/VerificationEndpointTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Auth;

public sealed class VerificationEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public VerificationEndpointTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> SeedInviteAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = $"admin-{unique}@example.com",
            Username = $"admin-{unique}",
            PasswordHash = "x",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(admin);
        var code = $"invite-{unique}";
        db.InviteCodes.Add(new InviteCode
        {
            Id = Guid.NewGuid(),
            Code = code,
            CreatedByAdminId = admin.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();
        return code;
    }

    // Registers a user; returns (email, password, verificationCode-from-email).
    private async Task<(string email, string password, string code)> RegisterAsync(HttpClient client)
    {
        var invite = await SeedInviteAsync();
        var unique = Guid.NewGuid().ToString("N");
        var email = $"newuser-{unique}@example.com";
        const string password = "Correct-Horse-9!";

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { inviteCode = invite, email, username = $"newuser-{unique}", password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var sender = _factory.Services.GetRequiredService<CapturingEmailSender>();
        var code = sender.LastCodeFor(email)!;
        Assert.False(string.IsNullOrEmpty(code));
        return (email, password, code);
    }

    [Fact]
    public async Task Verify_WithCorrectCode_ActivatesUser_AndAllowsLogin()
    {
        var client = _factory.CreateClient();
        var (email, password, code) = await RegisterAsync(client);

        var verify = await client.PostAsJsonAsync("/api/auth/verify", new { email, code });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == email);
            Assert.Equal(UserStatus.Active, user.Status);
        }

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Login_BeforeVerification_Fails()
    {
        var client = _factory.CreateClient();
        var (email, password, _) = await RegisterAsync(client);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Verify_WithWrongCode_Returns400()
    {
        var client = _factory.CreateClient();
        var (email, _, code) = await RegisterAsync(client);
        var wrong = code == "000000" ? "111111" : "000000";

        var verify = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = wrong });
        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
    }

    [Fact]
    public async Task Verify_AfterTooManyWrongAttempts_Returns429()
    {
        var client = _factory.CreateClient();
        var (email, _, code) = await RegisterAsync(client);
        var wrong = code == "000000" ? "111111" : "000000";

        // 5 wrong attempts exhaust the cap; the 6th is rejected with 429.
        for (var i = 0; i < 5; i++)
        {
            var bad = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = wrong });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        var capped = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = wrong });
        Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
    }

    [Fact]
    public async Task Resend_IssuesNewCode_AndOldCodeNoLongerWorks()
    {
        var client = _factory.CreateClient();
        var (email, _, oldCode) = await RegisterAsync(client);

        var resend = await client.PostAsJsonAsync("/api/auth/verify/resend", new { email });
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);

        var sender = _factory.Services.GetRequiredService<CapturingEmailSender>();
        var newCode = sender.LastCodeFor(email)!;
        Assert.NotEqual(oldCode, newCode);

        // Old code is now invalidated.
        var oldTry = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = oldCode });
        Assert.Equal(HttpStatusCode.BadRequest, oldTry.StatusCode);

        // New code works.
        var newTry = await client.PostAsJsonAsync("/api/auth/verify", new { email, code = newCode });
        Assert.Equal(HttpStatusCode.OK, newTry.StatusCode);
    }

    [Fact]
    public async Task Resend_ForUnknownEmail_Returns202_Silently()
    {
        var client = _factory.CreateClient();
        var resend = await client.PostAsJsonAsync("/api/auth/verify/resend",
            new { email = $"nobody-{Guid.NewGuid():N}@example.com" });
        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
    }
}
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test --filter FullyQualifiedName~VerificationEndpointTests`
Expected: PASS — 6 passed.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test JiraUnpacked.sln -c Debug`
Expected: 52 passed (46 existing + 6 new).

- [ ] **Step 4: Commit**

```bash
git add tests/JiraUnpacked.Tests/Auth/VerificationEndpointTests.cs
git commit -F - <<'EOF'
test: verification endpoint integration tests (register->verify->login, attempts, resend)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Final verification

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings, 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → 52 passing.
- [ ] `dotnet ef migrations has-pending-model-changes ...` → no drift.
- [ ] **Push the branch** (`git push -u origin feat/email-verification`) BEFORE the PR — verify `origin` tip equals local HEAD.
- [ ] Open PR → `develop`; confirm CI (Postgres service) is green; merge. This closes #6 (FR-A1).

---

## Roadmap (remaining Phase 2)
- **#9 block/unblock** (FR-A5 — Admin blocks an End User: set `Blocked` + revoke their refresh tokens; reversible). This is the last Identity (Module A) item; then Module A is complete and the **Phase 3 tenancy gate (FR-B2)** is next — the hard gate before any Jira ingestion.
