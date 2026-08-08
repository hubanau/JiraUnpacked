import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../test/setup';
import { ApiError } from './errors';
import { apiFetch, setAuthBridge } from './client';

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
    setAuthBridge({ getAccessToken: () => 'tok-1', refresh: async () => false, onAuthFailure: () => {} });

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
    const refresh = vi.fn(async () => { token = 'good'; return true; });
    setAuthBridge({ getAccessToken: () => token, refresh, onAuthFailure: () => {} });

    await expect(apiFetch('/api/thing')).resolves.toEqual({ ok: true });
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(calls).toBe(2);
  });

  it('gives up and signals auth failure when the refresh fails', async () => {
    server.use(http.get('/api/thing', () => new HttpResponse(null, { status: 401 })));
    const onAuthFailure = vi.fn();
    setAuthBridge({ getAccessToken: () => 'stale', refresh: async () => false, onAuthFailure });

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
  });

  it('does not loop when the retry also 401s', async () => {
    let calls = 0;
    server.use(http.get('/api/thing', () => { calls++; return new HttpResponse(null, { status: 401 }); }));
    const refresh = vi.fn(async () => true);
    const onAuthFailure = vi.fn();
    setAuthBridge({ getAccessToken: () => 'x', refresh, onAuthFailure });

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(ApiError);
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(calls).toBe(2); // original + exactly one retry
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
  });

  it('never retries when skipAuthRetry is set', async () => {
    server.use(http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })));
    const refresh = vi.fn(async () => true);
    setAuthBridge({ getAccessToken: () => null, refresh, onAuthFailure: () => {} });

    await expect(
      apiFetch('/api/auth/refresh', { method: 'POST', body: {}, skipAuthRetry: true }),
    ).rejects.toBeInstanceOf(ApiError);
    expect(refresh).not.toHaveBeenCalled();
  });
});
