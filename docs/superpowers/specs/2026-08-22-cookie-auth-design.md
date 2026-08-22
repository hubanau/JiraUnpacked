# httpOnly-cookie refresh tokens

**Status:** Approved (2026-08-22)
**Epic:** Post-UI backend follow-ups — **4 of 4**, and the one that closes the epic.
**Depends on:** nothing. Touches backend **and** SPA, unavoidably — moving the token out of `localStorage` is the whole point.

Spec 1 of the Web UI epic put the access token in memory only and the refresh token in `localStorage`, and recorded the upgrade: move the refresh token to an httpOnly cookie so script cannot read it at all. This does that.

---

## 1. What this actually buys

Precisely one thing, and it is worth being exact about it: **script can never read the refresh token.**

Today an XSS payload reads `localStorage['ju.refreshToken']` and walks away with a 7-day credential it can use from anywhere. After this change the token exists only as a `Set-Cookie` header the browser stores and replays; `document.cookie` cannot see it, `fetch` cannot read it, and it never appears in a response body — **including at login**, which is why the token is removed from the body rather than merely also set as a cookie.

What it does **not** buy: an XSS on the page can still *call* the API as the user for as long as the page is open, because the browser will attach the cookie to a refresh call the attacker's script makes. Cookies bound to an origin cannot prevent that. The win is that the credential cannot be **exfiltrated** and reused later, elsewhere, after the tab is closed.

## 2. Scope & non-goals

**In scope:** login/refresh/logout move to a cookie; the token leaves the response body and the request body; `localStorage` keeps only `ju.username` and `ju.theme`.

**Out of scope:**
- **Cookie-based access tokens.** The access token stays a `Bearer` header in memory. That is what makes every other endpoint CSRF-immune by construction — a browser never attaches an `Authorization` header on its own.
- **Sliding sessions, device lists, "log out everywhere".** Rotation and revocation already work and are unchanged.
- **Backwards compatibility with the body shape.** §5.

## 3. Cookie attributes, and why each one

`ju_rt`, set on login and re-set on every refresh (rotation is unchanged):

| Attribute | Value | Why |
|---|---|---|
| `HttpOnly` | yes | The entire point: script cannot read it. |
| `Secure` | outside Development | A cookie sent over plain HTTP is a cookie on the wire. Gated exactly like HSTS already is (`HstsGate`), because local development and the integration suite both run over HTTP and a hard-coded `Secure` would silently break both. |
| `SameSite` | `Strict` | The primary CSRF control — §4. |
| `Path` | `/api/auth` | The only endpoints that consume it. Nothing else in the app ever receives the token, so nothing else can leak it in a log or a proxy trace. |
| `Max-Age` | the refresh token's own lifetime | So the browser discards it exactly when the server would reject it. |

`Strict` is safe here despite its reputation: it withholds the cookie on a *cross-site navigation to* the app, but every refresh is an XHR the loaded app makes to its own origin, which is same-site. A user arriving from an external link lands anonymous for one request and the boot refresh — issued after load — carries the cookie normally.

## 4. CSRF: what the real exposure is

Worth reasoning through rather than reflexively bolting on a token.

Only two endpoints authenticate by cookie: `POST /api/auth/refresh` and `POST /api/auth/logout`. Everything else authenticates by `Bearer` header and so cannot be forged cross-site at all.

**`SameSite=Strict` is the primary control** and is a complete defence on any browser that honours it: a cross-site request never carries the cookie. And the residual damage even if one did not is small — a forged refresh rotates the victim's token and returns a new access token in a response the attacker **cannot read** (the app publishes no CORS policy, by design), and a forged logout is a nuisance.

**A required custom header is the second layer.** `/refresh` and `/logout` require `X-Requested-With: XMLHttpRequest`. A cross-site `<form>` post cannot set a header at all, and a cross-site `fetch` that sets one triggers a CORS preflight which fails — again because there is no CORS policy. This costs ten lines, does not depend on the browser honouring `SameSite`, and covers the case where someone later relaxes the attribute. A request without it gets **400** with a title that says what is missing, so a non-browser caller is not left guessing.

## 5. Removing the token from the body is the point, and it breaks existing sessions

`TokenResponse` loses `refreshToken`; `/refresh` and `/logout` lose their request bodies.

Leaving the token in the login response "for compatibility" would defeat the change: an XSS that runs during sign-in would read it straight out of the response, and any client that kept storing it would keep the exposure. The token must exist in exactly one place the browser controls.

**Consequence, stated plainly:** every currently signed-in user is signed out once, because their stored `localStorage` token is no longer sent anywhere and the server never issued them a cookie. They sign in again and it is over. For a self-hosted app whose only client is this SPA, that is the right trade; there is no third-party integration to break.

## 6. SPA changes

- `session.ts` drops `REFRESH_KEY`, `getRefreshToken`, and the token half of `setTokens`. `clearTokens` clears memory only — the *server* clears the cookie on logout, which is the only thing that can.
- **Boot always attempts a refresh.** There is no longer a readable token to check first, so the server's answer *is* the check: 200 resumes the session, 401 lands anonymous. Simpler than what it replaces, and `refreshOnce()`'s single-flight still makes React StrictMode's double-invoked effect safe.
- `logoutRequest()` becomes bodyless. It stays best-effort: the local session ends regardless, so a failed call never strands a user signed in.
- `apiFetch` sends `credentials: 'same-origin'` and the `X-Requested-With` header on every request. Explicit rather than relying on `fetch`'s default, because the default is the kind of thing a future refactor changes without noticing.
- `ju.username` and `ju.theme` keep their `localStorage` slots. Neither is a credential.

## 7. Testing

- **The load-bearing assertion: no response body on any auth route contains the refresh token.** Asserted on the **raw JSON** of login and refresh, not on a typed DTO — a typed assertion cannot see a property the DTO does not declare, which is exactly how this regression would return.
- The `Set-Cookie` on login and refresh carries `HttpOnly`, `SameSite=Strict`, `Path=/api/auth` and a `Max-Age`; the `Secure` gate is unit-tested as a pure function, since the suite runs over HTTP.
- Rotation still works **through the cookie**: refresh twice, and the first cookie's token is rejected on reuse — the existing rotation and reuse-rejection tests, re-pointed at the cookie.
- Logout revokes **and** clears the cookie (an expired `Set-Cookie`), and a refresh afterwards fails.
- A refresh or logout without the custom header is 400; with it and no cookie, 401.
- Blocking a user still invalidates their refresh — the existing test, via the cookie.
- SPA: boot with no session lands anonymous after one refresh attempt; boot with a session resumes; concurrent 401s still produce exactly one refresh call; `localStorage` is asserted to hold **no** key resembling a token after sign-in.

## 8. Success criteria

The refresh token is unreadable by script and absent from every response body; rotation, reuse rejection, revocation on block, and logout all work through the cookie; the SPA signs in, resumes on reload, and signs out; `localStorage` holds only `ju.username` and `ju.theme`.

## 9. Notes / follow-ups

This closes the post-UI backend follow-up epic. Nothing in it remains open.
