import { describe, expect, it } from 'vitest';
import { ApiError, RATE_LIMIT_MESSAGE, UPSTREAM_JIRA_MESSAGE, toApiError } from './errors';

function res(status: number, body?: unknown, contentType = 'application/json'): Response {
  if (body === undefined) return new Response(null, { status });
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': contentType } });
}

describe('toApiError', () => {
  it('reads RFC7807 ProblemDetails title and detail', async () => {
    const e = await toApiError(res(400, { title: 'Unknown field.', detail: 'field=nope' }));
    expect(e).toBeInstanceOf(ApiError);
    expect(e.status).toBe(400);
    expect(e.title).toBe('Unknown field.');
    expect(e.detail).toBe('field=nope');
  });

  it('reads the plain {error} shape', async () => {
    const e = await toApiError(res(400, { error: 'bucket must be one of: day, week, month' }));
    expect(e.title).toBe('bucket must be one of: day, week, month');
  });

  it('falls back to a status message for an empty body', async () => {
    const e = await toApiError(res(404));
    expect(e.status).toBe(404);
    expect(e.title).toBe('Not found.');
  });

  it('falls back for a non-JSON body without throwing', async () => {
    const e = await toApiError(new Response('<html>oops</html>', {
      status: 500, headers: { 'content-type': 'text/html' },
    }));
    expect(e.status).toBe(500);
    expect(e.title).toBe('Something went wrong on the server.');
  });

  it('maps 429 to the rate-limit message regardless of body', async () => {
    const e = await toApiError(res(429, { title: 'Too Many Requests' }));
    expect(e.title).toBe(RATE_LIMIT_MESSAGE);
  });

  it('maps 502 to the upstream-Jira message', async () => {
    const e = await toApiError(res(502, { title: 'Bad Gateway' }));
    expect(e.title).toBe(UPSTREAM_JIRA_MESSAGE);
  });
});
