import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../test/setup';
import AdminPage from './AdminPage';
import { useAuth } from '../../auth/AuthContext';

// vi.mock, not vi.spyOn: ES module namespace objects are frozen, so spying on
// a live export is unreliable under Vitest. Mocking the module is the stable way.
vi.mock('../../auth/AuthContext', () => ({ useAuth: vi.fn() }));
const mockUseAuth = vi.mocked(useAuth);

function renderAdmin({ role }: { role: string }) {
  mockUseAuth.mockReturnValue({
    status: 'authenticated',
    role,
    username: 'admin',
    signIn: vi.fn(),
    signOut: vi.fn(),
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <AdminPage />
    </QueryClientProvider>,
  );
}

describe('AdminPage', () => {
  it('shows the generated code and warns it cannot be retrieved again', async () => {
    server.use(http.post('/api/admin/invite-codes', () =>
      HttpResponse.json({ code: 'ABC123', expiresAtUtc: '2026-09-01T00:00:00Z' })));
    renderAdmin({ role: 'Admin' });

    await userEvent.click(screen.getByRole('button', { name: /generate/i }));

    expect(await screen.findByText('ABC123')).toBeInTheDocument();
    expect(screen.getByText(/cannot be (retrieved|shown) again/i)).toBeInTheDocument();
  });

  it('keeps delete disabled until DELETE is typed', async () => {
    renderAdmin({ role: 'Admin' });
    await userEvent.type(screen.getByLabelText(/user id/i), 'u1');
    await userEvent.click(screen.getByRole('button', { name: /delete user/i }));

    const confirm = await screen.findByRole('button', { name: /^delete$/i });
    expect(confirm).toBeDisabled();

    await userEvent.type(screen.getByLabelText(/type delete/i), 'DELETE');
    expect(confirm).toBeEnabled();
  });

  it('resets the confirmation when the dialog is reopened', async () => {
    const user = userEvent.setup();
    renderAdmin({ role: 'Admin' });
    await user.type(screen.getByLabelText(/user id/i), 'u1');

    await user.click(screen.getByRole('button', { name: /delete user/i }));
    await user.type(await screen.findByLabelText(/type delete/i), 'DELETE');
    expect(screen.getByRole('button', { name: /^delete$/i })).toBeEnabled();

    await user.keyboard('{Escape}');
    await user.click(screen.getByRole('button', { name: /delete user/i }));

    // Spec 1 shipped exactly this bug: a lingering dialog reopened pre-filled
    // with the confirm button already enabled, so one stray click could erase
    // an account. Conditional mounting is what makes this pass.
    expect(await screen.findByLabelText(/type delete/i)).toHaveValue('');
    expect(screen.getByRole('button', { name: /^delete$/i })).toBeDisabled();
  });

  it('surfaces a 403 rather than assuming the client role check was sufficient', async () => {
    server.use(http.post('/api/admin/users/u1/block', () =>
      HttpResponse.json({ title: 'Forbidden' }, { status: 403 })));
    renderAdmin({ role: 'Admin' });

    await userEvent.type(screen.getByLabelText(/user id/i), 'u1');
    await userEvent.click(screen.getByRole('button', { name: /block/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/forbidden/i);
  });

  it('does not render admin actions for a non-admin', () => {
    renderAdmin({ role: 'EndUser' });
    expect(screen.queryByRole('button', { name: /generate/i })).not.toBeInTheDocument();
  });
});
