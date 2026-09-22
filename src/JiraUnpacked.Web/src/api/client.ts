import { ApiError, toApiError } from './errors';

/**
 * The seam that lets the HTTP layer use tokens without importing the auth
 * module (which must call apiFetch to refresh). auth/session.ts registers
 * itself here at start-up.
 */
/**
 * Why this is not a boolean. A refresh can fail two ways that mean opposite things:
 * the session is genuinely over ('unauthenticated'), or the attempt itself could not be
 * made — rate limited, offline, server down ('unavailable'). Collapsing them signs a user
 * out of a working session because they reloaded too quickly.
 */
export type RefreshOutcome = 'ok' | 'unauthenticated' | 'unavailable';

export interface AuthBridge {
  getAccessToken(): string | null;
  /** Single-flight: concurrent callers share one in-flight refresh. */
  refresh(): Promise<RefreshOutcome>;
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
  return parseOk<T>(await sendAuthorized(path, options));
}

export interface Download {
  blob: Blob;
  /** The name the server gave the file, or null when it named none. */
  filename: string | null;
}

/**
 * A file the user asked for that needs the session to fetch. Downloads cannot be a plain
 * `<a href download>`: a navigation carries cookies but not the in-memory bearer token, so the
 * server answers every such link with a 401. The price is that the file is held in memory
 * before the browser is handed it, which is why whatever serves one must cap its size.
 */
export async function apiDownload(
  path: string,
  options: Pick<ApiFetchOptions, 'signal'> = {},
): Promise<Download> {
  const response = await sendAuthorized(path, options);
  return {
    blob: await response.blob(),
    filename: filenameFrom(response.headers.get('Content-Disposition')),
  };
}

function filenameFrom(contentDisposition: string | null): string | null {
  const match = contentDisposition && /filename="?([^";]+)"?/i.exec(contentDisposition);
  return match ? match[1] : null;
}

/**
 * Sends with the session's token, refreshing once on a 401. Resolves only with a 2xx response;
 * every other outcome throws, so a caller can never mistake an error body for its result.
 */
async function sendAuthorized(path: string, options: ApiFetchOptions): Promise<Response> {
  const response = await send(path, options);

  if (response.status === 401 && !options.skipAuthRetry && bridge) {
    const outcome = await bridge.refresh();

    if (outcome === 'unavailable') {
      // The refresh never got an answer, so nothing is known about the session. Surface the
      // original 401 without ending the session: a rate limit or a blip is not a sign-out.
      throw await toApiError(response);
    }

    if (outcome === 'unauthenticated') {
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
    return retry;
  }

  if (!response.ok) throw await toApiError(response);
  return response;
}

export { ApiError };
