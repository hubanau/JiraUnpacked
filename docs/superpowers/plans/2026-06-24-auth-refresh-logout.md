# Auth Refresh + Logout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add refresh-token rotation (`POST /api/auth/refresh`) and logout/revocation (`POST /api/auth/logout`) on top of the existing login vertical.

**Architecture:** Extends the existing `IAuthService`/`AuthService` and `AuthController`. Refresh validates the presented token by its SHA-256 hash, re-checks the user is still Active, rotates (revokes the old token, links it to its replacement, issues a new access + refresh pair). Logout revokes the presented token, idempotently. Reuses the `WebApplicationFactory`+Postgres integration harness from the previous slice.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, `Microsoft.AspNetCore.Mvc.Testing` + xUnit.

**Scope:** Plan 3 of the Phase 2 series. Refresh + logout only. Spec: `docs/superpowers/specs/2026-06-23-auth-approach-design.md` (§6.4 refresh, §6.5 logout, FR-A5 hook). After this: #5 admin seeding, #8 invite creation, #6/#7 registration + verification, #9 block/unblock.

**Preconditions:** local Docker Postgres running (`docker compose up -d` → healthy) for the integration tests.

---

## File Structure

- Modify `src/JiraUnpacked.Domain/Abstractions/IAuthService.cs` — add `RefreshAsync` + `LogoutAsync` to the interface (reuse the existing `LoginResult` record for the refresh token bundle).
- Modify `src/JiraUnpacked.Infrastructure/Services/AuthService.cs` — implement `RefreshAsync` (rotation) + `LogoutAsync` (revoke).
- Create `src/JiraUnpacked.Api/Contracts/Auth/RefreshTokenRequest.cs` — request DTO carrying the raw refresh token (shared by both endpoints).
- Modify `src/JiraUnpacked.Api/Controllers/AuthController.cs` — add `refresh` + `logout` actions.
- Modify `tests/JiraUnpacked.Tests/Integration/AuthEndpointsTests.cs` — add refresh + logout integration tests.

Existing pieces reused (already on `develop`): `ITokenService` (`CreateAccessToken`, `CreateRefreshToken`, `HashRefreshToken`), `IClock`, `RefreshToken` entity (`Id, UserId, TokenHash, ExpiresAt, CreatedAt, RevokedAt?, ReplacedByTokenHash?`), `AppDbContext.RefreshTokens`/`Users`, `UserStatus`, `LoginResult` record `(bool Succeeded, string? AccessToken, DateTime? AccessTokenExpiresAtUtc, string? RefreshToken)`, `TokenResponse` record `(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken)`, the `"auth"` rate limiter, and `JiraUnpackedApiFactory`.

---

## Task 1: Extend AuthService with refresh + logout

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/IAuthService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Services/AuthService.cs`

Behavior is DB-orchestration, validated by the integration tests in Task 3 (consistent with how `LoginAsync` is tested).

- [ ] **Step 1: Add the interface methods**

In `src/JiraUnpacked.Domain/Abstractions/IAuthService.cs`, add two members to the `IAuthService` interface (keep the existing `LoginAsync`; reuse `LoginResult` for refresh):
```csharp
    Task<LoginResult> RefreshAsync(string refreshToken, CancellationToken ct = default);

    Task LogoutAsync(string refreshToken, CancellationToken ct = default);
```
The full interface becomes:
```csharp
public interface IAuthService
{
    Task<LoginResult> LoginAsync(string emailOrUsername, string password, CancellationToken ct = default);

    Task<LoginResult> RefreshAsync(string refreshToken, CancellationToken ct = default);

    Task LogoutAsync(string refreshToken, CancellationToken ct = default);
}
```

- [ ] **Step 2: Implement the methods**

In `src/JiraUnpacked.Infrastructure/Services/AuthService.cs`, add these two methods to the `AuthService` class (after `LoginAsync`):
```csharp
    public async Task<LoginResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = _tokens.HashRefreshToken(refreshToken);
        var existing = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        var now = _clock.UtcNow;

        // Reject unknown, already-revoked (incl. rotated), or expired tokens.
        if (existing is null || existing.RevokedAt is not null || existing.ExpiresAt <= now)
        {
            return new LoginResult(false, null, null, null);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == existing.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
        {
            return new LoginResult(false, null, null, null);
        }

        var access = _tokens.CreateAccessToken(user);
        var newRefresh = _tokens.CreateRefreshToken();

        // Rotate: revoke the presented token and link it to its replacement.
        existing.RevokedAt = now;
        existing.ReplacedByTokenHash = newRefresh.TokenHash;

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = newRefresh.TokenHash,
            CreatedAt = now,
            ExpiresAt = newRefresh.ExpiresAtUtc
        });

        await _db.SaveChangesAsync(ct);

        return new LoginResult(true, access.Token, access.ExpiresAtUtc, newRefresh.Token);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = _tokens.HashRefreshToken(refreshToken);
        var existing = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, ct);

        if (existing is not null)
        {
            existing.RevokedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        // Idempotent: unknown or already-revoked tokens are a no-op (no error, no info leak).
    }
```

- [ ] **Step 3: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add src/JiraUnpacked.Domain/Abstractions/IAuthService.cs src/JiraUnpacked.Infrastructure/Services/AuthService.cs
git commit -F - <<'EOF'
feat: AuthService refresh-token rotation + logout revocation

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 2: DTO + AuthController refresh + logout endpoints

**Files:**
- Create: `src/JiraUnpacked.Api/Contracts/Auth/RefreshTokenRequest.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/AuthController.cs`

- [ ] **Step 1: Create the DTO**

`src/JiraUnpacked.Api/Contracts/Auth/RefreshTokenRequest.cs`:
```csharp
namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record RefreshTokenRequest(string RefreshToken);
```

- [ ] **Step 2: Add the controller actions**

In `src/JiraUnpacked.Api/Controllers/AuthController.cs`, add these two actions inside the `AuthController` class (after the `Login` action, before `Me`):
```csharp
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(RefreshTokenRequest request, CancellationToken ct)
    {
        var result = await _auth.RefreshAsync(request.RefreshToken, ct);
        if (!result.Succeeded)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid or expired refresh token.");
        }

        return Ok(new TokenResponse(
            result.AccessToken!, result.AccessTokenExpiresAtUtc!.Value, result.RefreshToken!));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(RefreshTokenRequest request, CancellationToken ct)
    {
        await _auth.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }
```
(No new `using` directives are needed — `RefreshTokenRequest` is in the already-imported `JiraUnpacked.Api.Contracts.Auth`, and `EnableRateLimiting`/`AllowAnonymous`/`Mvc` are already imported by the existing file.)

- [ ] **Step 3: Verify build**

Run: `dotnet build JiraUnpacked.sln -c Debug`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add src/JiraUnpacked.Api/Contracts/Auth/RefreshTokenRequest.cs src/JiraUnpacked.Api/Controllers/AuthController.cs
git commit -F - <<'EOF'
feat: AuthController refresh + logout endpoints

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Task 3: Integration tests (refresh + logout)

**Files:**
- Modify: `tests/JiraUnpacked.Tests/Integration/AuthEndpointsTests.cs`

Requires the local Postgres container running.

- [ ] **Step 1: Add the tests**

Add these test methods inside the existing `AuthEndpointsTests` class in `tests/JiraUnpacked.Tests/Integration/AuthEndpointsTests.cs` (the class already has `_factory`, `SeedUserAsync`, and the `TokenResponseDto` record). Also add a small helper to log in and return the token pair, and to insert a refresh token row directly for the expiry case:
```csharp
    private async Task<TokenResponseDto> LoginAndGetTokensAsync()
    {
        var (login, password) = await SeedUserAsync();
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });
        return (await response.Content.ReadFromJsonAsync<TokenResponseDto>())!;
    }

    [Fact]
    public async Task Refresh_WithValidToken_RotatesAndReturnsNewTokens()
    {
        var tokens = await LoginAndGetTokensAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = await response.Content.ReadFromJsonAsync<TokenResponseDto>();
        Assert.NotNull(rotated);
        Assert.False(string.IsNullOrWhiteSpace(rotated!.RefreshToken));
        Assert.NotEqual(tokens.RefreshToken, rotated.RefreshToken);
    }

    [Fact]
    public async Task Refresh_WithUnknownToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = "not-a-real-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithRotatedToken_Returns401()
    {
        var tokens = await LoginAndGetTokensAsync();
        var client = _factory.CreateClient();

        // First refresh rotates (revokes) the original token.
        await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });

        // Reusing the original (now-revoked) token must fail.
        var reuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Logout_ThenRefresh_Returns401_AndIsIdempotent()
    {
        var tokens = await LoginAndGetTokensAsync();
        var client = _factory.CreateClient();

        var logout1 = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout1.StatusCode);

        // Logout is idempotent — a second call still succeeds.
        var logout2 = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout2.StatusCode);

        // The revoked token can no longer be refreshed.
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Refresh_WhenUserBlocked_Returns401()
    {
        var (login, password) = await SeedUserAsync();
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = login, password });
        var tokens = (await loginResponse.Content.ReadFromJsonAsync<TokenResponseDto>())!;

        // Block the user after login. (Synchronous Single/SaveChanges via System.Linq,
        // so no extra `using Microsoft.EntityFrameworkCore;` is needed in this file.)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = db.Users.Single(u => u.Email == login);
            user.Status = UserStatus.Blocked;
            db.SaveChanges();
        }

        var response = await client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test --filter FullyQualifiedName~AuthEndpointsTests`
Expected: PASS — 10 tests in the class (5 from the previous slice + 5 new refresh/logout).

- [ ] **Step 3: Run the full suite**

Run: `dotnet test JiraUnpacked.sln -c Debug`
Expected: 25 passed (15 unit + 10 integration).

- [ ] **Step 4: Commit**

```bash
git add tests/JiraUnpacked.Tests/Integration/AuthEndpointsTests.cs
git commit -F - <<'EOF'
test: integration tests for refresh rotation + logout

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
```

---

## Final verification

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings, 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → 25 passing.
- [ ] **Push the branch** (`git push -u origin feat/auth-refresh-logout`) BEFORE opening the PR — verify `origin` tip equals local HEAD.
- [ ] Open PR → `develop`; confirm CI (with the Postgres service) is green; merge.

---

## Roadmap (remaining Phase 2 plans)
- **#5 Admin seeding** (FR-A3) · **#8 invite creation** (FR-A4) · **#6/#7 registration + email verification** (FR-A1/A2) · **#9 block/unblock** (FR-A5 — block sets `Status=Blocked` and revokes all the user's refresh tokens, which `RefreshAsync` already rejects via the Active-status re-check).
