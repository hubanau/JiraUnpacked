import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../test/setup';
import { ApiError } from './errors';
import { apiFetch, setAuthBridge, type RefreshOutcome } from './client';

describe('apiFetch', () => {
  beforeEach(() => setAuthBridge(null));

  it('returns the parsed body on success', async () => {
    server.use(http.get('/api/thing', () => HttpResponse.json({ value: 42 })));
    await expect(apiFetch<{ value: number }>('/api/thing')).resolves.toEqual({ value: 42 });
  });

  it('sends the bearer token from the bridge', async () => {
    let seen: string | null = null;
    server.use(http.get('/api/thing', ({ request }) => {
      seen = request.headers.get('authorization');
      return HttpResponse.json({ ok: true });
    }));
    setAuthBridge({ getAccessToken: () => 'tok-1', refresh: async () => 'unauthenticated', onAuthFailure: () => {} });

    await apiFetch('/api/thing');

    expect(seen).toBe('Bearer tok-1');
  });

  it('sends no Authorization header when there is no token', async () => {
    let seen: string | null = 'unset';
    server.use(http.get('/api/thing', ({ request }) => {
      seen = request.headers.get('authorization');
      return HttpResponse.json({ ok: true });
    }));

    await apiFetch('/api/thing');

    expect(seen).toBeNull();
  });

  it('returns undefined for a 204 with no body', async () => {
    server.use(http.post('/api/thing', () => new HttpResponse(null, { status: 204 })));
    await expect(apiFetch('/api/thing', { method: 'POST' })).resolves.toBeUndefined();
  });

  it('throws a normalised ApiError on failure', async () => {
    server.use(http.get('/api/thing', () =>
      HttpResponse.json({ title: 'Nope.' }, { status: 400 })));

    await expect(apiFetch('/api/thing')).rejects.toMatchObject({ status: 400, title: 'Nope.' });
    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
  });

  it('refreshes once and retries the request after a 401', async () => {
    let calls = 0;
    server.use(http.get('/api/thing', ({ request }) => {
      calls++;
      return request.headers.get('authorization') === 'Bearer good'
        ? HttpResponse.json({ ok: true })
        : new HttpResponse(null, { status: 401 });
    }));

    let token = 'stale';
    const refresh = vi.fn(async (): Promise<RefreshOutcome> => { token = 'good'; return 'ok'; });
    setAuthBridge({ getAccessToken: () => token, refresh, onAuthFailure: () => {} });

    await expect(apiFetch('/api/thing')).resolves.toEqual({ ok: true });
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(calls).toBe(2);
  });

  it('gives up and signals auth failure when the refresh fails', async () => {
    server.use(http.get('/api/thing', () => new HttpResponse(null, { status: 401 })));
    const onAuthFailure = vi.fn();
    setAuthBridge({ getAccessToken: () => 'stale', refresh: async () => 'unauthenticated', onAuthFailure });

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
  });

  it('does NOT end the session when the refresh was merely unavailable', async () => {
    // A 429 or an offline blip says nothing about whether the session is valid. Signing the
    // user out here evicts a working session because they reloaded too quickly — the bug this
    // outcome type exists to prevent.
    server.use(http.get('/api/thing', () => new HttpResponse(null, { status: 401 })));
    const onAuthFailure = vi.fn();
    setAuthBridge({
      getAccessToken: () => 'stale',
      refresh: async () => 'unavailable',
      onAuthFailure,
    });

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
    expect(onAuthFailure).not.toHaveBeenCalled();
  });

  it('does not loop when the retry also 401s', async () => {
    let calls = 0;
    server.use(http.get('/api/thing', () => { calls++; return new HttpResponse(null, { status: 401 }); }));
    const refresh = vi.fn(async (): Promise<RefreshOutcome> => 'ok');
    const onAuthFailure = vi.fn();
    setAuthBridge({ getAccessToken: () => 'x', refresh, onAuthFailure });

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(calls).toBe(2); // original + exactly one retry
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
  });

  it('logs out on a post-refresh 401 even when it carries a ProblemDetails body', async () => {
    // Jira credential failures are 502 now (JiraApiExceptionFilter), so a
    // 401 that survives a successful refresh — carrying a fresh bearer
    // token — is never that. It is a genuine session failure, e.g.
    // AdminUsersController's or AuthController.DeleteMe's "Invalid token.",
    // both of which carry a ProblemDetails body. The old discriminator saw
    // that body and skipped signing out, leaving a genuinely invalid token
    // in a broken half-session — this is the regression test for that.
    server.use(http.get('/api/thing', () =>
      HttpResponse.json({ title: 'Invalid token.' }, { status: 401 })));
    const onAuthFailure = vi.fn();
    setAuthBridge({ getAccessToken: () => 'x', refresh: async () => 'ok', onAuthFailure });

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);

    // The old discriminator saw the body and skipped this — a genuinely
    // invalid token then left the user in a broken half-session.
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
  });

  it('still signs the user out when the retry 401 is bodiless (the real bearer-token challenge)', async () => {
    server.use(http.get('/api/thing', () => new HttpResponse(null, { status: 401 })));
    const refresh = vi.fn(async (): Promise<RefreshOutcome> => 'ok');
    const onAuthFailure = vi.fn();
    setAuthBridge({ getAccessToken: () => 'x', refresh, onAuthFailure });

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
  });

  it('never retries when skipAuthRetry is set', async () => {
    server.use(http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })));
    const refresh = vi.fn(async (): Promise<RefreshOutcome> => 'ok');
    setAuthBridge({ getAccessToken: () => null, refresh, onAuthFailure: () => {} });

    await expect(
      apiFetch('/api/auth/refresh', { method: 'POST', body: {}, skipAuthRetry: true }),
    ).rejects.toBeInstanceOf(ApiError);
    expect(refresh).not.toHaveBeenCalled();
  });
});
