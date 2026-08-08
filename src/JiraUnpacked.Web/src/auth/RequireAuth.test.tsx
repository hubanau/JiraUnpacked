import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { RequireAuth } from './RequireAuth';
import { useAuth } from './AuthContext';

// vi.mock, not vi.spyOn: ES module namespace objects are frozen, so spying on
// a live export is unreliable under Vitest. Mocking the module is the stable way.
vi.mock('./AuthContext', () => ({ useAuth: vi.fn() }));
const mockUseAuth = vi.mocked(useAuth);

function LoginProbe() {
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? 'none';
  return <div>login page (from: {from})</div>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/login" element={<LoginProbe />} />
        <Route element={<RequireAuth />}>
          <Route path="/secret" element={<div>secret content</div>} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

describe('RequireAuth', () => {
  it('renders a loading state while the session is being resolved', () => {
    mockUseAuth.mockReturnValue({
      status: 'loading', role: null, username: null,
      signIn: vi.fn(), signOut: vi.fn(),
    });

    renderAt('/secret');

    expect(screen.getByRole('status')).toBeInTheDocument();
    expect(screen.queryByText('secret content')).not.toBeInTheDocument();
  });

  it('redirects an anonymous visitor to /login preserving the attempted path', () => {
    mockUseAuth.mockReturnValue({
      status: 'anonymous', role: null, username: null,
      signIn: vi.fn(), signOut: vi.fn(),
    });

    renderAt('/secret');

    expect(screen.getByText(/login page \(from: \/secret\)/)).toBeInTheDocument();
  });

  it('renders the protected content when authenticated', () => {
    mockUseAuth.mockReturnValue({
      status: 'authenticated', role: 'EndUser', username: 'sam',
      signIn: vi.fn(), signOut: vi.fn(),
    });

    renderAt('/secret');

    expect(screen.getByText('secret content')).toBeInTheDocument();
  });
});
