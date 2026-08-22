import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../test/setup';
import { apiFetch, setAuthBridge } from '../api/client';
import { clearTokens, getAccessToken, installAuthBridge, refreshOnce, setTokens } from './session';

const token = (n: number) => ({
  accessToken: `access-${n}`, expiresAtUtc: '2099-01-01T00:00:00Z',
});

beforeEach(() => {
  clearTokens();
  setAuthBridge(null);
  localStorage.clear();
});

describe('token storage', () => {
  it('keeps the access token in memory and persists NO credential at all', () => {
    setTokens(token(1));

    expect(getAccessToken()).toBe('access-1');
    // Neither token may be persisted. The refresh token now lives in an httpOnly
    // cookie the browser owns, so there is nothing for this module to store —
    // scanning the whole of localStorage catches a regression under ANY key,
    // which an assertion on one known key could not.
    expect(JSON.stringify(localStorage)).not.toContain('access-1');
    expect(localStorage.length).toBe(0);
  });

  it('clears the access token on clearTokens', () => {
    setTokens(token(1));
    clearTokens();

    expect(getAccessToken()).toBeNull();
  });
});

describe('refreshOnce', () => {
  it('stores the rotated access token on success', async () => {
    server.use(http.post('/api/auth/refresh', () => HttpResponse.json(token(2))));

    await expect(refreshOnce()).resolves.toBe(true);

    expect(getAccessToken()).toBe('access-2');
    // The rotated refresh token arrives as a Set-Cookie the browser swaps in —
    // it must not land anywhere this module can see.
    expect(localStorage.length).toBe(0);
  });

  it('sends no request body: the token travels in the cookie', async () => {
    let body: string | null = null;
    server.use(http.post('/api/auth/refresh', async ({ request }) => {
      body = await request.text();
      return HttpResponse.json(token(2));
    }));

    await refreshOnce();

    expect(body).toBe('');
  });

  it('sends the X-Requested-With header the server requires', async () => {
    let header: string | null = null;
    server.use(http.post('/api/auth/refresh', ({ request }) => {
      header = request.headers.get('x-requested-with');
      return HttpResponse.json(token(2));
    }));

    await refreshOnce();

    // Second CSRF layer behind SameSite=Strict; without it the server answers 400.
    expect(header).toBe('XMLHttpRequest');
  });

  it('is single-flight: concurrent callers share ONE network refresh', async () => {
    let hits = 0;
    server.use(http.post('/api/auth/refresh', async () => {
      hits++;
      await new Promise((r) => setTimeout(r, 20));
      return HttpResponse.json(token(2));
    }));

    const results = await Promise.all([refreshOnce(), refreshOnce(), refreshOnce()]);

    expect(results).toEqual([true, true, true]);
    // Refresh rotates server-side, so N parallel calls would consume the token N
    // times and all but the first would fail.
    expect(hits).toBe(1);
  });

  it('allows a fresh refresh after the previous one settles', async () => {
    let hits = 0;
    server.use(http.post('/api/auth/refresh', () => { hits++; return HttpResponse.json(token(2)); }));

    await refreshOnce();
    await refreshOnce();

    expect(hits).toBe(2);
  });

  it('returns false and clears the access token when the refresh is rejected', async () => {
    server.use(http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })));

    await expect(refreshOnce()).resolves.toBe(false);

    expect(getAccessToken()).toBeNull();
  });

  it('resolves false rather than throwing when the refresh call fails outright', async () => {
    server.use(http.post('/api/auth/refresh', () => HttpResponse.error()));

    await expect(refreshOnce()).resolves.toBe(false);
    expect(getAccessToken()).toBeNull();
  });
});

describe('installAuthBridge', () => {
  it('makes N concurrent 401s trigger exactly ONE refresh', async () => {
    setTokens(token(1));

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
