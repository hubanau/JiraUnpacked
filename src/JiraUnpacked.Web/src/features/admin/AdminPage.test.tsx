import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../test/setup';
import AdminPage from './AdminPage';
import { useAuth } from '../../auth/AuthContext';
import type { AdminUserListItem } from '../../api/admin';

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

const user1: AdminUserListItem = {
  id: 'u1',
  email: 'user1@example.com',
  username: 'user1',
  role: 'EndUser',
  status: 'Active',
  createdAt: '2026-01-01T00:00:00Z',
};

const user2: AdminUserListItem = {
  id: 'u2',
  email: 'user2@example.com',
  username: 'user2',
  role: 'EndUser',
  status: 'Active',
  createdAt: '2026-01-02T00:00:00Z',
};

// The Admin panel now fetches the list on mount, so — with the harness set
// to onUnhandledRequest: 'error' — every test rendering as an Admin must
// mock this, even when the test's focus is elsewhere.
function usersListHandler(items: AdminUserListItem[]) {
  return http.get('/api/admin/users', () =>
    HttpResponse.json({ items, page: 1, pageSize: 50, totalCount: items.length }),
  );
}

describe('AdminPage', () => {
  it('shows the generated code and warns it cannot be retrieved again', async () => {
    server.use(
      usersListHandler([]),
      http.post('/api/admin/invite-codes', () =>
        HttpResponse.json({ code: 'ABC123', expiresAtUtc: '2026-09-01T00:00:00Z' })),
    );
    renderAdmin({ role: 'Admin' });

    await userEvent.click(screen.getByRole('button', { name: /generate/i }));

    expect(await screen.findByText('ABC123')).toBeInTheDocument();
    expect(screen.getByText(/cannot be (retrieved|shown) again/i)).toBeInTheDocument();
  });

  it('renders the fetched users in a table, with role and status shown', async () => {
    server.use(usersListHandler([user1, user2]));
    renderAdmin({ role: 'Admin' });

    expect(await screen.findByText('user1')).toBeInTheDocument();
    expect(screen.getByText('user1@example.com')).toBeInTheDocument();
    expect(screen.getByText('user2')).toBeInTheDocument();
    // role/status are shown rather than hidden, since block/unblock reject
    // non-End-Users server-side with a typed error rather than the client
    // pre-filtering them out of the list.
    expect(screen.getAllByText('EndUser').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Active').length).toBeGreaterThan(0);
  });

  it('keeps delete disabled until DELETE is typed', async () => {
    server.use(usersListHandler([user1]));
    renderAdmin({ role: 'Admin' });

    await userEvent.click(await screen.findByRole('button', { name: /delete user/i }));

    const confirm = await screen.findByRole('button', { name: /^delete$/i });
    expect(confirm).toBeDisabled();

    await userEvent.type(screen.getByLabelText(/type delete/i), 'DELETE');
    expect(confirm).toBeEnabled();
  });

  it('resets the confirmation when the dialog is reopened', async () => {
    const user = userEvent.setup();
    server.use(usersListHandler([user1]));
    renderAdmin({ role: 'Admin' });

    const deleteUserButton = await screen.findByRole('button', { name: /delete user/i });
    await user.click(deleteUserButton);
    await user.type(await screen.findByLabelText(/type delete/i), 'DELETE');
    expect(screen.getByRole('button', { name: /^delete$/i })).toBeEnabled();

    await user.keyboard('{Escape}');
    await user.click(deleteUserButton);

    // Spec 1 shipped exactly this bug: a lingering dialog reopened pre-filled
    // with the confirm button already enabled, so one stray click could erase
    // an account. Conditional mounting is what makes this pass.
    expect(await screen.findByLabelText(/type delete/i)).toHaveValue('');
    expect(screen.getByRole('button', { name: /^delete$/i })).toBeDisabled();
  });

  it('surfaces a 403 rather than assuming the client role check was sufficient', async () => {
    server.use(
      usersListHandler([user1]),
      http.post('/api/admin/users/u1/block', () =>
        HttpResponse.json({ title: 'Forbidden' }, { status: 403 })),
    );
    renderAdmin({ role: 'Admin' });

    await userEvent.click(await screen.findByRole('button', { name: /^block$/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/forbidden/i);
  });

  it('a row action targets only that row\'s id', async () => {
    server.use(
      usersListHandler([user1, user2]),
      http.post('/api/admin/users/u2/block', () => new HttpResponse(null, { status: 204 })),
      // No handler for u1's block: if the click were misrouted to user1,
      // onUnhandledRequest: 'error' would fail this test.
    );
    renderAdmin({ role: 'Admin' });

    await screen.findByText('user1');
    const rows = screen.getAllByRole('row');
    // rows[0] is the header row; user1 is rows[1], user2 is rows[2].
    const secondRowBlock = within(rows[2]).getByRole('button', { name: /^block$/i });
    await userEvent.click(secondRowBlock);

    expect(await screen.findByText(/user2 blocked/i)).toBeInTheDocument();
    expect(screen.queryByText(/user1 blocked/i)).not.toBeInTheDocument();
  });

  it("does not let one row's result leak onto another", async () => {
    let resolveBlockU1: () => void = () => {};
    const blockU1Response = new Promise<void>((resolve) => {
      resolveBlockU1 = resolve;
    });
    server.use(
      usersListHandler([user1, user2]),
      // Held open until resolveBlockU1() is called below, after the admin
      // has already acted on user2 — deterministically reproducing the race
      // where an in-flight request for one row settles after the admin has
      // moved on to another.
      http.post('/api/admin/users/u1/block', async () => {
        await blockU1Response;
        return new HttpResponse(null, { status: 204 });
      }),
      http.post('/api/admin/users/u2/block', () => new HttpResponse(null, { status: 204 })),
    );
    renderAdmin({ role: 'Admin' });

    await screen.findByText('user1');
    const blockButtons = screen.getAllByRole('button', { name: /^block$/i });

    await userEvent.click(blockButtons[0]); // block user1 — left pending
    await userEvent.click(blockButtons[1]); // block user2 — resolves immediately

    expect(await screen.findByText(/user2 blocked/i)).toBeInTheDocument();
    expect(screen.queryByText(/user1 blocked/i)).not.toBeInTheDocument();

    resolveBlockU1();
    await waitFor(() => expect(blockButtons[0]).not.toHaveAttribute('aria-busy'));

    // user1's request has now actually resolved server-side, but the most
    // recent action the admin took was against user2 — user1's row must
    // still never show a result that belongs to a different row.
    expect(screen.queryByText(/user1 blocked/i)).not.toBeInTheDocument();
    expect(screen.getByText(/user2 blocked/i)).toBeInTheDocument();
  });

  it('shows a visible confirmation naming the deleted user, not a silent close', async () => {
    server.use(
      usersListHandler([user1]),
      http.delete('/api/admin/users/u1', () => new HttpResponse(null, { status: 204 })),
    );
    renderAdmin({ role: 'Admin' });

    await userEvent.click(await screen.findByRole('button', { name: /delete user/i }));
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
