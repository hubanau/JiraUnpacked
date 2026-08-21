# Backend follow-ups: the API gaps the Web UI exposed

**Status:** Approved (2026-08-19)
**Epic:** Post-UI backend follow-ups.
**This spec:** the three gaps the Web UI epic surfaced that are cheap to close and that let the client **delete** workarounds rather than accumulate them.

The Web UI epic is complete and released (`main` @ `be9495c`). Building it against a finished API surfaced three places where the API forced the client into a workaround. Each is small on its own; what makes them worth one spec is that every fix **retires client debt**, and two of them fix latent client bugs in the process.

---

## 1. Scope & non-goals

**In scope — three gaps, each with the client change it enables:**

| # | Gap | Client debt it retires |
|---|---|---|
| **BF1** | A rejected *Jira* credential returns **401**, indistinguishable from our own session expiring | Deletes the SPA's 401 body-discrimination — and fixes two latent bugs it introduced |
| **BF2** | No endpoint exposes the filter-field catalog | Deletes Spec 3's hand-maintained client mirror and its drift test |
| **BF3** | No endpoint lists users | Replaces the admin panel's "type a user id" input with a real list |

**Out of scope — each is its own future spec, none blocking:**

- **Saved/named filters** — a new tenant-owned table, so both isolation layers plus an FR-B2 gate extension.
- **Cross-field OR / nested filter groups** — an FR-E4 translator change.
- **Filtering metrics and gap-checks** — a semantic question (what does "throughput of a filtered subset" mean?), not a plumbing one.
- **httpOnly-cookie auth** — replaces the SPA's `localStorage` refresh token; security-sensitive, needs CSRF, and touches the whole auth flow.

## 2. Decisions locked in

- **Every fix must retire client debt, not just add an endpoint.** An endpoint nobody adopts is worse than the workaround it was meant to replace, because now both exist.
- **The FR-B2 tenant-isolation gate is untouched.** No new tenant-owned table; `Users` already has no query filter (§4).
- **No new dependency, on either side.**

## 3. BF1 — a rejected Jira credential must not look like an expired session

`JiraApiExceptionFilter` maps `JiraErrorKind.Unauthorized` → **401**. That kind is produced in exactly one place (`JiraApiClient.Discovery.cs:78`, "Jira rejected the credentials.") and reaches the client from live-Jira reads such as `GET /connections/{id}/fields` and `GET /projects/{id}/jira-statuses`.

The SPA cannot distinguish that from its own JWT expiring, so a user whose **Jira** token was revoked got signed out of **Jira Unpacked** on every project page. Spec 2 shipped a front-end workaround: after a successful refresh, a 401 carrying a ProblemDetails body is treated as *not* session expiry.

**The fix:** map `Unauthorized` → **502**, keeping its distinct title (*"Jira rejected the stored credentials."*). 401 then means only one thing again: our session.

**Two consequences that make this more than a status change.**

**(a) The client's 502 handling must stop discarding the server's wording.** Spec 1's `toApiError` maps 502 to a fixed `UPSTREAM_JIRA_MESSAGE` **by status alone, before reading the body** — so a 502 carrying "Jira rejected the stored credentials." would render as "Jira did not respond as expected", which is wrong and unactionable. 502 must prefer a ProblemDetails `title` when one is present, falling back to the generic message otherwise. This keeps Spec 2's genuinely valuable distinction — *your token is wrong* vs *Jira is down* — which is why the connection-create screen already separates 400 from 502.

**(b) Deleting the workaround fixes two latent bugs.** The remaining 401-with-body responses on authenticated paths are `AdminUsersController` (invalid `sub` claim) and `AuthController.DeleteMe` — both *"Invalid token."*, both genuine session failures that **should** log the user out. The current discriminator sees a body and does **not**. Once Jira is 502, the correct rule is simple again: **any 401 after a successful refresh is a session failure**, and the discriminator is deleted rather than adjusted.

No existing test asserts 401 for a Jira credential failure, so the blast radius is one filter line plus new coverage.

## 4. BF3 — listing users, and the isolation subtlety

`Users` deliberately has **no global query filter**: it is the tenant *root*, and an Admin is a platform operator acting across tenants by design. `UserAdminService.BlockAsync` already reads `_db.Users` unfiltered for exactly this reason.

**So the `Admin` policy is the only thing standing between an End User and every account on the platform.** That is the existing, correct design — but it means the new endpoint must be tested for it explicitly rather than assumed: **a non-admin receives 403**, and that test is as important as the happy path.

`GET /api/admin/users` returns a paged list projecting only `{ id, email, username, role, status, createdAt }`. **`PasswordHash` is never projected** — the entity carries it, and a naive `Select` of the whole entity would serialise it. Offset paging, default 50 / max 100, ordered by `CreatedAt DESC` then `Id`, matching every other paged read in this codebase.

Admins appear in the list alongside End Users; block/unblock already reject non-End-Users with a typed error, so the UI can show that state rather than pretending admins are absent.

## 5. BF2 — the filter catalog

`FilterFields.All` is `internal` to Infrastructure and no endpoint exposes it, so Spec 3 hand-maintained a client copy plus a test asserting the matrix, so drift would fail loudly. That test has worked — but the duplication is the actual defect.

`GET /api/filter-catalog` returns the field matrix: `[{ name, type, operators[] }]` plus the two guard limits (`maxConditions`, `maxInValues`). It is **static, tenant-independent data** — the same for every caller — so it needs no project scope and no tenant read.

**`FilterFields` stays `internal`.** A new `IFilterCatalogService` in `Domain.Abstractions` returns public DTOs, implemented in Infrastructure by projecting the internal dictionary. Making the internal types public to serve a controller would leak an implementation detail into the API contract.

The client then **fetches** the catalog and **deletes** `filterCatalog.ts` and its drift test. Drift becomes impossible rather than merely detected. The cost is that the filter builder needs the catalog loaded before it can render its selectors — acceptable, and it renders a spinner exactly as every other query-backed screen does.

## 6. Testing

- **BF1:** a Jira `Unauthorized` now yields **502** with its distinct title (the one-line filter change, covered); the client renders a 502's ProblemDetails `title` when present and the generic upstream message when absent; and — the load-bearing one — **a 401 after a successful refresh logs the user out even when it carries a ProblemDetails body**, which is the latent bug being fixed and must fail against the current code.
- **BF2:** the endpoint returns all twelve fields with their exact operator sets and both guards; the client renders the builder from fetched data; `filterCatalog.ts` and its test are **gone** (a test asserting a deleted module cannot pass, so this is verified by the suite still being green with the file removed).
- **BF3:** the happy path pages correctly; **a non-admin gets 403**; `passwordHash` appears nowhere in the response body (asserted on the raw JSON, not the typed DTO — a typed assertion cannot catch an extra serialised property).
- The FR-B2 isolation gate stays untouched and green; no new migration; both suites green.

## 7. Success criteria

- A revoked Jira token surfaces as an actionable Jira problem and **never** signs the user out; a genuinely invalid session token **does**.
- The filter builder's field list comes from the server, and the client mirror no longer exists.
- An admin sees and acts on a real user list; a non-admin gets 403.
- No new tenant table, no migration, no new dependency, FR-B2 gate untouched, both suites green.

## 8. Notes / follow-ups

Remaining, each its own spec: saved/named filters; cross-field OR and nested groups; filtering metrics and gap-checks; httpOnly-cookie auth.

One test-only item is being fixed in a separate session: `api/issues.test.ts` asserts a parsed URL pathname with a space fixture, which the WHATWG parser percent-encodes anyway, so the test cannot fail if `encodeURIComponent` were removed. The code is correct; only the test is inert.
