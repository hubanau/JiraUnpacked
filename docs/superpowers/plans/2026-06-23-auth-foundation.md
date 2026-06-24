# Auth Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the reusable security primitives for Phase 2 identity — password hashing, password policy, a testable clock, JWT access-token + refresh-token issuance, and the `RefreshToken` entity/migration — all unit-tested, with no HTTP endpoints yet.

**Architecture:** Approach A (lightweight custom identity). Pure abstractions live in `JiraUnpacked.Domain`; implementations live in `JiraUnpacked.Infrastructure` and are registered through `AddInfrastructure`. No ASP.NET Core Identity, no endpoints — those arrive in the next plan.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, `BCrypt.Net-Next` (hashing), `Microsoft.IdentityModel.JsonWebTokens` (JWT), xUnit.

**Scope note:** This is plan 1 of the Phase 2 series. See "Roadmap" at the end for the subsequent plans. Spec: `docs/superpowers/specs/2026-06-23-auth-approach-design.md`.

---

## File Structure

**Domain (abstractions + entity, no dependencies):**
- Create `src/JiraUnpacked.Domain/Abstractions/IClock.cs` — UTC time source.
- Create `src/JiraUnpacked.Domain/Abstractions/IPasswordHasher.cs` — hash/verify.
- Create `src/JiraUnpacked.Domain/Abstractions/ITokenService.cs` — token issuance + result records.
- Create `src/JiraUnpacked.Domain/Security/PasswordPolicy.cs` — pure validator (FR-A6).
- Create `src/JiraUnpacked.Domain/Entities/RefreshToken.cs` — entity.

**Infrastructure (implementations):**
- Create `src/JiraUnpacked.Infrastructure/Time/SystemClock.cs`.
- Create `src/JiraUnpacked.Infrastructure/Security/BcryptPasswordHasher.cs`.
- Create `src/JiraUnpacked.Infrastructure/Security/JwtOptions.cs`.
- Create `src/JiraUnpacked.Infrastructure/Security/JwtTokenService.cs`.
- Create `src/JiraUnpacked.Infrastructure/Persistence/Configurations/RefreshTokenConfiguration.cs`.
- Modify `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs` — add `RefreshTokens` set.
- Modify `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` — take `IConfiguration`, register services, bind `JwtOptions`.
- Generated: `src/JiraUnpacked.Infrastructure/Persistence/Migrations/*_AddRefreshTokens.cs`.

**Api (wiring only):**
- Modify `src/JiraUnpacked.Api/Program.cs` — update the `AddInfrastructure` call.
- Modify `src/JiraUnpacked.Api/appsettings.Development.json` — add a `Jwt` section.

**Tests:**
- Create `tests/JiraUnpacked.Tests/Security/FakeClock.cs` — test double.
- Create `tests/JiraUnpacked.Tests/Security/BcryptPasswordHasherTests.cs`.
- Create `tests/JiraUnpacked.Tests/Security/PasswordPolicyTests.cs`.
- Create `tests/JiraUnpacked.Tests/Security/JwtTokenServiceTests.cs`.
- Modify `tests/JiraUnpacked.Tests/Persistence/AppDbContextModelTests.cs` — assert `RefreshToken` mapped.

---

## Task 1: Add NuGet packages to Infrastructure

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/JiraUnpacked.Infrastructure.csproj`

- [ ] **Step 1: Add the packages**

Run:
```bash
dotnet add src/JiraUnpacked.Infrastructure package BCrypt.Net-Next --version 4.0.3
dotnet add src/JiraUnpacked.Infrastructure package Microsoft.IdentityModel.JsonWebTokens --version 8.*
```

- [ ] **Step 2: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/JiraUnpacked.Infrastructure.csproj
git commit -m "chore: add bcrypt + JWT packages to Infrastructure"
```

---

## Task 2: IClock + SystemClock + FakeClock

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IClock.cs`
- Create: `src/JiraUnpacked.Infrastructure/Time/SystemClock.cs`
- Create: `tests/JiraUnpacked.Tests/Security/FakeClock.cs`

- [ ] **Step 1: Create the abstraction**

`src/JiraUnpacked.Domain/Abstractions/IClock.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

/// <summary>UTC time source, injected so time-dependent logic is testable.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
```

- [ ] **Step 2: Create the implementation**

`src/JiraUnpacked.Infrastructure/Time/SystemClock.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
```

- [ ] **Step 3: Create the test double**

`tests/JiraUnpacked.Tests/Security/FakeClock.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Security;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTime utcNow) => UtcNow = utcNow;
    public DateTime UtcNow { get; set; }
}
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IClock.cs src/JiraUnpacked.Infrastructure/Time/SystemClock.cs tests/JiraUnpacked.Tests/Security/FakeClock.cs
git commit -m "feat: add IClock abstraction with system + fake implementations"
```

---

## Task 3: Password hashing (bcrypt)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IPasswordHasher.cs`
- Create: `src/JiraUnpacked.Infrastructure/Security/BcryptPasswordHasher.cs`
- Test: `tests/JiraUnpacked.Tests/Security/BcryptPasswordHasherTests.cs`

- [ ] **Step 1: Create the abstraction**

`src/JiraUnpacked.Domain/Abstractions/IPasswordHasher.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}
```

- [ ] **Step 2: Write the failing test**

`tests/JiraUnpacked.Tests/Security/BcryptPasswordHasherTests.cs`:
```csharp
using JiraUnpacked.Infrastructure.Security;

namespace JiraUnpacked.Tests.Security;

public class BcryptPasswordHasherTests
{
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public void Hash_DoesNotReturnPlaintext()
    {
        var hash = _hasher.Hash("Correct-Horse-9!");
        Assert.NotEqual("Correct-Horse-9!", hash);
        Assert.StartsWith("$2", hash); // bcrypt format marker
    }

    [Fact]
    public void Verify_ReturnsTrue_ForCorrectPassword()
    {
        var hash = _hasher.Hash("Correct-Horse-9!");
        Assert.True(_hasher.Verify("Correct-Horse-9!", hash));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForWrongPassword()
    {
        var hash = _hasher.Hash("Correct-Horse-9!");
        Assert.False(_hasher.Verify("wrong-password", hash));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~BcryptPasswordHasherTests`
Expected: FAIL — `BcryptPasswordHasher` does not exist (compile error).

- [ ] **Step 4: Write the implementation**

`src/JiraUnpacked.Infrastructure/Security/BcryptPasswordHasher.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Security;

public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 11;

    public string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool Verify(string password, string hash) =>
        BCrypt.Net.BCrypt.Verify(password, hash);
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~BcryptPasswordHasherTests`
Expected: PASS — 3 passed.

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IPasswordHasher.cs src/JiraUnpacked.Infrastructure/Security/BcryptPasswordHasher.cs tests/JiraUnpacked.Tests/Security/BcryptPasswordHasherTests.cs
git commit -m "feat: bcrypt password hasher (FR-A6)"
```

---

## Task 4: Password policy (FR-A6)

**Files:**
- Create: `src/JiraUnpacked.Domain/Security/PasswordPolicy.cs`
- Test: `tests/JiraUnpacked.Tests/Security/PasswordPolicyTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/JiraUnpacked.Tests/Security/PasswordPolicyTests.cs`:
```csharp
using JiraUnpacked.Domain.Security;

namespace JiraUnpacked.Tests.Security;

public class PasswordPolicyTests
{
    private readonly PasswordPolicy _policy = new(); // defaults: min 12, >=3 classes

    [Fact]
    public void Validate_Accepts_StrongPassword()
    {
        var result = _policy.Validate("Correct-Horse-9!");
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_Rejects_TooShort()
    {
        var result = _policy.Validate("Ab1!");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("12"));
    }

    [Fact]
    public void Validate_Rejects_TooFewCharacterClasses()
    {
        var result = _policy.Validate("alllowercaseletters"); // 1 class, long enough
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("character"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~PasswordPolicyTests`
Expected: FAIL — `PasswordPolicy` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

`src/JiraUnpacked.Domain/Security/PasswordPolicy.cs`:
```csharp
namespace JiraUnpacked.Domain.Security;

public sealed record PasswordValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed class PasswordPolicy
{
    public int MinLength { get; init; } = 12;
    public int MinCharacterClasses { get; init; } = 3;

    public PasswordValidationResult Validate(string password)
    {
        var errors = new List<string>();
        password ??= string.Empty;

        if (password.Length < MinLength)
            errors.Add($"Password must be at least {MinLength} characters.");

        var classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;

        if (classes < MinCharacterClasses)
            errors.Add($"Password must contain at least {MinCharacterClasses} character classes (lower, upper, digit, symbol).");

        return new PasswordValidationResult(errors.Count == 0, errors);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~PasswordPolicyTests`
Expected: PASS — 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Security/PasswordPolicy.cs tests/JiraUnpacked.Tests/Security/PasswordPolicyTests.cs
git commit -m "feat: password strength policy (FR-A6)"
```

---

## Task 5: RefreshToken entity + migration

**Files:**
- Create: `src/JiraUnpacked.Domain/Entities/RefreshToken.cs`
- Create: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/RefreshTokenConfiguration.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs`
- Modify: `tests/JiraUnpacked.Tests/Persistence/AppDbContextModelTests.cs`
- Generated: `src/JiraUnpacked.Infrastructure/Persistence/Migrations/*_AddRefreshTokens.cs`

- [ ] **Step 1: Create the entity**

`src/JiraUnpacked.Domain/Entities/RefreshToken.cs`:
```csharp
namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A revocable refresh token for a <see cref="User"/>. Only the SHA-256 hash of
/// the issued token is stored. Rotation links the old token to its replacement.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
}
```

- [ ] **Step 2: Create the configuration**

`src/JiraUnpacked.Infrastructure/Persistence/Configurations/RefreshTokenConfiguration.cs`:
```csharp
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();

        builder.Property(t => t.ExpiresAt).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();

        builder.HasIndex(t => t.UserId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

- [ ] **Step 3: Add the DbSet**

Modify `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs` — add this line alongside the other `DbSet` properties:
```csharp
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
```
(`ApplyConfigurationsFromAssembly` already picks up the new configuration — no change to `OnModelCreating`.)

- [ ] **Step 4: Extend the model test**

Add to `tests/JiraUnpacked.Tests/Persistence/AppDbContextModelTests.cs` inside `AppDbContextModelTests`:
```csharp
    [Fact]
    public void Model_Includes_RefreshToken_With_Unique_TokenHash()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(Domain.Entities.RefreshToken))!;

        Assert.NotNull(entity);
        Assert.Contains(entity.GetIndexes(),
            i => i.IsUnique && i.Properties.Any(p => p.Name == nameof(Domain.Entities.RefreshToken.TokenHash)));
    }
```

- [ ] **Step 5: Run the model test**

Run: `dotnet test --filter FullyQualifiedName~AppDbContextModelTests`
Expected: PASS — 4 passed.

- [ ] **Step 6: Generate the migration**

Run:
```bash
dotnet ef migrations add AddRefreshTokens --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure --output-dir Persistence/Migrations
```
Expected: `Done.` and a new `*_AddRefreshTokens.cs` under `Persistence/Migrations`.

- [ ] **Step 7: Apply the migration to local Postgres**

Run:
```bash
dotnet ef database update --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```
Expected: `Applying migration '..._AddRefreshTokens'.` then `Done.`

- [ ] **Step 8: Commit**

```bash
git add src/JiraUnpacked.Domain/Entities/RefreshToken.cs src/JiraUnpacked.Infrastructure/Persistence/Configurations/RefreshTokenConfiguration.cs src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs src/JiraUnpacked.Infrastructure/Persistence/Migrations/ tests/JiraUnpacked.Tests/Persistence/AppDbContextModelTests.cs
git commit -m "feat: RefreshToken entity + migration"
```

---

## Task 6: JWT token service (access + refresh issuance)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/ITokenService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Security/JwtOptions.cs`
- Create: `src/JiraUnpacked.Infrastructure/Security/JwtTokenService.cs`
- Test: `tests/JiraUnpacked.Tests/Security/JwtTokenServiceTests.cs`

- [ ] **Step 1: Create the abstraction + result records**

`src/JiraUnpacked.Domain/Abstractions/ITokenService.cs`:
```csharp
using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Domain.Abstractions;

public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);

/// <summary>The raw token is returned to the client; only <see cref="TokenHash"/> is persisted.</summary>
public sealed record RefreshTokenValue(string Token, string TokenHash, DateTime ExpiresAtUtc);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);
    RefreshTokenValue CreateRefreshToken();
    string HashRefreshToken(string rawToken);
}
```

- [ ] **Step 2: Create the options**

`src/JiraUnpacked.Infrastructure/Security/JwtOptions.cs`:
```csharp
namespace JiraUnpacked.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "JiraUnpacked";
    public string Audience { get; set; } = "JiraUnpacked";

    /// <summary>HMAC signing key; must be at least 32 characters (256 bits) for HS256.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}
```

- [ ] **Step 3: Write the failing test**

`tests/JiraUnpacked.Tests/Security/JwtTokenServiceTests.cs`:
```csharp
using System.Security.Cryptography;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace JiraUnpacked.Tests.Security;

public class JwtTokenServiceTests
{
    private static JwtTokenService CreateService(FakeClock clock)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = "test-iss",
            Audience = "test-aud",
            SigningKey = "this-is-a-32-byte-minimum-test-key!!",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7
        });
        return new JwtTokenService(options, clock);
    }

    private static User SampleUser() => new()
    {
        Id = Guid.NewGuid(),
        Email = "a@b.com",
        Username = "alice",
        PasswordHash = "x",
        Role = UserRole.EndUser,
        Status = UserStatus.Active
    };

    [Fact]
    public void CreateAccessToken_EmbedsClaims_AndExpiry()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = CreateService(new FakeClock(now));
        var user = SampleUser();

        var token = service.CreateAccessToken(user);

        Assert.Equal(now.AddMinutes(15), token.ExpiresAtUtc);

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.Token);
        Assert.Equal(user.Id.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal("EndUser", jwt.GetClaim("role").Value);
        Assert.Equal(user.Id.ToString(), jwt.GetClaim("endUserId").Value);
    }

    [Fact]
    public void CreateRefreshToken_ReturnsRawWithMatchingHashAndExpiry()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = CreateService(new FakeClock(now));

        var refresh = service.CreateRefreshToken();

        Assert.False(string.IsNullOrWhiteSpace(refresh.Token));
        Assert.Equal(now.AddDays(7), refresh.ExpiresAtUtc);
        Assert.Equal(service.HashRefreshToken(refresh.Token), refresh.TokenHash);
        Assert.NotEqual(refresh.Token, refresh.TokenHash);
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~JwtTokenServiceTests`
Expected: FAIL — `JwtTokenService` does not exist (compile error).

- [ ] **Step 5: Write the implementation**

`src/JiraUnpacked.Infrastructure/Security/JwtTokenService.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace JiraUnpacked.Infrastructure.Security;

public sealed class JwtTokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly IClock _clock;

    public JwtTokenService(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public AccessToken CreateAccessToken(User user)
    {
        var now = _clock.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(),
                ["role"] = user.Role.ToString(),
                ["endUserId"] = user.Id.ToString()
            }
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new AccessToken(token, expires);
    }

    public RefreshTokenValue CreateRefreshToken()
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var expires = _clock.UtcNow.AddDays(_options.RefreshTokenDays);
        return new RefreshTokenValue(raw, HashRefreshToken(raw), expires);
    }

    public string HashRefreshToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToBase64String(bytes);
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~JwtTokenServiceTests`
Expected: PASS — 2 passed.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/ITokenService.cs src/JiraUnpacked.Infrastructure/Security/JwtOptions.cs src/JiraUnpacked.Infrastructure/Security/JwtTokenService.cs tests/JiraUnpacked.Tests/Security/JwtTokenServiceTests.cs
git commit -m "feat: JWT access + refresh token service"
```

---

## Task 7: Register services + bind options in DI

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Modify: `src/JiraUnpacked.Api/Program.cs`
- Modify: `src/JiraUnpacked.Api/appsettings.Development.json`

- [ ] **Step 1: Rewrite `AddInfrastructure` to take `IConfiguration`**

Replace the body of `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` with:
```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Security;
using JiraUnpacked.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'Default' is not configured.");
        }

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();

        return services;
    }
}
```
This requires `Microsoft.Extensions.Configuration.Abstractions`, which already flows in transitively via EF Core. If the build reports it missing, run `dotnet add src/JiraUnpacked.Infrastructure package Microsoft.Extensions.Configuration.Abstractions --version 8.*`.

- [ ] **Step 2: Update the Program.cs call**

In `src/JiraUnpacked.Api/Program.cs`, replace:
```csharp
builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("Default"));
```
with:
```csharp
builder.Services.AddInfrastructure(builder.Configuration);
```

- [ ] **Step 3: Add the Jwt dev config**

In `src/JiraUnpacked.Api/appsettings.Development.json`, add a top-level `Jwt` section alongside `ConnectionStrings`:
```json
  "Jwt": {
    "Issuer": "JiraUnpacked",
    "Audience": "JiraUnpacked",
    "SigningKey": "dev-only-signing-key-change-me-min-32-chars",
    "AccessTokenMinutes": 15,
    "RefreshTokenDays": 7
  }
```

- [ ] **Step 4: Build + run the full suite**

Run: `dotnet build JiraUnpacked.sln -c Debug` then `dotnet test JiraUnpacked.sln -c Debug`
Expected: build succeeds; all tests pass (scaffold ×1 + model ×4 + hasher ×3 + policy ×3 + token ×2 = 13).

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Infrastructure/DependencyInjection.cs src/JiraUnpacked.Api/Program.cs src/JiraUnpacked.Api/appsettings.Development.json
git commit -m "feat: register auth services + bind JwtOptions"
```

---

## Final verification

- [ ] Run `dotnet build JiraUnpacked.sln -c Release` → 0 warnings, 0 errors.
- [ ] Run `dotnet test JiraUnpacked.sln -c Release` → all green.
- [ ] Open PR `feat/auth-foundation` → `develop`; confirm CI is green; merge.

---

## Roadmap (subsequent Phase 2 plans)

Each becomes its own plan doc + PR after this one merges:

1. **Auth endpoints & wiring** — `AuthService` (login/refresh/logout against the DB), `AuthController`, JWT bearer authentication + `"Admin"` authorization policy, rate limiting, **Postgres service added to CI**, and the first WebApplicationFactory integration tests. (Spec §6.3–6.5, §5, §9.)
2. **#5 — Admin seeding** — `AdminSeeder` from configuration, invoked at startup (FR-A3).
3. **#8 — Invite-code creation** — `InviteCodeService` + `InviteCodesController` (Admin) (FR-A4).
4. **#6 / #7 — Registration + verification** — `RegistrationService`, `EmailVerificationService`, `IEmailSender` + dev sender, invite-gating (FR-A1, FR-A2).
5. **#9 — Block / unblock** — `AdminUsersController`, status change + refresh-token revocation (FR-A5).
