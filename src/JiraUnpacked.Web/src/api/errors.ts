export const RATE_LIMIT_MESSAGE = 'Too many attempts — please wait a minute and try again.';
export const UPSTREAM_JIRA_MESSAGE =
  'Jira did not respond as expected. Check the connection and try again.';

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail?: string;

  constructor(status: number, title: string, detail?: string) {
    super(title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
  }
}

function statusFallback(status: number): string {
  switch (status) {
    case 400: return 'That request was not valid.';
    case 401: return 'Your session has expired. Please sign in again.';
    case 403: return 'You do not have access to that.';
    case 404: return 'Not found.';
    case 409: return 'That conflicts with something that already exists.';
    default: return status >= 500 ? 'Something went wrong on the server.' : `Request failed (${status}).`;
  }
}

async function readJson(response: Response): Promise<unknown> {
  try {
    const text = await response.text();
    if (!text) return null;
    return JSON.parse(text) as unknown;
  } catch {
    return null; // empty or non-JSON body — the caller falls back to the status.
  }
}

/** Normalises every error shape the API produces into one typed error. */
export async function toApiError(response: Response): Promise<ApiError> {
  // These two are decided by status alone so the user gets an actionable
  // message instead of the framework's generic wording.
  if (response.status === 429) return new ApiError(429, RATE_LIMIT_MESSAGE);
  if (response.status === 502) return new ApiError(502, UPSTREAM_JIRA_MESSAGE);

  const body = await readJson(response);
  if (body && typeof body === 'object') {
    const shape = body as { title?: unknown; detail?: unknown; error?: unknown };
    if (typeof shape.title === 'string' && shape.title.length > 0) {
      return new ApiError(
        response.status,
        shape.title,
        typeof shape.detail === 'string' ? shape.detail : undefined,
      );
    }
    if (typeof shape.error === 'string' && shape.error.length > 0) {
      return new ApiError(response.status, shape.error);
    }
  }
  return new ApiError(response.status, statusFallback(response.status));
}
