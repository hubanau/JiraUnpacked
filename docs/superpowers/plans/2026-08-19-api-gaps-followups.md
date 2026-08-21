# Backend Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three API gaps the Web UI epic exposed, and in each case **delete** the client workaround the gap forced.

**Architecture:** Three vertical slices. Each task changes the API *and* the client that consumes it, so no task can leave an endpoint nobody adopts. Two of the three end by deleting client code.

**Tech Stack:** ASP.NET Core 8 · EF Core 8 + Npgsql · xUnit · React 19 · TypeScript strict · Vitest + RTL + MSW.

## Global Constraints

- **No new tenant-owned table, no migration.** The FR-B2 gate (`tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs`) stays **untouched and green**.
- **No new dependency**, backend or frontend. Frontend runtime deps stay exactly `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`.
- **`FilterFields` stays `internal`.** Expose the catalog through a service returning public DTOs; making the internal types public would leak an Infrastructure detail into the API contract.
- **`PasswordHash` is never projected, serialised, or logged.**
- **No token, password, or invite code logged.**
- Frontend: `src/ui/` imports neither `src/api/` nor `src/auth/`; all query keys from `src/api/queryKeys.ts`; every hook above every conditional `return`.
- Tests target logic. **No snapshot tests. No test may pass if the behaviour it names is broken.**
- Each task ends with **both** suites green. **Do NOT run `dotnet test` in a subagent** — the controller runs it (agents have been killed by the harness watchdog waiting on it). Report the web suite; the controller reports .NET.
- Every task ends with a commit. Never push, open a PR, or merge.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Api/Filters/JiraApiExceptionFilter.cs` | *(modify)* map `Unauthorized` → 502 |
| `src/JiraUnpacked.Web/src/api/errors.ts` | *(modify)* 502 prefers a ProblemDetails title |
| `src/JiraUnpacked.Web/src/api/client.ts` | *(modify)* **delete** the 401 body-discriminator |
| `src/JiraUnpacked.Domain/Abstractions/IFilterCatalogService.cs` | Catalog contract + public DTOs |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs` | Projects the internal catalog |
| `src/JiraUnpacked.Api/Controllers/FilterCatalogController.cs` | `GET /api/filter-catalog` |
| `src/JiraUnpacked.Web/src/features/issues/filterCatalog.ts` | *(DELETE)* replaced by a fetch |
| `src/JiraUnpacked.Domain/Abstractions/IUserAdminService.cs` | *(modify)* add `ListAsync` + list DTO |
| `src/JiraUnpacked.Api/Controllers/AdminUsersController.cs` | *(modify)* `GET /api/admin/users` |
| `src/JiraUnpacked.Web/src/features/admin/AdminPage.tsx` | *(modify)* real user list |

---

## Task 1 (BF1): A rejected Jira credential must not end the session

**Files:**
- Modify: `src/JiraUnpacked.Api/Filters/JiraApiExceptionFilter.cs`, `src/JiraUnpacked.Web/src/api/errors.ts`, `src/JiraUnpacked.Web/src/api/client.ts`
- Test: `tests/JiraUnpacked.Tests/Integration/JiraCredentialFailureTests.cs` (new), `src/JiraUnpacked.Web/src/api/errors.test.ts`, `client.test.ts`

**Interfaces:**
- Produces: 502 (not 401) for `JiraErrorKind.Unauthorized`; `toApiError` preferring a 502's `title`; a simplified `apiFetch` 401 path.

- [ ] **Step 1: Change the filter**

`JiraApiExceptionFilter.cs` currently does `var status = ex.Kind == JiraErrorKind.Unauthorized ? 401 : 502;`. Replace the whole `OnException` body's status logic so **every** `JiraApiException` is **502**, keeping the two distinct titles:

```csharp
if (context.Exception is not JiraApiException ex) return;

// Every upstream Jira failure is 502 — including a rejected credential.
// It used to be 401, which the SPA could not tell apart from its own JWT
// expiring, so a revoked *Jira* token signed the user out of the app.
// 401 now means exactly one thing: our session. The title still separates
// "your stored token is wrong" from "Jira is unreachable".
const int status = StatusCodes.Status502BadGateway;
context.Result = new ObjectResult(new ProblemDetails
{
    Status = status,
    Title = ex.Kind == JiraErrorKind.Unauthorized
        ? "Jira rejected the stored credentials."
        : "Jira could not be reached."
}) { StatusCode = status };
context.ExceptionHandled = true;
```

- [ ] **Step 2: Write the API test**

`tests/JiraUnpacked.Tests/Integration/JiraCredentialFailureTests.cs`. Follow the existing integration style — look at `tests/JiraUnpacked.Tests/Integration/SpaHostingTests.cs` for `JiraUnpackedApiFactory` usage, and find how other tests stub `IJiraApiClient` (grep for `IJiraApiClient` under `tests/`). Drive an endpoint that reads live Jira (`GET /api/connections/{id}/fields`) with a stub throwing `new JiraApiException(JiraErrorKind.Unauthorized, "…")`, and assert:

```csharp
Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
var body = await response.Content.ReadAsStringAsync();
Assert.Contains("rejected the stored credentials", body);
```

Also assert an `Upstream`-kind exception still yields 502 with the *unreachable* title, so the two are not collapsed.

If stubbing `IJiraApiClient` through the factory proves impractical, say so in your report and instead unit-test `JiraApiExceptionFilter.OnException` directly with a fabricated `ExceptionContext` — the filter is the whole behaviour under test.

- [ ] **Step 3: Make the client stop discarding a 502's title**

In `src/api/errors.ts`, `toApiError` currently returns `UPSTREAM_JIRA_MESSAGE` for 502 **by status, before parsing the body**. Change it so 502 prefers a ProblemDetails `title` when present:

```ts
if (response.status === 429) return new ApiError(429, RATE_LIMIT_MESSAGE);

const body = await readJson(response);

// 502 now carries two distinct causes — a rejected stored credential and an
// unreachable Jira — so the server's own wording must win when it sent one.
// Falling back to the generic message keeps behaviour for bodiless 502s.
if (response.status === 502) {
  const title = titleOf(body);
  return new ApiError(502, title ?? UPSTREAM_JIRA_MESSAGE);
}
```
Keep 429 decided by status alone (the rate limiter's own body is framework wording, not ours). Extract a small `titleOf(body)` helper if it makes the existing ProblemDetails/`{error}` parsing cleaner; do not change that parsing.

- [ ] **Step 4: Delete the 401 discriminator**

In `src/api/client.ts`, the post-refresh 401 branch reads the body and calls `onAuthFailure()` only when there is no ProblemDetails `title`. **Delete that discrimination**, restoring:

```ts
const retry = await send(path, options);
if (retry.status === 401) {
  // A 401 after a successful refresh cannot be a stale bearer token — the
  // retry carried a fresh one. Since Jira credential failures are now 502,
  // every remaining 401 here is a genuine session failure (an invalid `sub`
  // claim, a revoked account), so ending the session is correct.
  bridge.onAuthFailure();
  throw await toApiError(retry);
}
```

**This fixes two latent bugs**, not just removes code: `AdminUsersController`'s and `AuthController.DeleteMe`'s *"Invalid token."* both carry a ProblemDetails body, so the discriminator was refusing to log out on a genuinely invalid token.

- [ ] **Step 5: Write the failing client tests**

In `errors.test.ts`:
```ts
it('prefers a 502 ProblemDetails title over the generic upstream message', async () => {
  const e = await toApiError(res(502, { title: 'Jira rejected the stored credentials.' }));
  expect(e.title).toBe('Jira rejected the stored credentials.');
});

it('falls back to the generic upstream message for a bodiless 502', async () => {
  const e = await toApiError(res(502));
  expect(e.title).toBe(UPSTREAM_JIRA_MESSAGE);
});
```

In `client.test.ts` — **the load-bearing one**, which must fail against the current code:
```ts
it('logs out on a post-refresh 401 even when it carries a ProblemDetails body', async () => {
  server.use(http.get('/api/thing', () =>
    HttpResponse.json({ title: 'Invalid token.' }, { status: 401 })));
  const onAuthFailure = vi.fn();
  setAuthBridge({ getAccessToken: () => 'x', refresh: async () => true, onAuthFailure });

  await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);

  // The old discriminator saw the body and skipped this — a genuinely
  // invalid token then left the user in a broken half-session.
  expect(onAuthFailure).toHaveBeenCalledTimes(1);
});
```
Keep every existing `client.test.ts` assertion, especially the single-flight/`refresh`-call-count ones.

- [ ] **Step 6: Run and commit**

From `src/JiraUnpacked.Web/`: `npm run test && npm run typecheck && npm run lint`. Report the count. **Do not run `dotnet test`.**

```bash
git add src/JiraUnpacked.Api/Filters/JiraApiExceptionFilter.cs tests/JiraUnpacked.Tests/Integration/JiraCredentialFailureTests.cs src/JiraUnpacked.Web/src/api
git commit -m "fix: return 502 for a rejected Jira credential, retire the client 401 workaround"
```

---

## Task 2 (BF2): Serve the filter catalog, delete the client mirror

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IFilterCatalogService.cs`, `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs`, `src/JiraUnpacked.Api/Controllers/FilterCatalogController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` (expose the guards), `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`, `src/JiraUnpacked.Web/src/api/issues.ts`, `queryKeys.ts`, `src/features/issues/FilterBuilder.tsx`, `ConditionRow.tsx`
- **Delete:** `src/JiraUnpacked.Web/src/features/issues/filterCatalog.ts` and `filterCatalog.test.ts`
- Test: `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs` (new), updated `FilterBuilder.test.tsx`

**Interfaces:**
- Produces: `GET /api/filter-catalog` → `{ fields: [{ name, type, operators[] }], maxConditions, maxInValues }`; client `getFilterCatalog()` and `queryKeys.filterCatalog()`.

- [ ] **Step 1: Expose the guard limits**

In `IssueFilterTranslator.cs`, change `private const int MaxConditions = 20;` and `private const int MaxInValues = 100;` to **`public const`**. They are already the authority for validation; the catalog must report the same numbers rather than restate them.

- [ ] **Step 2: Write the contract**

`src/JiraUnpacked.Domain/Abstractions/IFilterCatalogService.cs`:
```csharp
namespace JiraUnpacked.Domain.Abstractions;

public sealed record FilterFieldInfo(string Name, string Type, IReadOnlyList<string> Operators);

public sealed record FilterCatalogView(
    IReadOnlyList<FilterFieldInfo> Fields, int MaxConditions, int MaxInValues);

/// <summary>
/// The closed filter-field allow-list, exposed so clients need not mirror it.
/// Static and tenant-independent — identical for every caller.
/// </summary>
public interface IFilterCatalogService
{
    FilterCatalogView Get();
}
```

- [ ] **Step 3: Implement it over the internal dictionary**

`src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs`:
```csharp
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// Projects the internal FilterFields.All into public DTOs. FilterFields stays
/// internal on purpose: it holds compiled selector lambdas, an Infrastructure
/// detail that has no business in the API contract.
/// </summary>
public sealed class FilterCatalogService : IFilterCatalogService
{
    public FilterCatalogView Get() => new(
        FilterFields.All
            .Select(kvp => new FilterFieldInfo(
                kvp.Value.Name,
                kvp.Value.Type.ToString(),
                kvp.Value.Ops.Select(o => o.ToString()).ToList()))
            .ToList(),
        IssueFilterTranslator.MaxConditions,
        IssueFilterTranslator.MaxInValues);
}
```

**Ordering matters for a stable contract:** `FilterFields.All` is a `Dictionary`, whose enumeration order is not guaranteed by the language. Project in the dictionary's declaration order if that is stable in practice, or sort by `Name` — pick one, and say which in your report, so the response is deterministic.

Register it in `DependencyInjection.cs` beside the existing `IIssueFilterTranslator` registration (`AddSingleton` is appropriate — it is stateless and static).

- [ ] **Step 4: Add the controller**

`src/JiraUnpacked.Api/Controllers/FilterCatalogController.cs` — `[Authorize]` (not admin-only; every signed-in user builds filters), `[HttpGet("api/filter-catalog")]`, returning `Ok(_catalog.Get())`. No project id: the catalog is not tenant-scoped.

- [ ] **Step 5: Write the API test**

`FilterCatalogEndpointTests.cs`: an authenticated GET returns 200; the payload contains **all twelve** field names; `status` carries exactly `Eq, Neq, In, IsNull`; `jiraKey` carries exactly `Eq, Contains, StartsWith`; `maxConditions` is 20 and `maxInValues` is 100. Assert the operator sets **exactly**, not by `Contains` — a superset would pass a containment check while offering the client an operator the server rejects.

- [ ] **Step 6: Client — fetch it, delete the mirror**

In `src/api/issues.ts` add the types and wrapper:
```ts
export interface FilterFieldInfo { name: string; type: FieldType; operators: FilterOperator[] }
export interface FilterCatalog {
  fields: FilterFieldInfo[];
  maxConditions: number;
  maxInValues: number;
}
export type FieldType = 'DictName' | 'Decimal' | 'DateTime' | 'Text' | 'ParentRef';

export const getFilterCatalog = () => apiFetch<FilterCatalog>('/api/filter-catalog');
```
Add `filterCatalog: () => ['filter-catalog'] as const` to `queryKeys.ts` (no project segment — it is global).

`FilterBuilder` fetches it with `useQuery` and derives what `filterCatalog.ts` used to provide: the field list, `operatorsFor`, `typeFor`, and the reset-on-disallowed-operator rule. Keep those as small **pure helpers taking the fetched catalog** so they stay unit-testable — the logic was worth isolating; only its hard-coded data is going away. Render a `Spinner` while the catalog loads and an `ErrorState` if it fails, exactly as every other query-backed screen does.

**Then delete `src/features/issues/filterCatalog.ts` and `filterCatalog.test.ts`.** Drift is now impossible rather than merely detected.

- [ ] **Step 7: Update the builder tests**

`FilterBuilder.test.tsx` must now mock `GET /api/filter-catalog`. **Keep every existing assertion** — the exact `conditions` wire shape (`IsNull` sending neither key, `In` sending `values`, single-value ops sending `value`), the guards, the operator narrowing and reset, the `Neq` note, and the server-400-still-renders test. They are the reason this builder is trustworthy.

- [ ] **Step 8: Run and commit**

From `src/JiraUnpacked.Web/`: `npm run test && npm run typecheck && npm run lint`. Report the count (it will **drop** by the deleted mirror's tests — say by how many, so the drop is explained rather than mistaken for lost coverage). **Do not run `dotnet test`.**

```bash
git add -A src/JiraUnpacked.Domain src/JiraUnpacked.Infrastructure src/JiraUnpacked.Api tests/JiraUnpacked.Tests src/JiraUnpacked.Web/src
git commit -m "feat: serve the filter catalog, delete the client-side mirror"
```

---

## Task 3 (BF3): List users, so the admin panel stops asking for a GUID

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/IUserAdminService.cs`, `src/JiraUnpacked.Infrastructure/Services/UserAdminService.cs`, `src/JiraUnpacked.Api/Controllers/AdminUsersController.cs`, `src/JiraUnpacked.Web/src/api/admin.ts`, `queryKeys.ts`, `src/features/admin/AdminPage.tsx`
- Test: `tests/JiraUnpacked.Tests/Admin/AdminUserListTests.cs` (new), `src/JiraUnpacked.Web/src/features/admin/AdminPage.test.tsx`

**Interfaces:**
- Produces: `GET /api/admin/users?page=&pageSize=` → `PagedResult<AdminUserListItem>`; client `listUsers(page, pageSize)` and `queryKeys.adminUsers(page)`.

- [ ] **Step 1: Extend the service contract**

In `IUserAdminService.cs`, add beside the existing `BlockAsync`/`UnblockAsync`:
```csharp
/// <summary>Never carries PasswordHash — the entity has one and must not be projected.</summary>
public sealed record AdminUserListItem(
    Guid Id, string Email, string Username, string Role, string Status, DateTime CreatedAt);

Task<PagedResult<AdminUserListItem>> ListAsync(int page, int pageSize, CancellationToken ct = default);
```
`PagedResult<T>` already exists in `Domain.Abstractions` (`IssueViews.cs`) — reuse it.

- [ ] **Step 2: Implement it**

In `UserAdminService.cs`, mirroring how `BlockAsync` already reads `_db.Users`:

```csharp
public async Task<PagedResult<AdminUserListItem>> ListAsync(
    int page, int pageSize, CancellationToken ct = default)
{
    var p = page < 1 ? 1 : page;
    var size = pageSize < 1 ? 50 : Math.Min(pageSize, 100);

    // Users carries no tenant query filter by design: it IS the tenant root,
    // and an Admin is a platform operator acting across tenants. The Admin
    // policy on the controller is therefore the ONLY guard on this read.
    var q = _db.Users;
    var total = await q.CountAsync(ct);

    var items = await q
        .OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id)
        .Skip((p - 1) * size).Take(size)
        // Project explicitly — never select the entity, which carries PasswordHash.
        .Select(u => new AdminUserListItem(
            u.Id, u.Email, u.Username, u.Role.ToString(), u.Status.ToString(), u.CreatedAt))
        .ToListAsync(ct);

    return new PagedResult<AdminUserListItem>(items, p, size, total);
}
```

- [ ] **Step 3: Add the endpoint**

In `AdminUsersController` (already `[Route("api/admin/users")]` + `[Authorize(Policy = "Admin")]`):
```csharp
[HttpGet]
public async Task<IActionResult> List(
    [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    => Ok(await _users.ListAsync(page, pageSize, ct));
```

- [ ] **Step 4: Write the API tests — the 403 is load-bearing**

`tests/JiraUnpacked.Tests/Admin/AdminUserListTests.cs`. Follow the existing style in `tests/JiraUnpacked.Tests/Admin/BlockUnblockEndpointTests.cs` for obtaining an admin token and an End User token.

```csharp
[Fact] // The Admin policy is the ONLY thing between an End User and every account.
public async Task End_user_cannot_list_users()
{
    // authenticate as an End User
    var response = await client.GetAsync("/api/admin/users");
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
}

[Fact]
public async Task Response_never_contains_a_password_hash()
{
    // as an admin, with at least one seeded user
    var raw = await response.Content.ReadAsStringAsync();
    // Asserted on the RAW JSON, not a typed DTO: a typed assertion cannot
    // catch an extra serialised property.
    Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("$2a$", raw);   // a bcrypt hash prefix, in case the name changes
}

[Fact]
public async Task Admin_sees_users_paged() { /* page=1&pageSize=1 → 1 item, TotalCount ≥ 2 */ }
```

- [ ] **Step 5: Client — replace the id input with a list**

In `src/api/admin.ts`:
```ts
export interface AdminUserListItem {
  id: string; email: string; username: string;
  role: string; status: string; createdAt: string;
}
export const listUsers = (page = 1, pageSize = 50) =>
  apiFetch<PagedResult<AdminUserListItem>>(`/api/admin/users?page=${page}&pageSize=${pageSize}`);
```
Add `adminUsers: (page: number) => ['admin', 'users', page] as const` to `queryKeys.ts`.

In `AdminPage.tsx`, replace the "type a user id" input with a `Table` of users (username, email, role, status, created) and per-row **Block** / **Unblock** / **Delete** actions operating on that row's id.

**Keep everything the reviews established:**
- Delete stays behind the **typed-`DELETE` confirmation**, and the dialog stays **conditionally mounted** so its state resets on close. Spec 1 shipped that bug; Spec 4's test guards it.
- Action results still name the affected user, and stale results must not leak across rows — the previous `variables === userId` render guard becomes "matches this row".
- Block/unblock reject non-End-Users server-side, so show `role`/`status` and let the server's message surface rather than hiding admins from the list.
- Invalidate `queryKeys.adminUsers(page)` after a successful action so the list reflects the new status.

- [ ] **Step 6: Update the admin tests**

Keep the existing 403, invite-code, and typed-confirmation tests. Update those that typed a user id to act on a listed row instead. Add: the list renders users; a row action targets **that row's** id; and a result for one row does not appear against another.

- [ ] **Step 7: Run and commit**

From `src/JiraUnpacked.Web/`: `npm run test && npm run typecheck && npm run lint && npm run build`. Report the count. **Do not run `dotnet test`.**

```bash
git add -A src/JiraUnpacked.Domain src/JiraUnpacked.Infrastructure src/JiraUnpacked.Api tests/JiraUnpacked.Tests src/JiraUnpacked.Web/src
git commit -m "feat: list users for admins, replace the id-typing admin UI"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green; report the count (it will be **above** 278 by the new tests).
- [ ] From `src/JiraUnpacked.Web/`: `npm run typecheck && npm run lint && npm run test && npm run build` → green; the count reflects the deleted mirror tests.
- [ ] `git diff develop..HEAD --stat -- '*/Migrations/*'` → **empty**. No migration in this spec.
- [ ] Confirm `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is unmodified.
- [ ] `grep -rn "passwordHash\|PasswordHash" src/JiraUnpacked.Api/` → no projection into any response.
- [ ] `git diff develop..HEAD -- src/JiraUnpacked.Web/package.json` → no dependency added.
- [ ] `ls src/JiraUnpacked.Web/src/features/issues/filterCatalog*` → **gone**.
- [ ] `grep -rn "hasProblemDetailsTitle" src/JiraUnpacked.Web/src/` → **gone** (the retired workaround).
- [ ] Push `feat/api-gaps-followups`; PR → develop; CI green on both jobs; squash-merge. The `develop → main` release is a separate, user-approved step.

## Notes for execution

- **Every task must retire the client debt, not just add the endpoint.** An endpoint nobody adopts leaves both the workaround and the fix in place — worse than before.
- **BF1's client test is the load-bearing one:** a post-refresh 401 carrying a ProblemDetails body must now log out. It has to fail against the current code, because that is the latent bug being fixed.
- **BF3's 403 test is not a formality.** `Users` has no tenant query filter — the `Admin` policy is the only guard, so that test protects every account on the platform.
- **Assert the password-hash absence on raw JSON**, not a typed DTO: a typed assertion cannot see an extra serialised property.
- **Assert the catalog's operator sets exactly**, not by containment: a superset passes `Contains` while offering the client an operator the server will reject.
- Deferred, each its own spec: saved/named filters; cross-field OR and nested groups; filtering metrics and gap-checks; httpOnly-cookie auth.
