import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../test/setup';
import { apiFetch, setAuthBridge } from '../api/client';
import {
  REFRESH_KEY, clearTokens, getAccessToken, getRefreshToken,
  installAuthBridge, refreshOnce, setTokens,
} from './session';

const token = (n: number) => ({
  accessToken: `access-${n}`, expiresAtUtc: '2099-01-01T00:00:00Z', refreshToken: `refresh-${n}`,
});

beforeEach(() => {
  clearTokens();
  setAuthBridge(null);
  localStorage.clear();
});

describe('token storage', () => {
  it('keeps the access token in memory and the refresh token in localStorage', () => {
    setTokens(token(1));

    expect(getAccessToken()).toBe('access-1');
    expect(getRefreshToken()).toBe('refresh-1');
    // The access token must never be persisted.
    expect(JSON.stringify(localStorage)).not.toContain('access-1');
  });

  it('clears both on clearTokens', () => {
    setTokens(token(1));
    clearTokens();

    expect(getAccessToken()).toBeNull();
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull();
  });
});

describe('refreshOnce', () => {
  it('stores the rotated pair on success', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    server.use(http.post('/api/auth/refresh', () => HttpResponse.json(token(2))));

    await expect(refreshOnce()).resolves.toBe(true);

    expect(getAccessToken()).toBe('access-2');
    expect(getRefreshToken()).toBe('refresh-2');
  });

  it('is single-flight: concurrent callers share ONE network refresh', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    let hits = 0;
    server.use(http.post('/api/auth/refresh', async () => {
      hits++;
      await new Promise((r) => setTimeout(r, 20));
      return HttpResponse.json(token(2));
    }));

    const results = await Promise.all([refreshOnce(), refreshOnce(), refreshOnce()]);

    expect(results).toEqual([true, true, true]);
    expect(hits).toBe(1);
  });

  it('allows a fresh refresh after the previous one settles', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    let hits = 0;
    server.use(http.post('/api/auth/refresh', () => { hits++; return HttpResponse.json(token(2)); }));

    await refreshOnce();
    await refreshOnce();

    expect(hits).toBe(2);
  });

  it('returns false and clears tokens when the refresh is rejected', async () => {
    localStorage.setItem(REFRESH_KEY, 'expired');
    server.use(http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })));

    await expect(refreshOnce()).resolves.toBe(false);

    expect(getAccessToken()).toBeNull();
    expect(getRefreshToken()).toBeNull();
  });

  it('returns false without a network call when no refresh token is stored', async () => {
    let hits = 0;
    server.use(http.post('/api/auth/refresh', () => { hits++; return HttpResponse.json(token(2)); }));

    await expect(refreshOnce()).resolves.toBe(false);

    expect(hits).toBe(0);
  });

  it('resolves false without throwing when localStorage access fails', async () => {
    const getItemSpy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('storage disabled');
    });

    try {
      await expect(refreshOnce()).resolves.toBe(false);
    } finally {
      getItemSpy.mockRestore();
    }
  });
});

describe('installAuthBridge', () => {
  it('makes N concurrent 401s trigger exactly ONE refresh', async () => {
    localStorage.setItem(REFRESH_KEY, 'refresh-1');
    setTokens({ ...token(1), refreshToken: 'refresh-1' });

    let refreshHits = 0;
    server.use(
      http.post('/api/auth/refresh', async () => {
        refreshHits++;
        await new Promise((r) => setTimeout(r, 20));
        return HttpResponse.json(token(9));
      }),
      http.get('/api/thing', ({ request }) =>
        request.headers.get('authorization') === 'Bearer access-9'
          ? HttpResponse.json({ ok: true })
          : new HttpResponse(null, { status: 401 })),
    );

    installAuthBridge(() => {});

    const all = await Promise.all([
      apiFetch('/api/thing'), apiFetch('/api/thing'), apiFetch('/api/thing'),
    ]);

    expect(all).toEqual([{ ok: true }, { ok: true }, { ok: true }]);
    expect(refreshHits).toBe(1);
  });

  it('calls onAuthFailure when the refresh cannot succeed', async () => {
    setTokens(token(1));
    const onAuthFailure = vi.fn();
    server.use(
      http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })),
      http.get('/api/thing', () => new HttpResponse(null, { status: 401 })),
    );

    installAuthBridge(onAuthFailure);

    await expect(apiFetch('/api/thing')).rejects.toBeInstanceOf(Error);
    expect(onAuthFailure).toHaveBeenCalledTimes(1);
    expect(getAccessToken()).toBeNull();
  });
});
