# Web UI — Spec 1: Foundation design

**Status:** Approved (2026-08-08)
**Epic:** Web UI — the browser front-end over the completed API (entire scoped REQUIREMENTS set shipped to `main` @ `9313e67`).
**This spec:** Spec **1 of 4** — the foundation: project scaffold, design system, typed API client with token refresh, authentication screens, and the app shell.

The API is feature-complete and has no UI. This epic adds one. The four specs are sequenced so each ships working software; this one ends with "a user can register, verify, log in, and navigate an empty shell."

---

## 1. Scope & non-goals

**In scope:** a React + TypeScript single-page app at `src/JiraUnpacked.Web/`, served same-origin by the existing API, covering:

- the build/test toolchain and its CI job,
- a small design system (light + dark),
- a typed API client that handles auth headers, token refresh, and error normalisation,
- the authentication screens (register → verify → login → logout) and route guards,
- the app shell (nav + user menu) that later specs hang pages on.

**Out of scope — later specs in this epic:**

| Spec | Scope |
|---|---|
| 2 — Connect & Configure | connections, projects, semantic-layer mapping, sync trigger + run history |
| 3 — Explore | issue list, FR-E4 filter builder, issue detail |
| 4 — Insight | the four curated metrics, gap-checks, admin panel (invite codes, block/delete) |

**Explicit non-goals (YAGNI):** no Redux/Zustand (TanStack Query holds server state; React context holds the little client state there is), no i18n, no component library, no SSR, no PWA/offline, no Playwright e2e in this spec.

**No backend behaviour changes.** The only API-side edit is static-file hosting (§4) — no controller, service, entity, migration, or auth-logic change. The FR-B2 tenant-isolation gate is untouched.

## 2. Decisions locked in brainstorming

- **React 19 + TypeScript + Vite.** Rejected Blazor WASM (one toolchain and shared DTOs, but a heavy initial payload and a weaker table/chart ecosystem for a reporting app) and Razor Pages + HTMX (simplest deploy, but the FR-E4 filter builder and the metric charts genuinely want client state).
- **Five runtime dependencies, no component library:** React, React Router, TanStack Query, Tailwind CSS v4, and (Spec 4 only) Recharts.
- **Same-origin serving — no CORS anywhere**, in dev or production (§4).
- **Access token in memory, refresh token in `localStorage`** (§5), with the XSS tradeoff stated rather than hidden.
- **The UI lives in the repo** at `src/JiraUnpacked.Web/`, matching the existing `src/` convention, and ships in the same CI pipeline.

## 3. Architecture

```
src/JiraUnpacked.Web/
  src/
    api/          client.ts (apiFetch), errors.ts (ProblemDetails → ApiError), endpoints/auth.ts
    auth/         AuthContext.tsx, useAuth.ts, tokenStore.ts, RequireAuth.tsx
    ui/           Button, Field, Select, Card, Table, Badge, Spinner,
                  EmptyState, ErrorState, Toast, Modal
    routes/       Login.tsx, Register.tsx, Verify.tsx, NotFound.tsx
    shell/        AppShell.tsx, SideNav.tsx, UserMenu.tsx, ThemeToggle.tsx
    main.tsx, App.tsx, theme.css
  index.html, package.json, vite.config.ts, tsconfig.json, vitest.config.ts
```

Each unit has one job: `api/` knows HTTP and nothing about React; `auth/` owns token lifecycle and exposes a hook; `ui/` is presentational with no API knowledge; `routes/` and `shell/` compose them. This keeps the pieces independently testable — the refresh logic is tested without rendering, the primitives without a server.

## 4. Serving and the dev proxy (no CORS)

The API currently serves no static files and registers no CORS policy. Rather than add CORS, the SPA is served from the **same origin** as the API.

**Development:** the Vite dev server (`http://localhost:5173`) proxies `/api` to `https://localhost:7191` (the existing `https` launch profile) with `secure: false`, because the dev certificate is self-signed. The browser only ever sees one origin, so no CORS, no preflight.

**Production:** `npm run build` emits to `src/JiraUnpacked.Api/wwwroot/`, which the API serves. One artifact deploys.

**The `Program.cs` change — placement is load-bearing.** The current pipeline is HSTS → HttpsRedirection → Authentication → Authorization → RateLimiter → TenantScopeMiddleware → `MapControllers()`. Two additions:

1. `app.UseStaticFiles()` **before** `UseAuthentication()`, so the JS/CSS bundle is served without an auth challenge.
2. `app.MapFallbackToFile("index.html")` **after** `MapControllers()`, so `/api/*` routes always win over the SPA fallback.

**The subtlety this spec resolves explicitly:** a bare `MapFallbackToFile` also swallows *unmatched* `/api/**` paths and returns `index.html` with HTTP 200 — which would turn a genuine 404 into HTML that the client parses as JSON, producing a confusing error. So an unmatched-API terminal route is registered ahead of the fallback:

```csharp
app.MapControllers();
// An unknown /api path must stay a JSON 404 — never fall through to the SPA shell.
app.Map("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
```

`wwwroot/` build output is git-ignored; CI builds it.

**Consequence for the existing .NET tests, resolved here:** `wwwroot/` will not exist in a fresh clone or in the integration-test host. That is safe and must stay safe — with no web root, `UseStaticFiles` resolves a null file provider and no-ops, and `MapFallbackToFile` finds no `index.html` and falls through to a 404. So the 276 existing integration tests keep seeing exactly the status codes they assert today. The implementation task verifies this by running the full .NET suite after the `Program.cs` change, and treats any changed status code as a defect in this design rather than a test to update.

## 5. Authentication and token lifecycle

`POST /api/auth/login` and `/refresh` both return `{ accessToken, expiresAtUtc, refreshToken }` in the body. There is no cookie auth, so the SPA holds the tokens.

- **Access token: memory only** (React state). Never written to disk.
- **Refresh token: `localStorage`**, so a page reload does not log the user out. On boot the app calls `/api/auth/refresh` once; success hydrates the session, failure clears storage and routes to `/login`.
- **Rotation:** every refresh returns a new refresh token, which replaces the stored one.
- **Single-flight refresh:** if several requests 401 at once, the first starts a refresh and the rest await that same promise, then each retries **once**. A second 401 after a successful refresh is a real failure — log out; never loop.
- **Logout** calls `POST /api/auth/logout` with the refresh token (revoking it server-side) and clears both tokens even if that call fails.
- **Tokens are never logged**, consistent with the project's existing rule that tokens never appear in logs, responses, or stored error text.

**Stated tradeoff:** a successful XSS could read the refresh token from `localStorage`. The stronger design — refresh token in an httpOnly, SameSite cookie plus CSRF protection — requires backend auth changes and is therefore out of this spec's scope; it is recorded as a follow-up candidate (§9). Memory-only storage was rejected because it logs the user out on every reload, which for a self-hosted internal tool is a poor trade for a threat that already implies script execution in the page.

## 6. The API client

A single `apiFetch<T>(path, options)` is the only place `fetch` is called.

- Injects `Authorization: Bearer <accessToken>` when a session exists.
- **Normalises errors into a typed `ApiError { status, title, detail? }`.** The API returns RFC 7807 **ProblemDetails** (`Problem(statusCode:…, title:…)`) from most failure paths, plus a few plain `{ error: "…" }` bodies (for example the throughput endpoint's `bucket` validation) and bodies that are empty (`NotFound()`, `Conflict()`). The client parses ProblemDetails first, falls back to `error`, then to a status-derived message — so no screen ever renders `[object Object]`.
- **429 is first-class.** The auth limiter is 10/min per IP and the sync limiter 5/min per user; when either trips the client surfaces "Too many attempts — please wait a minute and try again" rather than a generic failure, so a rate-limited app never looks broken.
- **502** (the `JiraApiExceptionFilter` result for upstream Jira failures) is surfaced as an upstream-Jira problem, distinguishing "Jira is unhappy" from "this app is broken" — which matters most in Spec 2.
- Enum-valued fields serialise **by name** (the API registers `JsonStringEnumConverter`), so TypeScript models enums as string unions — relevant to Specs 2 and 3.

TanStack Query wraps `apiFetch` for caching, in-flight de-duplication, and pagination in later specs.

## 7. Screens in this spec

- **Login** — email-or-username + password (matching `LoginRequest`), inline error on 401, rate-limit message on 429.
- **Register** — invite code → email → username → password, in that order (FR-A1/FR-A2). The strength meter mirrors the server policy exactly: **minimum 12 characters and at least 3 of 4 character classes** (lower, upper, digit, symbol). The client hint prevents a pointless round-trip; the server remains the authority and its `PasswordValidationResult` errors are displayed verbatim when they occur.
- **Verify** — code entry plus resend. Resend is server-throttled, so the button disables with a visible countdown after each attempt.
- **App shell** — side nav (the later specs' destinations appear as they land), top bar with theme toggle and a user menu offering logout and account deletion. Deleting an account is destructive and irreversible (it cascades across every tenant table), so it requires a typed confirmation, not a single click.
- **Route guards** — `RequireAuth` redirects unauthenticated users to `/login`, preserving the attempted path. The `role` claim from `/api/auth/me` is read and stored now; it gates the admin nav in Spec 4.

**Design system:** light and dark themes via CSS custom properties, defaulting to the OS preference with a manual toggle persisted in `localStorage`. Layout is responsive to a phone-width viewport.

## 8. Testing

Vitest + React Testing Library + **MSW** (mock API at the network boundary). Tests target logic that can actually break, not markup:

- **Token refresh:** a 401 triggers exactly one refresh and one retry; N concurrent 401s trigger exactly **one** refresh (single-flight); a failed refresh clears tokens and redirects; a 401 after a successful refresh logs out rather than looping.
- **Boot:** a stored refresh token hydrates the session; an invalid one clears storage and lands on `/login`.
- **Error normalisation:** ProblemDetails, `{error}`, and empty bodies each map to the right `ApiError`; 429 yields the rate-limit message; 502 yields the upstream-Jira message.
- **Password meter** agrees with the server policy at its boundaries (11 vs 12 characters; 2 vs 3 character classes).
- **Route guards:** unauthenticated access redirects and preserves the attempted path.
- **Logout** clears both tokens even when the server call fails.

Explicitly **not** tested: component markup snapshots, and the styling itself.

**CI:** a `web` job runs `npm ci`, typecheck, lint, `vitest run`, and `npm run build`. It runs alongside the existing `Build & Test` job; the .NET suite (276 tests) must stay green and untouched.

## 9. Success criteria

- A user registers with an invite code, verifies by email code, logs in, reloads the page without being logged out, and logs out.
- Every API failure the screens can hit renders a human-readable message; a rate-limited response reads as rate-limiting, not as a crash.
- The SPA is served by the API in production and proxied in development, with **no CORS policy in the codebase** and `/api/*` never shadowed by the SPA fallback — an unknown `/api` path still returns a JSON 404.
- Access tokens never reach persistent storage; no token is ever logged.
- The .NET suite stays green, the FR-B2 gate is untouched, and no migration is added.

## 10. Notes / follow-ups

- **Deferred, in likely order:** Spec 2 (Connect & Configure), Spec 3 (Explore, incl. the FR-E4 filter builder UI), Spec 4 (Insight + admin).
- **Auth hardening candidate:** move the refresh token to an httpOnly `SameSite=Strict` cookie with CSRF protection. It is a backend change, so it warrants its own spec rather than riding along here.
- `/api/auth/me` currently returns `sub`, `role`, and `endUserId` but no display name; the user menu shows the username captured at login. If a richer profile is wanted later, that is a small API addition.
