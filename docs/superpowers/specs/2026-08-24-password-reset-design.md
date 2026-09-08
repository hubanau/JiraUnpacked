# Password reset

**Status:** Approved (2026-08-24)
**Scope:** backend + SPA. A user who has forgotten their password can regain access by email.

Registration is invite-only and there is no self-service way back in. Today a forgotten password means asking an Admin, and an Admin cannot help — there is no reset endpoint and `PasswordHash` is never readable. The account is simply lost.

---

## 1. Why this is not "email verification again"

The two flows look identical and are not. Verification **activates an account you already proved you own**. Reset **hands over an account**, so a guessed challenge is full account takeover.

That single difference drives the main decision below, and it is the reason this does not simply reuse the 6-digit code.

## 2. A high-entropy token, not a 6-digit code

`EmailVerification` uses a 6-digit code, capped at 5 attempts and 15 minutes. That is fine for activation. For reset it is thin: an attacker can request a fresh code repeatedly, and each request buys another 5 guesses against a 1,000,000 space. Per-IP rate limiting is the only thing bounding that, and it is exactly what a botnet defeats.

So a reset token is **32 random bytes**, delivered as a link — the same shape as a refresh token, and not guessable at any request volume. Stored as a SHA-256 hash, compared in fixed time, single-use, 30-minute expiry.

## 3. The link base URL comes from configuration, never the request

The reset email contains `{App:BaseUrl}/reset-password?token=…`.

**It must not be built from the `Host` header.** That is host-header injection: an attacker submits a reset for someone else's address with a forged `Host`, and the victim receives a genuine email carrying a valid token pointed at the attacker's server. The victim clicks their own legitimate-looking email and hands over the token.

Configuration is not attacker-controlled, so it closes that off entirely. It also means a misconfigured deployment produces a visibly broken link rather than a silent leak.

## 4. Rules

- **The request endpoint always answers 202**, whatever the address. Anything else turns it into an account-existence oracle, which is the same reason `verify/resend` already answers unconditionally.
- **Only `Active` users get a token.** A `PendingVerification` user must not reset, because that would let someone who never proved they own the address take the account through a second door. A `Blocked` user must not either — that would undo the block.
- **Requesting again invalidates outstanding tokens.** One live token per user, so an old email cannot be replayed.
- **On success, every refresh token is revoked.** This is the point people skip. If the reset was because someone else got in, leaving their session alive defeats the whole exercise. Existing sessions must die.
- The new password goes through the same `PasswordPolicy` as registration, and the server stays authoritative.
- Both endpoints are anonymous and carry the existing `auth` rate-limit policy.
- The token is never logged, never returned in a response body, and never written to an error.

## 5. Shape

| Method | Route | Result |
|---|---|---|
| POST | `/api/auth/password/forgot` | **202** always, body says only that an email may have been sent |
| POST | `/api/auth/password/reset` | **204**, or **400** for an invalid/expired/used token or a weak password |

`PasswordResetTokens` is keyed by `UserId`, not `EndUserId` — like `RefreshTokens` and `EmailVerifications` it is an identity table, not tenant data. No query filter, no RLS policy, no FR-B2 gate change. It cascades from `User`, so erasure stays complete.

## 6. SPA

- Sign-in gains a **Forgot your password?** link.
- `/forgot-password` — one email field. On submit it shows the same confirmation regardless of outcome, or the oracle is simply moved to the client.
- `/reset-password?token=…` — new password with the existing strength hints, plus a confirmation field. A missing token is stated up front rather than after typing a password.
- Success routes to sign-in. It does **not** auto-sign-in: the reset deliberately revoked every session, and issuing one here would contradict that.

## 7. Testing

- **The oracle test:** a request for an unknown address, a blocked user and a pending user all return 202, and their responses are byte-identical to the one for a real active user. A status-only assertion would miss a body that differs.
- A token resets the password, and the old password then fails while the new one works.
- **A reset kills existing sessions:** sign in, reset, and the refresh token from before is rejected.
- Single use — a second reset with the same token is refused.
- Expired token refused; a token belonging to another user is refused.
- Requesting twice invalidates the first token.
- A weak new password is refused with the policy's own message, and the password is unchanged.
- The token never appears in any response body, asserted on raw JSON.
- The stored value is a hash — the raw token is never in the table.

## 8. Success criteria

A user who has forgotten their password gets back in by email; nobody learns whether an address is registered; a reset ends every existing session; and a token cannot be guessed, replayed, reused, or aimed at a host the attacker chose.
