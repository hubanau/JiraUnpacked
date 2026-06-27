# Invite-Gated Registration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an End User register with a valid invite code: `POST /api/auth/register` creates a `PendingVerification` account, atomically redeems the invite (single-use), stores a hashed verification code, and emails it (FR-A1 registration half + FR-A2).

**Architecture:** A new `IRegistrationService`/`RegistrationService` orchestrates the flow against `AppDbContext`, reusing `IPasswordHasher`, `PasswordPolicy`, `IClock`, and a new `IEmailSender`. The invite is redeemed with a conditional `ExecuteUpdateAsync` inside a transaction so a code can't be used twice. `AuthController` gains an anonymous, rate-limited `register` action. Verification codes are 6-digit, stored hashed via a new `EmailVerification.HashCode` helper.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, bcrypt, xUnit + `WebApplicationFactory`.

## Global Constraints

- Target framework `net8.0`; EF Core + Npgsql pinned to `8.0.11`.
- Email is stored normalized via `User.NormalizeEmail` (trim + lowercase) — writers must normalize before persisting and before uniqueness checks.
- Secrets/codes are stored hashed, never in plaintext; passwords are bcrypt; `PasswordHash` is never returned or logged.
- Every commit message ends with a blank line then `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- Local Docker Postgres must be running for integration tests (`docker compose up -d`).

**Scope:** Plan 6 of the Phase 2 series — issue #6 (registration half of FR-A1) + #7 (FR-A2). **Verification** (`verify`/`resend` → `Active`) and the per-code attempt cap are the **next** plan. Spec: `docs/superpowers/specs/2026-06-23-auth-approach-design.md` §6.1.

---

## File Structure

- Create `src/JiraUnpacked.Domain/Abstractions/IEmailSender.cs` — email port.
- Create `src/JiraUnpacked.Infrastructure/Messaging/LoggingEmailSender.cs` — dev sender (logs the code).
- Modify `src/JiraUnpacked.Domain/Entities/EmailVerification.cs` — add `HashCode` static helper.
- Create `src/JiraUnpacked.Domain/Abstractions/IRegistrationService.cs` — interface + `RegistrationResult` + `RegistrationError`.
- Create `src/JiraUnpacked.Infrastructure/Services/RegistrationService.cs` — the orchestration.
- Modify `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` — register `PasswordPolicy`, `IEmailSender`, `IRegistrationService`.
- Create `src/JiraUnpacked.Api/Contracts/Auth/RegisterRequest.cs` — request DTO.
- Modify `src/JiraUnpacked.Api/Controllers/AuthController.cs` — inject `IRegistrationService`; add `register` action.
- Create `tests/JiraUnpacked.Tests/Integration/CapturingEmailSender.cs` — test double capturing the sent code.
- Modify `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs` — register the capturing sender.
- Create `tests/JiraUnpacked.Tests/Auth/RegistrationEndpointTests.cs` — integration tests.

Reused: `User` (`NormalizeEmail`, fields), `UserRole.EndUser`, `UserStatus.PendingVerification`, `InviteCode`, `EmailVerification` (`Id, UserId, CodeHash, ExpiresAt, ConsumedAt`), `PasswordPolicy`, `IPasswordHasher`, `IClock`, `AppDbContext`, the `"auth"` rate-limiter, `JiraUnpackedApiFactory`.

---

## Task 1: IEmailSender + dev sender + EmailVerification.HashCode

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IEmailSender.cs`
- Create: `src/JiraUnpacked.Infrastructure/Messaging/LoggingEmailSender.cs`
- Modify: `src/JiraUnpacked.Domain/Entities/EmailVerification.cs`

**Interfaces:**
- Produces: `IEmailSender.SendVerificationCodeAsync(string email, string code, CancellationToken ct = default) : Task`; `EmailVerification.HashCode(string code) : string` (static).

- [ ] **Step 1: Create the email port**

`src/JiraUnpacked.Domain/Abstractions/IEmailSender.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

public interface IEmailSender
{
    Task SendVerificationCodeAsync(string email, string code, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create the dev sender**

`src/JiraUnpacked.Infrastructure/Messaging/LoggingEmailSender.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace JiraUnpacked.Infrastructure.Messaging;

/// <summary>
/// Development email sender — logs the verification code instead of sending it.
/// A real provider (AWS SES/SMTP) replaces this in a later phase.
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendVerificationCodeAsync(string email, string code, CancellationToken ct = default)
    {
        _logger.LogInformation("Verification code for {Email}: {Code}", email, code);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Add the code-hash helper to EmailVerification**

In `src/JiraUnpacked.Domain/Entities/EmailVerification.cs`, add this static method inside `public class EmailVerification`, after the last property (`ConsumedAt`). It uses fully-qualified BCL types so no `using` changes are needed:
```csharp
    /// <summary>
    /// Canonical hash of a verification code for storage/comparison. Codes are
    /// low-entropy, so online attempts are additionally rate-limited and expire.
    /// </summary>
    public static string HashCode(string code) =>
        Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IEmailSender.cs src/JiraUnpacked.Infrastructure/Messaging/LoggingEmailSender.cs src/JiraUnpacked.Domain/Entities/EmailVerification.cs
git commit -F - <<'EOF'
feat: IEmailSender + dev LoggingEmailSender + EmailVerification.HashCode

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 2: IRegistrationService + RegistrationService + DI

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IRegistrationService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/RegistrationService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`

**Interfaces:**
- Consumes: `IEmailSender.SendVerificationCodeAsync`, `EmailVerification.HashCode`, `User.NormalizeEmail`, `PasswordPolicy.Validate`, `IPasswordHasher.Hash`, `IClock.UtcNow`.
- Produces: `IRegistrationService.RegisterAsync(string inviteCode, string email, string username, string password, CancellationToken ct = default) : Task<RegistrationResult>`; `RegistrationResult(bool Succeeded, RegistrationError Error, IReadOnlyList<string> Errors)` with factories `Ok()`, `Fail(RegistrationError)`, `Weak(IReadOnlyList<string>)`; `enum RegistrationError { None, InvalidInviteCode, WeakPassword, EmailOrUsernameTaken }`.

Behavior is DB-orchestration; validated by the integration tests in Task 4.

- [ ] **Step 1: Create the abstraction**

`src/JiraUnpacked.Domain/Abstractions/IRegistrationService.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

public enum RegistrationError
{
    None,
    InvalidInviteCode,
    WeakPassword,
    EmailOrUsernameTaken
}

public sealed record RegistrationResult(bool Succeeded, RegistrationError Error, IReadOnlyList<string> Errors)
{
    public static RegistrationResult Ok() => new(true, RegistrationError.None, Array.Empty<string>());
    public static RegistrationResult Fail(RegistrationError error) => new(false, error, Array.Empty<string>());
    public static RegistrationResult Weak(IReadOnlyList<string> errors) => new(false, RegistrationError.WeakPassword, errors);
}

public interface IRegistrationService
{
    Task<RegistrationResult> RegisterAsync(
        string inviteCode, string email, string username, string password, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create the implementation**

`src/JiraUnpacked.Infrastructure/Services/RegistrationService.cs`:
```csharp
using System.Security.Cryptography;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Domain.Security;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class RegistrationService : IRegistrationService
{
    private const int CodeTtlMinutes = 15;

    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly PasswordPolicy _policy;
    private readonly IEmailSender _emailSender;
    private readonly IClock _clock;

    public RegistrationService(
        AppDbContext db, IPasswordHasher hasher, PasswordPolicy policy, IEmailSender emailSender, IClock clock)
    {
        _db = db;
        _hasher = hasher;
        _policy = policy;
        _emailSender = emailSender;
        _clock = clock;
    }

    public async Task<RegistrationResult> RegisterAsync(
        string inviteCode, string email, string username, string password, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var normalizedEmail = User.NormalizeEmail(email);

        // Reject an invalid/expired/used invite before creating anything (FR-A2).
        var inviteOk = await _db.InviteCodes.AnyAsync(
            c => c.Code == inviteCode && c.UsedByUserId == null && c.ExpiresAt > now, ct);
        if (!inviteOk)
        {
            return RegistrationResult.Fail(RegistrationError.InvalidInviteCode);
        }

        var policy = _policy.Validate(password);
        if (!policy.IsValid)
        {
            return RegistrationResult.Weak(policy.Errors);
        }

        var taken = await _db.Users.AnyAsync(
            u => u.Email == normalizedEmail || u.Username == username, ct);
        if (taken)
        {
            return RegistrationResult.Fail(RegistrationError.EmailOrUsernameTaken);
        }

        var userId = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Insert the user first so it exists as the FK target for the invite's UsedByUserId.
        _db.Users.Add(new User
        {
            Id = userId,
            Email = normalizedEmail,
            Username = username,
            PasswordHash = _hasher.Hash(password),
            Role = UserRole.EndUser,
            Status = UserStatus.PendingVerification,
            CreatedAt = now
        });
        await _db.SaveChangesAsync(ct);

        // Atomically consume the invite; if a concurrent registration already claimed
        // it, zero rows update and we roll back — a code can never be used twice.
        var claimed = await _db.InviteCodes
            .Where(c => c.Code == inviteCode && c.UsedByUserId == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UsedByUserId, userId)
                .SetProperty(c => c.UsedAt, now), ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return RegistrationResult.Fail(RegistrationError.InvalidInviteCode);
        }

        _db.EmailVerifications.Add(new EmailVerification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CodeHash = EmailVerification.HashCode(code),
            ExpiresAt = now.AddMinutes(CodeTtlMinutes)
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Send the code after the account is committed (don't hold the transaction on I/O).
        await _emailSender.SendVerificationCodeAsync(normalizedEmail, code, ct);

        return RegistrationResult.Ok();
    }
}
```

- [ ] **Step 3: Register the services**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, add `using JiraUnpacked.Domain.Security;` and `using JiraUnpacked.Infrastructure.Messaging;` to the usings, and register inside `AddInfrastructure` after the `services.AddScoped<IInviteCodeService, InviteCodeService>();` line:
```csharp
        services.AddSingleton<PasswordPolicy>();
        services.AddSingleton<IEmailSender, LoggingEmailSender>();
        services.AddScoped<IRegistrationService, RegistrationService>();
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IRegistrationService.cs src/JiraUnpacked.Infrastructure/Services/RegistrationService.cs src/JiraUnpacked.Infrastructure/DependencyInjection.cs
git commit -F - <<'EOF'
feat: RegistrationService (invite-gated, atomic redeem, emailed code; FR-A1/A2)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 3: RegisterRequest DTO + AuthController.register

**Files:**
- Create: `src/JiraUnpacked.Api/Contracts/Auth/RegisterRequest.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/AuthController.cs`

**Interfaces:**
- Consumes: `IRegistrationService.RegisterAsync`, `RegistrationError`.

- [ ] **Step 1: Create the request DTO**

`src/JiraUnpacked.Api/Contracts/Auth/RegisterRequest.cs`:
```csharp
namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record RegisterRequest(string InviteCode, string Email, string Username, string Password);
```

- [ ] **Step 2: Inject IRegistrationService into AuthController**

In `src/JiraUnpacked.Api/Controllers/AuthController.cs`, the controller currently has a constructor taking `IAuthService auth`. Replace the field + constructor so it also takes `IRegistrationService`:
```csharp
    private readonly IAuthService _auth;
    private readonly IRegistrationService _registration;

    public AuthController(IAuthService auth, IRegistrationService registration)
    {
        _auth = auth;
        _registration = registration;
    }
```
(`IRegistrationService` is in `JiraUnpacked.Domain.Abstractions`, already imported by the existing `using JiraUnpacked.Domain.Abstractions;` in this file.)

- [ ] **Step 3: Add the register action**

Add this action to `AuthController` (e.g. above the `login` action). It needs `using Microsoft.AspNetCore.RateLimiting;` and `using Microsoft.AspNetCore.Authorization;` — both are already in the file:
```csharp
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var result = await _registration.RegisterAsync(
            request.InviteCode, request.Email, request.Username, request.Password, ct);

        return result.Error switch
        {
            RegistrationError.None => Ok(new { message = "Registration received. Check your email for a verification code." }),
            RegistrationError.InvalidInviteCode => Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid, expired, or already-used invite code."),
            RegistrationError.WeakPassword => Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Password does not meet the strength policy.",
                detail: string.Join(" ", result.Errors)),
            RegistrationError.EmailOrUsernameTaken => Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Email or username is already in use."),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Api/Contracts/Auth/RegisterRequest.cs src/JiraUnpacked.Api/Controllers/AuthController.cs
git commit -F - <<'EOF'
feat: POST /api/auth/register endpoint

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 4: Test email sender + registration integration tests

**Files:**
- Create: `tests/JiraUnpacked.Tests/Integration/CapturingEmailSender.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs`
- Create: `tests/JiraUnpacked.Tests/Auth/RegistrationEndpointTests.cs`

**Interfaces:**
- Consumes: `IEmailSender`, `IRegistrationService` (via HTTP), `JiraUnpackedApiFactory`.
- Produces: `CapturingEmailSender.LastCodeFor(string email) : string?` for tests.

- [ ] **Step 1: Create the capturing test sender**

`tests/JiraUnpacked.Tests/Integration/CapturingEmailSender.cs`:
```csharp
using System.Collections.Concurrent;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>Test <see cref="IEmailSender"/> that records the last code sent per email.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentDictionary<string, string> _codes = new();

    public Task SendVerificationCodeAsync(string email, string code, CancellationToken ct = default)
    {
        _codes[email] = code;
        return Task.CompletedTask;
    }

    public string? LastCodeFor(string email) => _codes.TryGetValue(email, out var code) ? code : null;
}
```

- [ ] **Step 2: Register the capturing sender in the factory**

In `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs`, inside the existing `builder.ConfigureTestServices(services => { ... });` block, append (after the rate-limiter override):
```csharp
            // Replace the dev email sender with a capturing one so tests can read the code.
            var emailDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmailSender));
            if (emailDescriptor is not null)
            {
                services.Remove(emailDescriptor);
            }
            services.AddSingleton<CapturingEmailSender>();
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<CapturingEmailSender>());
```
Add `using JiraUnpacked.Domain.Abstractions;` to the factory's usings if not present.

- [ ] **Step 3: Write the integration tests**

`tests/JiraUnpacked.Tests/Auth/RegistrationEndpointTests.cs`:
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

public sealed class RegistrationEndpointTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public RegistrationEndpointTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Seeds an admin (FK target) + an invite code; returns the code string. expired/used override the state.
    private async Task<string> SeedInviteAsync(bool expired = false, bool used = false)
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
            ExpiresAt = expired ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(7),
            UsedByUserId = used ? admin.Id : null,
            UsedAt = used ? DateTime.UtcNow : null
        });
        await db.SaveChangesAsync();
        return code;
    }

    private static object RegisterBody(string inviteCode, string email, string username, string password) =>
        new { inviteCode, email, username, password };

    [Fact]
    public async Task Register_WithValidInvite_CreatesPendingUser_ConsumesInvite_AndSendsCode()
    {
        var code = await SeedInviteAsync();
        var unique = Guid.NewGuid().ToString("N");
        var email = $"newuser-{unique}@example.com";
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, email, $"newuser-{unique}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        Assert.Equal(UserStatus.PendingVerification, user.Status);
        Assert.Equal(UserRole.EndUser, user.Role);

        var invite = await db.InviteCodes.SingleAsync(c => c.Code == code);
        Assert.Equal(user.Id, invite.UsedByUserId);

        var sender = _factory.Services.GetRequiredService<CapturingEmailSender>();
        Assert.False(string.IsNullOrEmpty(sender.LastCodeFor(email)));
    }

    [Fact]
    public async Task Register_NormalizesEmail()
    {
        var code = await SeedInviteAsync();
        var unique = Guid.NewGuid().ToString("N");
        var mixedCase = $"NewUser-{unique}@Example.COM";
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, mixedCase, $"newuser-{unique}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Users.AnyAsync(u => u.Email == $"newuser-{unique}@example.com"));
    }

    [Fact]
    public async Task Register_WithInvalidInvite_Returns422_AndCreatesNoUser()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"newuser-{unique}@example.com";
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody("does-not-exist", email, $"newuser-{unique}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task Register_WithExpiredInvite_Returns422()
    {
        var code = await SeedInviteAsync(expired: true);
        var unique = Guid.NewGuid().ToString("N");
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, $"newuser-{unique}@example.com", $"newuser-{unique}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithUsedInvite_Returns422()
    {
        var code = await SeedInviteAsync(used: true);
        var unique = Guid.NewGuid().ToString("N");
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, $"newuser-{unique}@example.com", $"newuser-{unique}", "Correct-Horse-9!"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithWeakPassword_Returns400()
    {
        var code = await SeedInviteAsync();
        var unique = Guid.NewGuid().ToString("N");
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code, $"newuser-{unique}@example.com", $"newuser-{unique}", "weak"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_Returns409()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = $"dupe-{unique}@example.com";

        // First registration succeeds.
        var code1 = await SeedInviteAsync();
        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code1, email, $"first-{unique}", "Correct-Horse-9!"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second with the same email (different username, fresh invite) conflicts.
        var code2 = await SeedInviteAsync();
        var second = await client.PostAsJsonAsync("/api/auth/register",
            RegisterBody(code2, email.ToUpperInvariant(), $"second-{unique}", "Correct-Horse-9!"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }
}
```

- [ ] **Step 4: Run the new tests**

Run: `dotnet test --filter FullyQualifiedName~RegistrationEndpointTests`
Expected: PASS — 7 passed.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test JiraUnpacked.sln -c Debug`
Expected: 45 passed (38 existing + 7 new).

- [ ] **Step 6: Commit**

```bash
git add tests/JiraUnpacked.Tests/Integration/CapturingEmailSender.cs tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs tests/JiraUnpacked.Tests/Auth/RegistrationEndpointTests.cs
git commit -F - <<'EOF'
test: registration endpoint integration tests + capturing email sender

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Final verification

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings, 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → 45 passing.
- [ ] **Push the branch** (`git push -u origin feat/registration`) BEFORE the PR — verify `origin` tip equals local HEAD.
- [ ] Open PR → `develop`; confirm CI (Postgres service) is green; merge.

---

## Roadmap (remaining Phase 2 plans)
- **Verification** — `verify` + `verify/resend` endpoints (`PendingVerification` → `Active`, attempt cap + `EmailVerification.Attempts` column/migration, resend throttle), and the full register → verify → login integration test.
- **#9 block/unblock** (FR-A5 — block sets `Blocked` and revokes the user's refresh tokens).
