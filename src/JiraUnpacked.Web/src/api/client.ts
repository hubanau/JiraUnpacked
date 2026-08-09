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
  const headers: Record<string, string> = {};
  const token = bridge?.getAccessToken() ?? null;
  if (token) headers.Authorization = `Bearer ${token}`;
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';

  return fetch(path, {
    method: options.method ?? 'GET',
    headers,
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

/**
 * True when `body` looks like an ASP.NET ProblemDetails payload — i.e. it
 * carries a non-empty `title`. That shape is only ever produced by our own
 * application code choosing to reject a specific request (see
 * JiraApiExceptionFilter, which maps a revoked/expired *Jira* token to 401
 * with a titled ProblemDetails body). The bearer-token challenge that
 * rejects a genuinely bad *access* token, by contrast, carries no body at
 * all — that distinction is exactly what apiFetch uses below to decide
 * whether a 401 means our own session is over.
 */
function hasProblemDetailsTitle(body: unknown): body is { title: string } {
  return (
    body !== null &&
    typeof body === 'object' &&
    typeof (body as { title?: unknown }).title === 'string' &&
    (body as { title: string }).title.length > 0
  );
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
      // A 401 that survives a *successful* refresh is ambiguous on its own:
      // it could mean our own session really is over, or it could be some
      // other check on our API rejecting this particular request for a
      // reason that has nothing to do with our session — e.g. the stored
      // Jira credentials being rejected upstream (JiraApiExceptionFilter
      // maps that to 401 too). Since refresh just succeeded, the retry
      // above already carried a *fresh* access token, so a 401 caused by
      // our bearer token being stale is impossible here; only the actual
      // JWT bearer challenge (which is always bodiless) can mean that.
      //
      // Discriminator: read the body once, and only treat this as a real
      // session failure when it is empty. A 401 that instead carries a
      // JSON ProblemDetails body (a `title`) was produced deliberately by
      // our own application code AFTER the bearer token had already been
      // accepted, so it must not log the user out of Jira Unpacked.
      const bodyText = await retry.text(); // Response bodies are single-use — read exactly once.
      let parsedBody: unknown = null;
      try {
        parsedBody = bodyText ? JSON.parse(bodyText) : null;
      } catch {
        parsedBody = null; // non-JSON body — treated the same as no body.
      }

      if (!hasProblemDetailsTitle(parsedBody)) {
        bridge.onAuthFailure();
      }

      // Rebuild a Response over the text already consumed above so
      // toApiError can apply its normal ProblemDetails/`{error}` parsing
      // without re-reading the original (now-drained) stream.
      throw await toApiError(new Response(bodyText, { status: retry.status }));
    }
    if (!retry.ok) throw await toApiError(retry);
    return parseOk<T>(retry);
  }

  if (!response.ok) throw await toApiError(response);
  return parseOk<T>(response);
}

export { ApiError };
