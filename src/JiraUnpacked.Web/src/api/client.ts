import { ApiError, toApiError } from './errors';

/**
 * The seam that lets the HTTP layer use tokens without importing the auth
 * module (which must call apiFetch to refresh). auth/session.ts registers
 * itself here at start-up.
 */
export interface AuthBridge {
  getAccessToken(): string | null;
  /** Single-flight: concurrent callers share one in-flight refresh. */
  refresh(): Promise<boolean>;
  onAuthFailure(): void;
}

let bridge: AuthBridge | null = null;

export function setAuthBridge(next: AuthBridge | null): void {
  bridge = next;
}

export interface ApiFetchOptions {
  method?: string;
  body?: unknown;
  signal?: AbortSignal;
  /** Set on the refresh call itself so a 401 there cannot recurse. */
  skipAuthRetry?: boolean;
}

async function send(path: string, options: ApiFetchOptions): Promise<Response> {
  // Sent on every request. /api/auth/refresh and /logout require it as the second CSRF
  // layer behind SameSite=Strict: a cross-site form post cannot set a header at all, and a
  // cross-site fetch that sets one triggers a CORS preflight, which fails because the app
  // publishes no CORS policy.
  const headers: Record<string, string> = { 'X-Requested-With': 'XMLHttpRequest' };
  const token = bridge?.getAccessToken() ?? null;
  if (token) headers.Authorization = `Bearer ${token}`;
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';

  return fetch(path, {
    method: options.method ?? 'GET',
    headers,
    // fetch already defaults to same-origin; stated explicitly because the refresh cookie
    // depends on it, and a default is the kind of thing a refactor changes without noticing.
    credentials: 'same-origin',
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
  });
}

async function parseOk<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  if (!text) return undefined as T;
  return JSON.parse(text) as T;
}

export async function apiFetch<T>(path: string, options: ApiFetchOptions = {}): Promise<T> {
  const response = await send(path, options);

  if (response.status === 401 && !options.skipAuthRetry && bridge) {
    const refreshed = await bridge.refresh();
    if (!refreshed) {
      bridge.onAuthFailure();
      throw await toApiError(response);
    }
    const retry = await send(path, options);
    if (retry.status === 401) {
      // A 401 after a successful refresh cannot be a stale bearer token — the
      // retry carried a fresh one. Since Jira credential failures are now 502,
      // every remaining 401 here is a genuine session failure (an invalid `sub`
      // claim, a revoked account), so ending the session is correct.
      bridge.onAuthFailure();
      throw await toApiError(retry);
    }
    if (!retry.ok) throw await toApiError(retry);
    return parseOk<T>(retry);
  }

  if (!response.ok) throw await toApiError(response);
  return parseOk<T>(response);
}

export { ApiError };
