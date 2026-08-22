import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../test/setup';
import { USERNAME_KEY, getAccessToken } from './session';
import { AuthProvider, useAuth } from './AuthContext';

function Probe() {
  const { status, role, username, signIn, signOut } = useAuth();
  return (
    <div>
      <span data-testid="status">{status}</span>
      <span data-testid="role">{role ?? 'none'}</span>
      <span data-testid="username">{username ?? 'none'}</span>
      <button onClick={() => void signIn('someone', 'whatever12A!')}>sign in</button>
      <button onClick={() => void signOut()}>sign out</button>
    </div>
  );
}

const renderProbe = () => render(<AuthProvider><Probe /></AuthProvider>);

beforeEach(() => localStorage.clear());

describe('AuthProvider boot', () => {
  it('lands anonymous when the refresh cookie is absent or rejected', async () => {
    // There is nothing readable to check first — the cookie is httpOnly — so
    // boot always asks the server, and a 401 is the anonymous answer.
    server.use(http.post('/api/auth/refresh', () => new HttpResponse(null, { status: 401 })));

    renderProbe();

    expect(await screen.findByTestId('status')).toHaveTextContent('anonymous');
  });

  it('hydrates the session from the refresh cookie', async () => {
    server.use(
      http.post('/api/auth/refresh', () => HttpResponse.json({
        accessToken: 'access-2', expiresAtUtc: '2099-01-01T00:00:00Z',
      })),
      http.get('/api/auth/me', () => HttpResponse.json({ sub: 'u', role: 'Admin', endUserId: 'e' })),
    );

    renderProbe();

    expect(await screen.findByText('authenticated')).toBeInTheDocument();
    expect(screen.getByTestId('role')).toHaveTextContent('Admin');
  });

  it('restores the persisted username on boot hydration', async () => {
    // /api/auth/me has no display name (sub is a GUID) — the username the
    // user typed at sign-in must survive a reload via USERNAME_KEY instead.
    localStorage.setItem(USERNAME_KEY, 'alice');
    server.use(
      http.post('/api/auth/refresh', () => HttpResponse.json({
        accessToken: 'access-2', expiresAtUtc: '2099-01-01T00:00:00Z',
      })),
      http.get('/api/auth/me', () => HttpResponse.json({ sub: 'u', role: 'Admin', endUserId: 'e' })),
    );

    renderProbe();

    expect(await screen.findByText('authenticated')).toBeInTheDocument();
    expect(screen.getByTestId('username')).toHaveTextContent('alice');
  });

  it('persists no credential in localStorage after a successful sign-in', async () => {
    server.use(
      http.post('/api/auth/refresh', () => HttpResponse.json({
        accessToken: 'access-2', expiresAtUtc: '2099-01-01T00:00:00Z',
      })),
      http.get('/api/auth/me', () => HttpResponse.json({ sub: 'u', role: 'Admin', endUserId: 'e' })),
    );

    renderProbe();
    await screen.findByText('authenticated');

    // The whole point of the cookie: nothing script can read holds a token.
    // Asserting over the WHOLE store rather than one known key, because a
    // regression would most likely reintroduce it under a new name.
    expect(JSON.stringify(localStorage)).not.toContain('access-2');
    expect(Object.keys(localStorage).every((k) => k === USERNAME_KEY || k === 'ju.theme')).toBe(true);
  });
});

describe('signIn', () => {
  // Regression for: AuthContext.signIn wrote the display username via a raw
  // localStorage.setItem, so a blocked/full store threw AFTER the tokens
  // were already saved — the promise rejected, status never reached
  // 'authenticated', and the user saw "Sign-in failed." despite valid
  // credentials. It must route through session's safe wrapper instead.
  it('reaches authenticated even when localStorage.setItem throws', async () => {
    server.use(
      http.post('/api/auth/login', () => HttpResponse.json({
        accessToken: 'access-1', expiresAtUtc: '2099-01-01T00:00:00Z',
      })),
      http.get('/api/auth/me', () => HttpResponse.json({ sub: 'u', role: 'Admin', endUserId: 'e' })),
    );
    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage disabled');
    });

    try {
      renderProbe();
      await screen.findByText('anonymous');

      await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

      expect(await screen.findByText('authenticated')).toBeInTheDocument();
      expect(screen.getByTestId('username')).toHaveTextContent('someone');
    } finally {
      setItemSpy.mockRestore();
    }
  });
});

describe('signOut', () => {
  it('clears the local session even when the logout call fails', async () => {
    server.use(
      http.post('/api/auth/refresh', () => HttpResponse.json({
        accessToken: 'access-2', expiresAtUtc: '2099-01-01T00:00:00Z',
      })),
      http.get('/api/auth/me', () => HttpResponse.json({ sub: 'u', role: null, endUserId: 'e' })),
      // The server refuses to revoke — the local session must end anyway.
      http.post('/api/auth/logout', () => new HttpResponse(null, { status: 500 })),
    );
    renderProbe();
    await screen.findByText('authenticated');

    await userEvent.click(screen.getByRole('button', { name: /sign out/i }));

    expect(await screen.findByText('anonymous')).toBeInTheDocument();
    expect(getAccessToken()).toBeNull();
  });
});
