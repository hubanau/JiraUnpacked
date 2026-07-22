# Phase 7 — Hardening: Tenant data lifecycle (GDPR erasure + retention) design

**Status:** Approved (2026-07-22)
**Epic:** Phase 7 (Hardening)
**This spec:** Spec **1 of 3** of Phase 7.

- **Spec 1 (this doc) — Tenant data lifecycle:** account deletion that erases all tenant data, plus a defined retention policy.
- Spec 2 — Sync reliability: the `AlreadyRunning` TOCTOU (needs a partial unique index), per-issue upsert N+1, per-snapshot `SaveChanges` round-trips, the empty-embedded-changelog edge, the sprint take-last heuristic.
- Spec 3 — Security posture: rate-limiting breadth beyond auth, HSTS/HTTPS-redirect posture, a token-handling audit.

Closes the REQUIREMENTS §4 privacy obligation: *"Hard delete of all tenant data on account removal"* and *"define retention from day one."* No new user-facing reporting features; FR-E4 remains deferred.

---

## 1. Why this spec exists (verified findings)

Two facts, confirmed against the codebase rather than assumed:

1. **There is no account-deletion path at all.** `AdminUsersController` exposes only `block` and `unblock`.
2. **No tenant-owned table references `Users`.** All **16** `ITenantOwned` entities carry `EndUserId` as a plain indexed `Guid` with no foreign key (e.g. `JiraConnectionConfiguration.cs:18` has `HasIndex`, no `HasOne<User>`). The only `HasOne<User>` relationships are identity-side: `RefreshToken`, `EmailVerification`, `InviteCode`.

Consequently a `DELETE FROM "Users"` would succeed and silently orphan every connection, project, issue, raw snapshot, changelog, change-history row, sync run, dictionary, semantic and mapping. The obligation is unmet in the strongest sense: not incorrectly implemented, but absent.

**A third finding blocks any naive implementation.** `InviteCodeConfiguration` restricts deletion on the redeemer link:

```csharp
// Redeemer (nullable) — restrict delete; auditing keeps the link.
builder.HasOne<User>().WithMany()
    .HasForeignKey(i => i.UsedByUserId)
    .OnDelete(DeleteBehavior.Restrict);
```

Because invite codes are how End Users register, this makes essentially **every End User undeletable** — the FK throws before any cascade could run.

## 2. Decisions locked in brainstorming

- **Both self-service and admin deletion.** Erasure is normally the data subject's own right; the admin path covers operator offboarding.
- **Immediate and irreversible.** No pending-deletion state, no scheduled purge, no recovery window.
- **Retention:** documented policy; the only automated purge is `SyncRun` history. Raw stores are explicitly **never** time-purged.
- **A minimal tombstone** survives deletion as proof the erasure happened.
- **Database-enforced cascade, guarded by a reflection-driven completeness gate.** The schema guarantees completeness; the gate keeps it true as tables are added. An explicit hand-maintained deletion list was rejected — a forgotten table is exactly the failure this project has already shipped twice past a green suite.

## 3. Mechanism

### 3.1 Resolving the invite-code conflict

`UsedByUserId` changes from `Restrict` to **`SetNull`** (the column is already nullable). The invite code survives as proof it was issued and redeemed; the link to the erased person is severed.

This deliberately overrides the existing comment's intent ("auditing keeps the link"): retaining the audit *fact* while dropping the personal *identifier* is the GDPR-correct reading, and an audit trail cannot outrank an erasure obligation.

`CreatedByAdminId` remains `Restrict` — admins are not deletable through this path (§3.3).

### 3.2 Cascade wiring, applied centrally

`AppDbContext.OnModelCreating` iterates the model's entity types implementing `ITenantOwned` and configures, for each:

```csharp
modelBuilder.Entity(clrType)
    .HasOne(typeof(User))
    .WithMany()
    .HasForeignKey(nameof(ITenantOwned.EndUserId))
    .OnDelete(DeleteBehavior.Cascade);
```

A future 17th tenant-owned table therefore gets deletion wired **automatically**. The existing explicit per-entity `HasQueryFilter` lines are unchanged — this complements Layer 1, it does not replace it.

Migration `AddTenantCascadeToUsers` adds the 16 foreign keys. If orphaned rows already exist, Postgres refuses to create the constraint and the migration **fails loudly** — that is correct behaviour and must not be worked around by deleting the offending rows silently.

### 3.3 The deletion service

```csharp
public enum AccountDeletionOutcome { Deleted, NotFound, NotAnEndUser }

public sealed record AccountDeletionResult(AccountDeletionOutcome Outcome);

public interface IAccountDeletionService
{
    Task<AccountDeletionResult> DeleteAsync(Guid userId, Guid requestedBy, CancellationToken ct = default);
}
```

Behaviour, all inside **one transaction**:
1. Load the user. Missing → `NotFound`.
2. Role is not `EndUser` → `NotAnEndUser`. Admin accounts are config-seeded and the startup seeder would simply recreate them, so deleting one is meaningless.
3. Open **`ITenantScope.BeginScope(userId)`** and delete the `User` row. The cascade removes all 16 tables' rows; `RefreshToken` and `EmailVerification` cascade via their existing FKs; `InviteCode.UsedByUserId` is nulled by §3.1.
4. Write the tombstone (§4.2).

**Ordering constraint — `BeginScope` must precede the first database access in the transaction.** `TenantConnectionInterceptor` sets the `app.current_tenant` GUC when a connection is *opened*, so a scope entered after the connection is already open leaves the GUC naming the wrong tenant (or none), and RLS then silently denies the very rows being erased. The scope is therefore opened before the transaction begins. This is the same ordering hazard that had to be verified in the Quartz tick job (`BeginScope` before the DI scope), and it gets the same explicit treatment.

`User` and `DeletedAccount` are not `ITenantOwned`, so neither the tenant query filter nor `StampTenant()` applies to them; the scope exists solely to satisfy RLS on the cascaded tenant tables.

**Why `BeginScope(userId)` is load-bearing:** every tenant table has `FORCE ROW LEVEL SECURITY` and the app connects as `jiraunpacked_app` (`NOBYPASSRLS`). Scoping to the target tenant sets the `app.current_tenant` GUC to the account being erased, so both the EF filter and the RLS policy permit exactly those rows — including when an **Admin** deletes another user's account, where the admin's own tenant context would otherwise deny everything.

Whether Postgres applies RLS to rows removed by a referential cascade, and how `FORCE` interacts with the RI triggers, is subtle. This spec does not rely on reasoning about it: scoping to the target tenant is correct under either behaviour, and the completeness gate (§6) proves the outcome empirically against real Postgres with RLS forced. That matters because the failure mode here is *silently leaving personal data behind while reporting success*.

## 4. API surface

### 4.1 Endpoints

- **`DELETE /api/auth/me`** — self-service, `[Authorize]`, `[EnableRateLimiting("auth")]`. Deletes the caller's own account, id taken from the `endUserId` claim. Chosen for symmetry with the existing `GET /api/auth/me`, which already exposes the account resource.
- **`DELETE /api/admin/users/{id:guid}`** — `[Authorize(Policy = "Admin")]`, alongside the existing `block`/`unblock`.

Responses: **204** on success; **404** for an unknown id; **409** when the target is not an `EndUser`. Both routes delegate to `IAccountDeletionService`.

### 4.2 The tombstone

```csharp
public sealed class DeletedAccount
{
    public Guid Id { get; set; }
    public Guid DeletedUserId { get; set; }  // opaque; the user row is gone
    public Guid RequestedBy { get; set; }    // equals DeletedUserId for self-service
    public DateTime DeletedAt { get; set; }
}
```

It stores **no email, no username, no display name** — only a pseudonymous id, who requested the deletion, and when. Neither `Guid` carries a foreign key, since both may reference rows that no longer exist.

**`DeletedAccount` is deliberately not `ITenantOwned`.** It must outlive the tenant, so it has no `EndUserId`, no query filter and no RLS policy — which means **this spec adds a table without triggering the FR-B2 gate-extension rule**, and the gate test is unaffected. The central cascade loop of §3.2 skips it for the same reason.

No read endpoint in v1 (the row is queryable by an operator); add `GET /api/admin/deleted-accounts` when demonstrating compliance actually requires it.

### 4.3 The access-token caveat

Deletion severs the refresh chain immediately — refresh tokens cascade away — but the caller's **existing access token remains cryptographically valid until it expires**.

In practice the blast radius is nil: all tenant data is gone, so reads return empty or 404 and there is nothing to write to. One real hazard remains: any endpoint resolving the current user from the database, notably `GET /api/auth/me`, finds **no user row** and must degrade to **401**, never a 500. This gets an explicit test (§6).

No token revocation list in v1. A severed refresh chain plus a short access-token lifetime is proportionate for a self-hosted tool; a revocation store is real infrastructure for a nil-impact window.

## 5. Retention

### 5.1 The policy

- **Raw stores** (`RawIssueSnapshots`, `RawChangelog`) — retained **indefinitely; never time-purged.** They are the source for re-derivation; ageing them out would silently destroy the ability to re-derive history, which is the property Module E Specs 2 and 3 depend on ("change a mapping, results change, with no Jira re-pull").
- **Modeled data** (`Issues`, `IssueChangeHistory`, dictionaries, semantics, mappings) — retained indefinitely; it is the product.
- **`SyncRun` history** — trimmed on a timer. Operational telemetry with unbounded growth and no analytical value beyond recent runs.
- **All tenant data** — erased immediately on account deletion (§3).

### 5.2 The purge, and the hazard it creates

The incremental watermark is `Max(StartedAt)` over runs where `Status == Succeeded && Kind == SyncRunKind.Sync`. If retention deleted all of a project's runs, the next sync would lose its watermark and re-pull from the beginning.

The purge therefore has two hard exclusions:

1. **Never delete the newest `Succeeded` + `Kind == Sync` run per project** — the watermark's anchor.
2. **Never delete a run whose `Status` is `Running`**, at any age — that would break the `AlreadyRunning` guard and orphan an in-flight sync.

Configuration: `Retention:Enabled` (default `true`) and `Retention:SyncRunDays` (default `90`). Execution: a daily Quartz maintenance job, disabled in tests alongside the existing scheduler toggle.

### 5.3 Isolation posture for the maintenance job

A maintenance sweep has no tenant context, so it needs privileged access — and unlike `DueProjectFinder`, deletion *writes*.

The job therefore **enumerates** `(JiraProjectId, EndUserId)` pairs through `IPrivilegedDbContextFactory` — scheduling metadata only, no issue content, exactly the existing sanctioned `DueProjectFinder` pattern — then **deletes within `ITenantScope.BeginScope(endUserId)` per tenant**, applying the §5.2 exclusions inside that scope. The result is that this spec adds a privileged *reader* and **no privileged writer**: every write stays under RLS. The extra loop is worth it given how much of this system's design rests on that rule.

## 6. Testing

**The deletion-completeness gate — the centrepiece.** Mirroring the FR-B2 isolation gate: reflect over every `ITenantOwned` implementor in the domain assembly, seed at least one row in each for tenant A, delete A's account, then assert **zero rows remain for A in every one of those tables** (read via the privileged context with `IgnoreQueryFilters` — test-only, and legitimate for a cross-tenant assertion). Adding a 17th tenant-owned table without cascade wiring fails CI. This is what keeps the design true after the fact.

**The over-delete guard.** Seed tenants A **and** B with rows in every tenant table, delete A, and assert **B's row counts are unchanged**. Completeness and containment are separate failure modes; a cascade that over-reaches is as serious as one that under-reaches.

Plus:
- Self-service deletion returns 204; the user can no longer log in, and refresh fails.
- Admin deletion returns 204; unknown id → 404; a non-`EndUser` target → 409.
- **`GET /api/auth/me` with a still-valid access token after deletion → 401, not 500.**
- A user who redeemed an invite code **is deletable**, and the code row survives with `UsedByUserId` null.
- The tombstone is written with the correct ids and contains no personal data.
- **Retention:** runs older than the cutoff are purged, the newest `Succeeded`+`Sync` run per project **survives**, a subsequent sync still computes the correct watermark, and `Running` runs are never purged.

## 7. Success criteria

- An End User deletes their own account, or an Admin deletes theirs, and **every row of that tenant's data is gone** — proven by a reflection-driven gate that cannot silently miss a table.
- No other tenant's data is touched.
- The FR-B2 isolation gate is unchanged and green; `DeletedAccount` is not tenant-owned, so no RLS policy or gate extension is required.
- Retention is documented, `SyncRun` history is trimmed on a timer, and the sync watermark survives the purge.
- One migration for the cascade, one for the tombstone; no drift; Release build clean.

## 8. Notes / follow-ups

- Spec 2 (sync reliability) will add a **partial unique index** on `SyncRuns` to close the `AlreadyRunning` TOCTOU. That index and this spec's retention exclusions both concern the same table — Spec 2 should re-check the purge exclusions still hold once the index exists.
- If demonstrating compliance later needs an operator view, add `GET /api/admin/deleted-accounts` over the tombstone.
- Automated purging of raw stores was considered and **rejected**: it would break re-derivation, which is a core capability rather than an optimisation.
