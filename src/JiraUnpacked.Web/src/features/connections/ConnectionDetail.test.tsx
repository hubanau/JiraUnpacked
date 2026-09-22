import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import ConnectionDetail from './ConnectionDetail';

const CONNECTION = {
  id: 'c1', baseUrl: 'https://acme.atlassian.net', email: 'ops@acme.com', createdAtUtc: '2026-01-01T00:00:00Z',
};

const project = (id: string, key: string, syncPaused = false) => ({
  id, jiraConnectionId: 'c1', jiraProjectKey: key, jiraProjectId: '1',
  syncIntervalMinutes: 60, syncPaused, createdAtUtc: '2026-01-01T00:00:00Z',
});

function renderConnectionDetail() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/connections/c1']}>
        <Routes>
          <Route path="/connections/:id" element={<ConnectionDetail />} />
          <Route path="/connections" element={<p>Connections list</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function useBaseHandlers(projects = [project('p1', 'ACME')]) {
  server.use(
    http.get('/api/connections/c1', () => HttpResponse.json(CONNECTION)),
    http.get('/api/connections/c1/projects', () => HttpResponse.json(projects)),
    http.get('/api/connections/c1/issue-types', () => HttpResponse.json([])),
  );
}

describe('ConnectionDetail — projects', () => {
  it('marks a paused project', async () => {
    useBaseHandlers([project('p1', 'ACME', true), project('p2', 'BETA')]);
    renderConnectionDetail();

    const acme = await screen.findByText('ACME');
    expect(acme).toHaveTextContent(/paused/i);
    expect(screen.getByText('BETA')).not.toHaveTextContent(/paused/i);
  });
});

describe('ConnectionDetail — update API token', () => {
  it('prefills the email and PATCHes the new token', async () => {
    useBaseHandlers();
    let body: unknown = null;
    server.use(http.patch('/api/connections/c1', async ({ request }) => {
      body = await request.json();
      return HttpResponse.json({ ...CONNECTION, email: 'new@acme.com' });
    }));
    const user = userEvent.setup();
    renderConnectionDetail();

    const form = await screen.findByRole('form', { name: /update api token/i });
    const email = within(form).getByLabelText(/email/i);
    expect(email).toHaveValue('ops@acme.com');
    const submit = within(form).getByRole('button', { name: /update token/i });
    expect(submit).toBeDisabled(); // nothing to send without a token

    await user.clear(email);
    await user.type(email, 'new@acme.com');
    await user.type(within(form).getByLabelText(/new api token/i), 'rotated');
    await user.click(submit);

    expect(await within(form).findByRole('status')).toHaveTextContent(/api token updated/i);
    expect(body).toEqual({ email: 'new@acme.com', apiToken: 'rotated' });
    expect(within(form).getByLabelText(/new api token/i)).toHaveValue('');
  });

  it('renders the token input as a password field', async () => {
    useBaseHandlers();
    renderConnectionDetail();

    expect(await screen.findByLabelText(/new api token/i)).toHaveAttribute('type', 'password');
  });

  it('shows the server’s reason when Jira rejects the new credentials', async () => {
    useBaseHandlers();
    server.use(http.patch('/api/connections/c1', () =>
      HttpResponse.json({ title: 'Jira credentials could not be validated.' }, { status: 400 })));
    const user = userEvent.setup();
    renderConnectionDetail();

    const form = await screen.findByRole('form', { name: /update api token/i });
    await user.type(within(form).getByLabelText(/new api token/i), 'wrong');
    await user.click(within(form).getByRole('button', { name: /update token/i }));

    expect(await within(form).findByRole('alert')).toHaveTextContent(/credentials could not be validated/i);
    expect(within(form).queryByRole('status')).not.toBeInTheDocument();
  });
});

describe('ConnectionDetail — delete', () => {
  it('asks for confirmation, sends nothing on cancel, and deletes then leaves on confirm', async () => {
    useBaseHandlers();
    let deletes = 0;
    server.use(http.delete('/api/connections/c1', () => {
      deletes += 1;
      return new HttpResponse(null, { status: 204 });
    }));
    const user = userEvent.setup();
    renderConnectionDetail();

    await user.click(await screen.findByRole('button', { name: /^delete connection$/i }));
    const dialog = screen.getByRole('dialog', { name: /delete this connection/i });
    expect(dialog).toHaveTextContent('https://acme.atlassian.net');
    expect(dialog).toHaveTextContent(/1 project/i);

    await user.click(within(dialog).getByRole('button', { name: /cancel/i }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(deletes).toBe(0);

    await user.click(screen.getByRole('button', { name: /^delete connection$/i }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: /yes, delete connection/i }));

    expect(await screen.findByText('Connections list')).toBeInTheDocument();
    expect(deletes).toBe(1);
  });

  it('keeps the dialog open and explains a 409 while a sync is running', async () => {
    useBaseHandlers();
    server.use(http.delete('/api/connections/c1', () =>
      HttpResponse.json({ title: 'A sync is running for one of this connection’s projects.' }, { status: 409 })));
    const user = userEvent.setup();
    renderConnectionDetail();

    await user.click(await screen.findByRole('button', { name: /^delete connection$/i }));
    const dialog = screen.getByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /yes, delete connection/i }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/sync is running/i);
    expect(screen.queryByText('Connections list')).not.toBeInTheDocument();
  });
});

describe('ConnectionDetail — waits for a real answer', () => {
  it('does not leave the page before the DELETE has answered', async () => {
    useBaseHandlers();
    let release: () => void = () => {};
    server.use(http.delete('/api/connections/c1', () =>
      new Promise<Response>((resolve) => {
        release = () => resolve(new HttpResponse(null, { status: 204 }));
      })));
    const user = userEvent.setup();
    renderConnectionDetail();

    await user.click(await screen.findByRole('button', { name: /^delete connection$/i }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: /yes, delete connection/i }));

    expect(screen.queryByText('Connections list')).not.toBeInTheDocument();
    release();
    await waitFor(() => expect(screen.getByText('Connections list')).toBeInTheDocument());
  });
});
