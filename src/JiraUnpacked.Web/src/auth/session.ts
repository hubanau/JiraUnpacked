import { ApiError, setAuthBridge, type RefreshOutcome } from '../api/client';
import { refreshTokens, type TokenResponse } from '../api/auth';

export const USERNAME_KEY = 'ju.username';

/**
 * The access token is deliberately module state and never touches any
 * persistent store, and the refresh token is not here at all: it lives in an
 * httpOnly cookie the browser attaches to /api/auth calls. Script cannot read
 * it, so an XSS cannot exfiltrate a credential to use later or elsewhere.
 *
 * Nothing in this module persists a credential. localStorage holds only
 * ju.username and ju.theme, neither of which is one.
 */
let accessToken: string | null = null;

/** The in-flight refresh, shared by every concurrent caller (single-flight). */
let inFlight: Promise<RefreshOutcome> | null = null;

/**
 * localStorage can throw (storage disabled, private-browsing quota, a
 * blocked third-party context, etc.). Treat any such failure as "nothing
 * stored" rather than letting it propagate — a storage failure should
 * degrade to a logged-out state, not hang or crash the caller.
 */
export function safeGetItem(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function safeSetItem(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Best effort; losing persistence here is not fatal.
  }
}

export function safeRemoveItem(key: string): void {
  try {
    localStorage.removeItem(key);
  } catch {
    // Best effort.
  }
}

export function getAccessToken(): string | null {
  return accessToken;
}

export function setTokens(tokens: TokenResponse): void {
  accessToken = tokens.accessToken;
}

/**
 * Clears the in-memory access token only. The refresh cookie is httpOnly, so
 * the server is the only party that can remove it — which /api/auth/logout
 * does.
 */
export function clearTokens(): void {
  accessToken = null;
}

async function doRefresh(): Promise<RefreshOutcome> {
  try {
    // No stored token to check first: the cookie is unreadable, so the
    // server's answer IS the check. Rotation still happens — the response
    // carries a fresh Set-Cookie the browser swaps in.
    setTokens(await refreshTokens());
    return 'ok';
  } catch (error) {
    // Only a 401 means the session is over. A 429 means the refresh endpoint
    // declined to answer — and since boot now always refreshes, a few quick
    // reloads can hit the shared per-IP auth limit. Treating that as a sign-out
    // would evict a user with a perfectly valid session for reloading.
    if (error instanceof ApiError && error.status === 401) {
      clearTokens();
      return 'unauthenticated';
    }
    return 'unavailable';
  }
}

/**
 * Refreshes the session, collapsing concurrent callers onto one network
 * request. Without this, a page issuing several requests at once would fire
 * one refresh per 401 and — because refresh rotates — all but the first would
 * present an already-consumed token and fail. Collapsing also keeps a burst of
 * 401s from spending the per-IP auth rate limit N times over.
 */
export function refreshOnce(): Promise<RefreshOutcome> {
  if (inFlight) return inFlight;
  inFlight = doRefresh().finally(() => {
    inFlight = null;
  });
  return inFlight;
}

/** Wires the token lifecycle into the HTTP layer. Call once at start-up. */
export function installAuthBridge(onAuthFailure: () => void): void {
  setAuthBridge({
    getAccessToken,
    refresh: refreshOnce,
    onAuthFailure: () => {
      clearTokens();
      onAuthFailure();
    },
  });
}
