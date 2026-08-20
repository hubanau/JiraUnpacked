import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { SideNav } from './SideNav';
import { useAuth } from '../auth/AuthContext';

// vi.mock, not vi.spyOn: ES module namespace objects are frozen, so spying on
// a live export is unreliable under Vitest. Mocking the module is the stable way.
vi.mock('../auth/AuthContext', () => ({ useAuth: vi.fn() }));
const mockUseAuth = vi.mocked(useAuth);

function renderNav({ role }: { role: string | null }) {
  mockUseAuth.mockReturnValue({
    status: 'authenticated', role, username: 'sam',
    signIn: vi.fn(), signOut: vi.fn(),
  });

  return render(
    <MemoryRouter>
      <SideNav />
    </MemoryRouter>,
  );
}

describe('SideNav admin entry', () => {
  it('shows Admin to an admin', () => {
    renderNav({ role: 'Admin' });

    expect(screen.getByRole('link', { name: /admin/i })).toHaveAttribute('href', '/admin');
  });

  it('hides Admin from a non-admin', () => {
    renderNav({ role: 'EndUser' });

    expect(screen.queryByRole('link', { name: /admin/i })).not.toBeInTheDocument();
  });
});
