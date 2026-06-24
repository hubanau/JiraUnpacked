# Auth Endpoints & Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the login vertical and the authenticated request pipeline: JWT bearer validation, an `Admin` authorization policy, rate limiting, `POST /api/auth/login` (issues access + refresh tokens), a protected `GET /api/auth/me`, plus a `WebApplicationFactory`+Postgres integration-test harness and a Postgres service in CI.

**Architecture:** Builds on the auth-foundation primitives (`ITokenService`, `IPasswordHasher`, `IClock`, `RefreshToken`). A new `IAuthService`/`AuthService` orchestrates login against `AppDbContext`. The Api wires JWT bearer auth (validating tokens minted by `JwtTokenService`), an `Admin` policy, and a per-endpoint rate limiter. Integration tests run against a real Postgres (`jiraunpacked_test` DB locally; a Postgres service in CI).

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, `Microsoft.AspNetCore.Authentication.JwtBearer`, built-in rate limiting, `Microsoft.AspNetCore.Mvc.Testing` + xUnit.

**Scope:** This is plan 2 of the Phase 2 series. Login only. **Refresh + logout** are the next plan (they extend `AuthService`/`AuthController` and reuse this harness). Spec: `docs/superpowers/specs/2026-06-23-auth-approach-design.md` (§5 authz, §6.3 login, §9 testing).

---

## File Structure

**Api:**
- Modify `src/JiraUnpacked.Api/JiraUnpacked.Api.csproj` — add JwtBearer package.
- Modify `src/JiraUnpacked.Api/Program.cs` — JWT bearer auth, `Admin` policy, rate limiter, pipeline order.
- Create `src/JiraUnpacked.Api/Contracts/Auth/LoginRequest.cs`, `TokenResponse.cs`.
- Create `src/JiraUnpacked.Api/Controllers/AuthController.cs` — `login` + `me`.

**Domain / Infrastructure:**
- Create `src/JiraUnpacked.Domain/Abstractions/IAuthService.cs` — interface + `LoginResult`.
- Create `src/JiraUnpacked.Infrastructure/Services/AuthService.cs` — `LoginAsync`.
- Modify `src/JiraUnpacked.Infrastructure/DependencyInjection.cs` — register `IAuthService`.

**CI:**
- Modify `.github/workflows/ci.yml` — add a `postgres` service + test connection string.

**Tests:**
- Modify `tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj` — add `Microsoft.AspNetCore.Mvc.Testing`.
- Create `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs` — WebApplicationFactory over a test Postgres DB.
- Create `tests/JiraUnpacked.Tests/Integration/AuthEndpointsTests.cs` — login + me integration tests.

---

## Task 1: Add packages

**Files:**
- Modify: `src/JiraUnpacked.Api/JiraUnpacked.Api.csproj`
- Modify: `tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj`

- [ ] **Step 1: Add the packages**

```bash
dotnet add src/JiraUnpacked.Api package Microsoft.AspNetCore.Authentication.JwtBearer --version 8.0.11
dotnet add tests/JiraUnpacked.Tests package Microsoft.AspNetCore.Mvc.Testing --version 8.0.11
```

- [ ] **Step 2: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/JiraUnpacked.Api/JiraUnpacked.Api.csproj tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj
git commit -F - <<'EOF'
chore: add JwtBearer + Mvc.Testing packages

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 2: IAuthService + AuthService (login)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IAuthService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/AuthService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`

No unit test here — `AuthService` is DB-orchestration and is covered by the integration tests in Task 7. (It has no isolated branching logic worth a separate unit test beyond the uniform-failure path, which the integration tests assert.)

- [ ] **Step 1: Create the abstraction**

`src/JiraUnpacked.Domain/Abstractions/IAuthService.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

public sealed record LoginResult(
    bool Succeeded,
    string? AccessToken,
    DateTime? AccessTokenExpiresAtUtc,
    string? RefreshToken);

public interface IAuthService
{
    Task<LoginResult> LoginAsync(string emailOrUsername, string password, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create the implementation**

`src/JiraUnpacked.Infrastructure/Services/AuthService.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ITokenService _tokens;
    private readonly IClock _clock;

    public AuthService(AppDbContext db, IPasswordHasher hasher, ITokenService tokens, IClock clock)
    {
        _db = db;
        _hasher = hasher;
        _tokens = tokens;
        _clock = clock;
    }

    public async Task<LoginResult> LoginAsync(string emailOrUsername, string password, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Email == emailOrUsername || u.Username == emailOrUsername, ct);

        // Uniform failure for unknown user, non-active status, or wrong password
        // (no enumeration; same response for every reason).
        if (user is null || user.Status != UserStatus.Active || !_hasher.Verify(password, user.PasswordHash))
        {
            return new LoginResult(false, null, null, null);
        }

        var access = _tokens.CreateAccessToken(user);
        var refresh = _tokens.CreateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refresh.TokenHash,
            CreatedAt = _clock.UtcNow,
            ExpiresAt = refresh.ExpiresAtUtc
        });
        await _db.SaveChangesAsync(ct);

        return new LoginResult(true, access.Token, access.ExpiresAtUtc, refresh.Token);
    }
}
```

- [ ] **Step 3: Register the service**

In `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, add `using JiraUnpacked.Infrastructure.Services;` and register inside `AddInfrastructure` after the existing singletons (scoped, because it depends on the scoped `AppDbContext`):
```csharp
        services.AddScoped<IAuthService, AuthService>();
```

- [ ] **Step 4: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IAuthService.cs src/JiraUnpacked.Infrastructure/Services/AuthService.cs src/JiraUnpacked.Infrastructure/DependencyInjection.cs
git commit -F - <<'EOF'
feat: AuthService.LoginAsync (issue access + refresh tokens)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 3: DTOs + AuthController (login + me)

**Files:**
- Create: `src/JiraUnpacked.Api/Contracts/Auth/LoginRequest.cs`
- Create: `src/JiraUnpacked.Api/Contracts/Auth/TokenResponse.cs`
- Create: `src/JiraUnpacked.Api/Controllers/AuthController.cs`

- [ ] **Step 1: Create the DTOs**

`src/JiraUnpacked.Api/Contracts/Auth/LoginRequest.cs`:
```csharp
namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record LoginRequest(string EmailOrUsername, string Password);
```

`src/JiraUnpacked.Api/Contracts/Auth/TokenResponse.cs`:
```csharp
namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record TokenResponse(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
```

- [ ] **Step 2: Create the controller**

`src/JiraUnpacked.Api/Controllers/AuthController.cs`:
```csharp
using JiraUnpacked.Api.Contracts.Auth;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth) => _auth = auth;

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request.EmailOrUsername, request.Password, ct);
        if (!result.Succeeded)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials.");
        }

        return Ok(new TokenResponse(
            result.AccessToken!, result.AccessTokenExpiresAtUtc!.Value, result.RefreshToken!));
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        sub = User.FindFirst("sub")?.Value,
        role = User.FindFirst("role")?.Value,
        endUserId = User.FindFirst("endUserId")?.Value
    });
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded`. (It builds even though `"auth"` limiter + auth middleware are added in Task 4 — attributes are not validated at build time.)

- [ ] **Step 4: Commit**

```bash
git add src/JiraUnpacked.Api/Contracts/Auth/ src/JiraUnpacked.Api/Controllers/AuthController.cs
git commit -F - <<'EOF'
feat: AuthController login + me endpoints

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 4: Wire JWT bearer auth, Admin policy, rate limiting

**Files:**
- Modify: `src/JiraUnpacked.Api/Program.cs`

- [ ] **Step 1: Update Program.cs**

Replace the contents of `src/JiraUnpacked.Api/Program.cs` with:
```csharp
using System.Text;
using JiraUnpacked.Infrastructure;
using JiraUnpacked.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// EF Core (Npgsql) + AppDbContext. Connection string resolved per environment.
builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("Default"));

// Bind + validate JWT options at startup (fail fast if the signing key is unsafe).
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.SigningKey) && o.SigningKey.Length >= 32,
        "Jwt:SigningKey must be configured with at least 32 characters.")
    .ValidateOnStart();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()!;

// JWT bearer authentication — validates tokens minted by JwtTokenService.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep raw claim names (sub/role/endUserId)
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
    options.AddPolicy("Admin", policy => policy.RequireRole("Admin")));

// Rate limiting for sensitive auth endpoints.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth", o =>
    {
        o.Window = TimeSpan.FromMinutes(1);
        o.PermitLimit = 10;
        o.QueueLimit = 0;
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposed so the test project can drive the API with WebApplicationFactory<Program>.
public partial class Program { }
```

- [ ] **Step 2: Verify build + existing tests still pass**

Run: `dotnet build JiraUnpacked.sln -c Debug` then `dotnet test JiraUnpacked.sln -c Debug`
Expected: build succeeds; the existing 15 unit tests still pass.

- [ ] **Step 3: Commit**

```bash
git add src/JiraUnpacked.Api/Program.cs
git commit -F - <<'EOF'
feat: JWT bearer auth, Admin policy, and rate limiting

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 5: Add Postgres service to CI

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Add a Postgres service + test DB env to the build-test job**

In `.github/workflows/ci.yml`, add a `services:` block to the `build-test` job (sibling of `steps:`) and an `env:` so the integration tests have a database. The full job becomes:
```yaml
  build-test:
    name: Build & Test
    runs-on: ubuntu-latest
    timeout-minutes: 15

    services:
      postgres:
        image: postgres:16
        env:
          POSTGRES_DB: jiraunpacked
          POSTGRES_USER: jiraunpacked
          POSTGRES_PASSWORD: localdev
        ports:
          - 5432:5432
        options: >-
          --health-cmd "pg_isready -U jiraunpacked -d jiraunpacked"
          --health-interval 5s
          --health-timeout 5s
          --health-retries 10

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x

      - name: Restore
        run: dotnet restore JiraUnpacked.sln

      - name: Build
        run: dotnet build JiraUnpacked.sln --configuration Release --no-restore

      - name: Test
        run: dotnet test JiraUnpacked.sln --configuration Release --no-build --verbosity normal
```
(The integration-test factory connects to `localhost:5432` with these credentials and uses a `jiraunpacked_test` database — see Task 6. The superuser `jiraunpacked` can create it.)

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -F - <<'EOF'
ci: add Postgres service for integration tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 6: Integration-test harness

**Files:**
- Create: `tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs`

- [ ] **Step 1: Create the factory**

`tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs`:
```csharp
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Spins up the API against a dedicated local/CI Postgres database
/// (`jiraunpacked_test`), applying migrations once on first use. Tests use
/// unique data (random emails/usernames) so they don't collide.
/// </summary>
public sealed class JiraUnpackedApiFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5432;Database=jiraunpacked_test;Username=jiraunpacked;Password=localdev";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Override the DbContext registration to point at the test database.
        builder.ConfigureTestServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(TestConnectionString));
        });
    }

    public async Task EnsureDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    public AppDbContext CreateDbContext()
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add tests/JiraUnpacked.Tests/Integration/JiraUnpackedApiFactory.cs
git commit -F - <<'EOF'
test: WebApplicationFactory harness over a test Postgres database

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 7: Integration tests (login + me)

**Files:**
- Create: `tests/JiraUnpacked.Tests/Integration/AuthEndpointsTests.cs`

Requires the local Postgres container running (`docker compose up -d`); CI provides it via Task 5.

- [ ] **Step 1: Write the integration tests**

`tests/JiraUnpacked.Tests/Integration/AuthEndpointsTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Integration;

public sealed class AuthEndpointsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;

    public AuthEndpointsTests(JiraUnpackedApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    // Seeds an Active user with a known password; returns (login id, password).
    private async Task<(string login, string password)> SeedUserAsync(UserStatus status = UserStatus.Active)
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
            Role = UserRole.EndUser,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (user.Email, password);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokens()
    {
        var (login, password) = await SeedUserAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TokenResponseDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var (login, _) = await SeedUserAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WhenBlocked_Returns401()
    {
        var (login, password) = await SeedUserAsync(UserStatus.Blocked);
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithValidToken_Returns200()
    {
        var (login, password) = await SeedUserAsync();
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });
        var tokens = await loginResponse.Content.ReadFromJsonAsync<TokenResponseDto>();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        var meResponse = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }
}
```

- [ ] **Step 2: Run the integration tests**

Run: `dotnet test --filter FullyQualifiedName~AuthEndpointsTests`
Expected: PASS — 5 passed. (Requires the local Postgres container running.)

- [ ] **Step 3: Run the full suite**

Run: `dotnet test JiraUnpacked.sln -c Debug`
Expected: 20 passed (15 unit + 5 integration).

- [ ] **Step 4: Commit**

```bash
git add tests/JiraUnpacked.Tests/Integration/AuthEndpointsTests.cs
git commit -F - <<'EOF'
test: integration tests for login + protected /me endpoint

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Final verification

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings, 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → 20 passing.
- [ ] **Push the branch** (`git push -u origin feat/auth-endpoints`) BEFORE opening the PR — verify `origin/feat/auth-endpoints` tip equals local HEAD.
- [ ] Open PR → `develop`; confirm CI (with the Postgres service) is green; merge.

---

## Roadmap (next Phase 2 plans)
1. **Refresh + logout** — `AuthService.RefreshAsync` (rotate, revoke-old, re-check Active) + `LogoutAsync` (revoke) + `AuthController` actions + integration tests (reuses this harness).
2. **#5 Admin seeding** · **#8 invite creation** · **#6/#7 registration + verification** · **#9 block/unblock** (block revokes refresh tokens).
