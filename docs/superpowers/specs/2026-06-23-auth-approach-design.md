# Phase 2 — Identity & Auth: Design

**Date:** 2026-06-23
**Status:** Approved (design); pending implementation plan
**Covers:** REQUIREMENTS FR-A1…FR-A6 (Module A — Identity & access), issues #5–#10

## 1. Context & goal

Phase 2 builds the identity layer on top of the entities migrated in Phase 1
(`User`, `InviteCode`, `EmailVerification`). It delivers invite-gated End User
registration with email verification, admin seeding, admin-issued invite codes,
block/unblock, a strong password policy, and JWT-based authentication for API
clients.

Tenant isolation (FR-B2) is **not** part of this phase, but the auth layer plants
one forward hook for it (an `endUserId` claim — see §5).

## 2. Locked constraints (from brainstorming)

1. **Client model:** separate SPA / mobile / API clients → **stateless JWT bearer** auth.
2. **Email delivery:** an `IEmailSender` abstraction with a **dev (logging) sender**
   now; a real provider (AWS SES/SMTP) is deferred to a later phase.
3. **Sessions & revocation:** **short-lived access tokens + revocable refresh tokens**
   (rotation; revoke-on-block) so FR-A5 blocking actually ends a session.
4. **Architecture:** **Approach A — lightweight custom identity** on the existing
   entities, hashing with **bcrypt**. Not ASP.NET Core Identity; not PBKDF2.

## 3. Architecture & layering

Honors the locked 4-project structure (no new Application project).

- **Domain** — entities + interfaces, no dependencies:
  - New entity: `RefreshToken`.
  - New interfaces: `IPasswordHasher`, `ITokenService`, `IEmailSender`, `IClock`.
- **Infrastructure** — implementations (depend on EF Core / libraries):
  - `BcryptPasswordHasher` (BCrypt.Net-Next), `JwtTokenService`
    (Microsoft.IdentityModel.JsonWebTokens), `LoggingEmailSender` (dev), `SystemClock`.
  - Orchestration services: `RegistrationService`, `EmailVerificationService`,
    `InviteCodeService`, `AuthService`, and `AdminSeeder`.
  - `AppDbContext` gains a `RefreshTokens` set + configuration + migration.
- **Api** — thin controllers + wiring only:
  - `AuthController`, `InviteCodesController`, `AdminUsersController`.
  - JWT bearer authentication, authorization policies, rate-limiting, and the
    `AdminSeeder` invocation at startup.

Controllers depend on the service interfaces; implementations are registered in
`AddInfrastructure`. Each service has one clear responsibility and is unit-testable
in isolation.

## 4. Data model addition

**`RefreshToken`** (tenant-agnostic; belongs to a `User`):

| Column | Type | Notes |
|---|---|---|
| Id | uuid | PK |
| UserId | uuid | FK → Users, **cascade** delete |
| TokenHash | text | **SHA-256 of the random token**; never plaintext |
| ExpiresAt | timestamptz | |
| CreatedAt | timestamptz | |
| RevokedAt | timestamptz? | null while active |
| ReplacedByTokenHash | text? | set on rotation, for audit/chaining |

Indexes: **unique** on `TokenHash`; non-unique on `UserId`. Delivered as a second
migration (`AddRefreshTokens`).

The refresh token presented to clients is a 256-bit cryptographically-random,
base64url-encoded string. Only its SHA-256 hash is stored (high-entropy tokens do
not need a slow hash).

## 5. Authentication & authorization

- **JWT:** HS256, signing key + issuer + audience from configuration (dev:
  appsettings; prod: secrets, later). Access-token lifetime ~15 min.
- **Claims:** `sub` = user id, `role` = Admin|EndUser, and **`endUserId`** = the
  user's own id for End Users. The `endUserId` claim is a deliberate forward hook
  for the Phase 3 tenant query filter; nothing in Phase 2 enforces tenancy.
- **Authorization:** `[Authorize]` by default; an `"Admin"` policy guards admin
  endpoints. `register` / `verify` / `login` / `refresh` are anonymous.

## 6. Endpoints & flows

All bodies/responses use `ProblemDetails` for errors. Account-existence is never
revealed (generic messages) to prevent enumeration.

### 6.1 Registration — `POST /api/auth/register` (anonymous; FR-A1, FR-A2)
Body: `{ inviteCode, email, username, password }`. In one transaction:
1. Validate invite code: exists, not expired, not used → else reject **before**
   creating anything.
2. Enforce password policy (§7); reject weak passwords.
3. Enforce unique email and username.
4. Create `User { Role = EndUser, Status = PendingVerification,
   PasswordHash = bcrypt(password) }`.
5. Consume the invite code (`UsedByUserId`, `UsedAt`).
6. Create `EmailVerification { CodeHash, ExpiresAt }` and send a 6-digit code via
   `IEmailSender`.

No token is issued. Response: 200/202 with a "check your email" message.

### 6.2 Verify — `POST /api/auth/verify` and `POST /api/auth/verify/resend` (anonymous; FR-A1)
- Verify: look up the user's active `EmailVerification`; check unexpired,
  unconsumed, and under the attempt cap; compare code hash → set `Status = Active`
  and `ConsumedAt`. Registration cannot complete without a valid code.
- Resend: throttled; issues a fresh code and invalidates the previous one.

### 6.3 Login — `POST /api/auth/login` (anonymous)
Body: `{ emailOrUsername, password }`. Only `Active` users authenticate; bcrypt
verify → issue access JWT + refresh token (store its hash). `Blocked` or
`PendingVerification` → generic 401. Rate-limited.

### 6.4 Refresh — `POST /api/auth/refresh` (anonymous)
Body: `{ refreshToken }`. Look up by hash; validate (not expired, not revoked);
**re-check the user is still Active** (blocked → reject); rotate: revoke the old
token (set `RevokedAt`, `ReplacedByTokenHash`) and issue a new access + refresh pair.

### 6.5 Logout — `POST /api/auth/logout`
Revokes the presented refresh token.

### 6.6 Invite codes — `POST /api/admin/invite-codes` (Admin; FR-A4)
Generates a single-use, expiring, shareable code (`CreatedByAdminId` = caller).
Used/expired codes cannot be reused.

### 6.7 Block / unblock — `POST /api/admin/users/{id}/block` · `/unblock` (Admin; FR-A5)
- Block → `Status = Blocked` **and revoke all of that user's refresh tokens**, so
  the access token dies within its ≤15-min TTL and refresh is impossible.
- Unblock → `Status = Active` (revoked tokens stay revoked; the user logs in again).
- Pausing the user's scheduled syncs is a **noted hook** for Phase 5 (no sync
  engine exists yet).

### 6.8 Admin seeding — startup (FR-A3)
`AdminSeeder` reads admin account(s) from configuration (email, username, and an
initial password) and idempotently upserts an `Active` `Admin` user if absent. No
admin self-registration path exists. Seeded admins skip email verification.

## 7. Security

- **Password policy (FR-A6):** minimum 12 characters and at least 3 of 4 character
  classes (lower, upper, digit, symbol); configurable. Hash with **bcrypt** (work
  factor 11). `PasswordHash` is never logged or returned in any response.
- **Secrets at rest:** verification codes and refresh tokens are stored hashed; raw
  values exist only transiently.
- **Rate limiting:** ASP.NET Core (.NET 8 built-in) rate limiters on `login`,
  `register`, `verify`, and `verify/resend`.
- **Codes:** 6-digit numeric verification codes, short TTL (~15 min), capped
  attempts, throttled resend.
- **Transport:** TLS assumed at the edge (NFR), out of scope for app code here.

## 8. Error handling

- Validation failures → 400 `ProblemDetails`.
- Invalid/expired/used invite code → 422 (before any account is created).
- Authentication failures (bad credentials, blocked, unverified) → generic 401.
- Authorization failures (non-admin on admin route) → 403.
- Registration runs in a transaction so a partial failure leaves no orphaned user,
  consumed code, or dangling verification.

## 9. Testing

- **Unit:** password-policy validator; bcrypt hash/verify; JWT issue/validate;
  invite-code validation (expiry/used); verification-code logic (expiry, consume,
  attempt cap); refresh-token rotation/revocation.
- **Integration (WebApplicationFactory + Postgres):** end-to-end journeys —
  register → verify → login → refresh → block → (refresh now fails). Admin: seed →
  create invite code → register with it. Negative: reused/expired invite, weak
  password, wrong/expired verification code, login while Pending/Blocked.
- **CI change:** add a Postgres `service` to the GitHub Actions workflow so
  integration tests have a database. This harness is intentionally reused by the
  Phase 3 tenant-isolation gate.

## 10. Build order (maps to issues)

1. **Auth foundation** — `RefreshToken` entity + migration; `IPasswordHasher` +
   bcrypt; `ITokenService` + JWT issuance/validation; refresh rotation;
   `IClock`; JWT bearer + `"Admin"` policy; rate limiting; CI Postgres service.
   (Includes #10 — password policy + hashing.)
2. **#5** — Admin seeding.
3. **#8** — Admin invite-code creation.
4. **#6 / #7** — Registration + email verification + invite-gating.
5. **#9** — Block / unblock.

Each step lands as its own PR into `develop` (PR-by-convention).

## 11. Defaults & tunables (configurable)

| Setting | Default |
|---|---|
| Access-token TTL | 15 minutes |
| Refresh-token TTL | 7 days |
| bcrypt work factor | 11 |
| Verification code | 6 digits, 15-min TTL, max 5 attempts |
| Password policy | min 12 chars, ≥3 of 4 char classes |
| JWT algorithm | HS256 |

## 12. Out of scope (this phase)

- Real email delivery (dev logging sender only).
- Tenant-isolation enforcement (FR-B2) — only the `endUserId` claim is planted.
- Pausing scheduled syncs on block (no sync engine until Phase 5).
- Generic filter builder, Jira connections, reporting.
