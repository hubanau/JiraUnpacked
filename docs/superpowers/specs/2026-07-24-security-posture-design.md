# Phase 7 — Hardening: Security posture design

**Status:** Approved (2026-07-24)
**Epic:** Phase 7 (Hardening)
**This spec:** Spec **3 of 3** of Phase 7 — the last, closing the v1 build.

- Spec 1 — Tenant data lifecycle (GDPR erasure + retention): **shipped** to main.
- Spec 2 — Sync reliability (cross-project collision + TOCTOU + N+1): **shipped** to main.
- **Spec 3 (this doc) — Security posture:** rate-limiting breadth, HSTS, a tenant-readable-error leak fix, and the stuck-run reaper Spec 2 surfaced.

No new user-facing features. This closes the deployment/security surface and the last operational follow-ups. It adds **no new tenant table** — the FR-B2 gate is untouched.

---

## 1. Why this spec exists (verified findings)

Confirmed against the codebase, not taken from notes:

1. **The sync endpoints are unguarded.** Only the six `AuthController` actions carry `[EnableRateLimiting("auth")]`. `POST /api/projects/{id}/sync` and `/rederive` — expensive, Jira-calling — have no limiter.
2. **The `auth` limiter is a single global bucket.** `Program.cs:77` uses `AddFixedWindowLimiter("auth", …)` with **no partition key**, so its 10/min window is shared across *all* callers: one client (or attacker) exhausts it for everyone — a self-DoS.
3. **No HSTS.** `Program.cs:106` calls `UseHttpsRedirection()`; there is no `UseHsts()`.
4. **`RunRederiveAsync` leaks raw exception text into a tenant-readable field.** `RunProjectAsync` guards its failure path (`SyncOrchestrator.cs:106-123`: `JiraApiException` → curated message; anything else → generic + server-log). `RunRederiveAsync` (`:152`) does `run.Error = ex.Message` **unconditionally**, so a `DbUpdateException`/`JsonException` message (SQL fragments, column names) lands in `SyncRun.Error`, which the tenant can read via the sync-runs endpoint.
5. **A crashed sync permanently blocks its project.** Spec 2's partial unique index `IX_SyncRuns_OneRunningPerProject` allows one `Running` run per project, and the retention purge never deletes `Running` rows — so a process crash mid-sync leaves a `Running` row that blocks the project forever.

**Confirmed safe (no action):** `ConnectionResponse` is `(Id, BaseUrl, Email, CreatedAtUtc)` — no token; no credential appears in any log statement; the Jira token is decrypted in memory only (`ITokenProtector.Unprotect`) and never serialized. The token-handling audit's only defect is finding #4, which is an *infra-detail* leak, not a token leak (rederive is Jira-free).

## 2. Decisions locked in brainstorming

- **Sync limiter:** a new policy partitioned by the `endUserId` claim, low limit — targets the real cost driver (a tenant hammering its own sync) and can't be exhausted by one user for another.
- **Auth limiter:** partition by client IP, keeping the 10/min window.
- **HSTS:** add `UseHsts` in production, gated by a config flag (default on) so reverse-proxy deployments can disable it.
- **The reaper is in scope** for this spec.
- **Explicitly deferred** (non-goals): the SR2 concurrency tests not exercising `TryStartRunAsync`'s EF catch through a genuine lost race (a deterministic race test is exactly the flaky kind removed during Spec 2's plan review; the catch is correct by inspection and the DB guarantee is proven by the raw-SQL test); `AdminUsersController`'s 401-on-malformed-`sub` being untested (unreachable behind the `Admin` policy — a test would exercise a branch that cannot occur). Neither is worth building. Also still deferred: the sprint take-last heuristic, the empty-embedded-changelog edge, `RemoveRange → ExecuteDelete`.

## 3. Rate limiting

### 3.1 Fix the `auth` limiter (partition by IP)

Replace the global fixed-window registration with a partitioned policy:

```csharp
options.AddPolicy("auth", httpContext =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 10, QueueLimit = 0 }));
```

Each client IP now has its own 10/min bucket. The six `AuthController` `[EnableRateLimiting("auth")]` attributes are unchanged.

### 3.2 Add the `sync` limiter (partition by user)

```csharp
options.AddPolicy("sync", httpContext =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirst("endUserId")?.Value
                      ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 5, QueueLimit = 0 }));
```

Apply `[EnableRateLimiting("sync")]` to `POST /api/projects/{projectId:guid}/sync` and `.../rederive` in `SyncController`. These are `[Authorize]`, so `endUserId` is present; the IP fallback is defensive. **5/min per user** is generous for a deliberate manual action while stopping a tenant from hammering Jira.

### 3.3 The test-harness implication (load-bearing)

`JiraUnpackedApiFactory` re-registers the `auth` policy as a no-op limiter so the ~50 auth-using integration tests aren't throttled. It **must** be extended to no-op the new `sync` policy too, or every sync/rederive integration test would begin returning 429. The plan makes this an explicit step of the same task that adds the `sync` policy.

## 4. HSTS (gated)

Add a `SecurityOptions` with `bool HstsEnabled = true`, bound from `Security` config. In the pipeline:

```csharp
if (!app.Environment.IsDevelopment() && securityOptions.HstsEnabled)
    app.UseHsts();
app.UseHttpsRedirection();   // unchanged
```

Framework-default HSTS (30-day max-age). `UseHttpsRedirection` is left as-is.

**Documented caveat (not built):** a deployment terminating TLS at a reverse proxy that forwards plain HTTP should also configure `UseForwardedHeaders` and may disable redirection; that is out of scope. `Security:HstsEnabled=false` is the escape hatch such deployments need now.

## 5. The `SyncRun.Error` leak fix

Extract `RunProjectAsync`'s failure handling into one private helper on `SyncOrchestrator`:

```csharp
private void SetFailure(SyncRun run, Exception ex, Guid projectId)
{
    run.Status = SyncRunStatus.Failed;
    run.FinishedAt = _clock.UtcNow;
    // JiraApiException messages are curated + token-free; anything else can carry raw infra
    // detail (SQL fragments, column names) and must not reach this tenant-readable field.
    if (ex is JiraApiException)
    {
        run.Error = ex.Message;
    }
    else
    {
        _logger.LogError(ex, "Unexpected failure syncing project {ProjectId}", projectId);
        run.Error = "Sync failed unexpectedly.";
    }
}
```

Call it from **both** `RunProjectAsync` and `RunRederiveAsync` catch blocks (RunProjectAsync additionally sets `NextRunAt` after, as today). This makes rederive consistent with sync. `SyncOrchestrator` already injects `ILogger<SyncOrchestrator>`.

## 6. The stuck-run reaper

A maintenance job `StuckRunReaperJob` (mirroring `SyncRunRetentionJob`) that marks runs where `Status == SyncRunStatus.Running && StartedAt < now − timeout` as `Failed`, with `Error = "Marked failed: exceeded maximum runtime (stuck-run reaper)."` and `FinishedAt = now`. Marking it `Failed` drops it out of the partial index's `WHERE Status = 'Running'` filter, unblocking the project.

- Config: `Sync:StuckRunTimeoutMinutes` (default **120**). A normal sync is seconds-to-minutes, so 2h is comfortably above a slow-but-live run.
- Isolation posture (same as retention): enumerate `(JiraProjectId, EndUserId)` privileged via `IPrivilegedDbContextFactory` (metadata only), then update within `ITenantScope.BeginScope(endUserId)` per tenant — a privileged *reader*, **no privileged writer**. `BeginScope` before the DI scope, as in `SyncTickJob`/`SyncRunRetentionJob`.
- Schedule: hourly, in the same `Sync:SchedulerEnabled` Quartz block (disabled in tests by that existing toggle).
- **A recent `Running` run must never be reaped** — the timeout guard is what distinguishes a stuck run from an in-flight one.

## 7. Testing

- **Rate limiting:** the broad suite keeps the factory's no-op for both `auth` and `sync`. One dedicated test class uses its **own** `WebApplicationFactory` subclass that does *not* override the limiters, fires `PermitLimit + 1` rapid requests at a sync endpoint (and at an auth endpoint), and asserts a **429** appears. The brittleness of fixed-window assertions is isolated to that one class.
- **The error-leak fix:** drive `/rederive` over a deliberately malformed raw snapshot so derivation throws a non-`JiraApiException`; assert `SyncRun.Error == "Sync failed unexpectedly."`, not the raw parser message. (A regression test that would have failed before the fix.)
- **The reaper:** seed an old `Running` run → run the job → assert it is `Failed` and a fresh sync can start; seed a **recent** `Running` run → run the job → assert it is left `Running` (not reaped).
- **HSTS is *not* integration-tested**, deliberately: `WebApplicationFactory`'s `TestServer` serves plain HTTP, and `UseHsts` emits the `Strict-Transport-Security` header only on HTTPS responses, so a header assertion could never pass — testing it would prove nothing. Instead, the gating **decision** is a small pure predicate (`ShouldEnableHsts(isDevelopment, HstsEnabled)`) with a unit test covering its four cases; the wiring (`if (predicate) app.UseHsts()`) is verified by review. This keeps a genuine test on the one piece with real logic and avoids a green test that asserts nothing.
- No migration; no schema change; the FR-B2 gate untouched; full suite green; Release build clean.

## 8. Success criteria

- `POST /sync` and `/rederive` are rate-limited per user; the auth limiter is per-IP, not a shared global bucket.
- HSTS is emitted in production by default and can be disabled by config for reverse-proxy deployments.
- No sync path — including rederive — writes raw infrastructure detail into the tenant-readable `SyncRun.Error`.
- A crashed sync no longer blocks its project permanently: the reaper clears stuck `Running` runs after a timeout, without touching in-flight ones.
- No new tenant table; the FR-B2 gate unchanged and green; no migration; Release clean.

## 9. Notes / follow-ups

- This spec **closes Phase 7 and the v1 build.** Everything in REQUIREMENTS §1–§4 that was in scope for v1 is then shipped; FR-E4 (the generic filter builder) remains the explicit post-v1 item.
- Deferred, unchanged: the SR2 EF-catch race-path test, the unreachable 401-`sub` test, the sprint take-last heuristic, the empty-embedded-changelog edge, `RemoveRange → ExecuteDelete`, and full reverse-proxy forwarded-headers support.
