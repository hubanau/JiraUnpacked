import { render, screen, waitFor } from '@testing-library/react';
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

  it('clears a block result when the User ID changes, before it can be mistaken for someone else', async () => {
    server.use(http.post('/api/admin/users/u1/block', () => new HttpResponse(null, { status: 204 })));
    renderAdmin({ role: 'Admin' });

    const userIdInput = screen.getByLabelText(/user id/i);
    await userEvent.type(userIdInput, 'u1');
    await userEvent.click(screen.getByRole('button', { name: /^block user$/i }));

    expect(await screen.findByText(/blocked/i)).toBeInTheDocument();

    // Editing the id — not firing any new action — must clear the stale
    // result immediately. Before the reset effect existed, this message
    // survived a change to the id field, so blocking u1 then pasting u2 and
    // clicking Restore access showed "User blocked." and "Access restored."
    // at once, naming neither user.
    await userEvent.clear(userIdInput);
    await userEvent.type(userIdInput, 'u2');

    expect(screen.queryByText(/blocked/i)).not.toBeInTheDocument();
  });

  it('does not resurface a block result for an id the field no longer shows', async () => {
    // A manually-resolved response, not a wall-clock delay: the request is
    // held open until resolveBlock() is called below, after the id field
    // has already moved on to u2 — deterministically reproducing the race
    // where an in-flight request (which reset() cannot cancel) settles
    // after the admin has stopped looking at the id it was sent for.
    let resolveBlock: () => void = () => {};
    const blockResponse = new Promise<void>((resolve) => {
      resolveBlock = resolve;
    });
    server.use(http.post('/api/admin/users/u1/block', async () => {
      await blockResponse;
      return new HttpResponse(null, { status: 204 });
    }));
    renderAdmin({ role: 'Admin' });

    const userIdInput = screen.getByLabelText(/user id/i);
    const blockButton = screen.getByRole('button', { name: /^block user$/i });
    await userEvent.type(userIdInput, 'u1');
    await userEvent.click(blockButton);

    await userEvent.clear(userIdInput);
    await userEvent.type(userIdInput, 'u2');

    resolveBlock();
    // aria-busy is driven by isPending alone (not gated on `variables`), so
    // waiting for it to clear proves the delayed response has actually been
    // processed, without relying on wall-clock timing.
    await waitFor(() => expect(blockButton).not.toHaveAttribute('aria-busy'));

    expect(screen.queryByText(/blocked/i)).not.toBeInTheDocument();
  });

  it('shows a visible confirmation naming the deleted user, not a silent close', async () => {
    server.use(http.delete('/api/admin/users/u1', () => new HttpResponse(null, { status: 204 })));
    renderAdmin({ role: 'Admin' });

    await userEvent.type(screen.getByLabelText(/user id/i), 'u1');
    await userEvent.click(screen.getByRole('button', { name: /delete user/i }));
    await userEvent.type(await screen.findByLabelText(/type delete/i), 'DELETE');
    await userEvent.click(screen.getByRole('button', { name: /^delete$/i }));

    expect(await screen.findByText(/user u1 deleted/i)).toBeInTheDocument();
    // The dialog itself is gone too — deletion succeeded and closed it,
    // rather than the admin having to infer success from the modal closing.
    expect(screen.queryByLabelText(/type delete/i)).not.toBeInTheDocument();
  });

  it('does not render admin actions for a non-admin', () => {
    renderAdmin({ role: 'EndUser' });
    expect(screen.queryByRole('button', { name: /generate/i })).not.toBeInTheDocument();
  });
});
