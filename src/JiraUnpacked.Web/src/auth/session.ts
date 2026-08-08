import { setAuthBridge } from '../api/client';
import { refreshTokens, type TokenResponse } from '../api/auth';

export const REFRESH_KEY = 'ju.refreshToken';

/**
 * The access token is deliberately module state and never touches any
 * persistent store — a page reload re-obtains it from the refresh token.
 */
let accessToken: string | null = null;

/** The in-flight refresh, shared by every concurrent caller (single-flight). */
let inFlight: Promise<boolean> | null = null;

export function getAccessToken(): string | null {
  return accessToken;
}

export function getRefreshToken(): string | null {
  return localStorage.getItem(REFRESH_KEY);
}

export function setTokens(tokens: TokenResponse): void {
  accessToken = tokens.accessToken;
  localStorage.setItem(REFRESH_KEY, tokens.refreshToken);
}

export function clearTokens(): void {
  accessToken = null;
  localStorage.removeItem(REFRESH_KEY);
}

async function doRefresh(): Promise<boolean> {
  const stored = getRefreshToken();
  if (!stored) return false;
  try {
    // Refresh rotates: the response carries a new refresh token that
    // replaces the stored one.
    setTokens(await refreshTokens(stored));
    return true;
  } catch {
    clearTokens();
    return false;
  }
}

/**
 * Refreshes the session, collapsing concurrent callers onto one network
 * request. Without this, a page issuing several requests at once would fire
 * one refresh per 401 and — because refresh rotates — all but the first would
 * present an already-consumed token and fail.
 */
export function refreshOnce(): Promise<boolean> {
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
