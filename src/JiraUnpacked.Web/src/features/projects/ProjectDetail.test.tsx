import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import ProjectDetail from './ProjectDetail';

const PROJECT = {
  id: 'p1',
  jiraConnectionId: 'c1',
  jiraProjectKey: 'ACME',
  jiraProjectId: '100',
  syncIntervalMinutes: 60,
  syncPaused: false,
  createdAtUtc: '2026-01-01T00:00:00Z',
};

function renderProjectDetail() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1']}>
        <Routes>
          <Route path="/projects/:id" element={<ProjectDetail />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

// Readiness inputs are irrelevant to the interval field, so keep them
// minimal — just enough for every query ProjectDetail fires unconditionally
// (Rules of Hooks) to resolve.
function useBaseHandlers() {
  server.use(
    http.get('/api/projects/p1', () => HttpResponse.json(PROJECT)),
    http.get('/api/projects/p1/field-mappings', () => HttpResponse.json([])),
    http.get('/api/projects/p1/jira-statuses', () => HttpResponse.json([])),
    http.get('/api/projects/p1/status-semantics', () => HttpResponse.json([])),
    http.get('/api/connections/c1/issue-types', () => HttpResponse.json([])),
    http.get('/api/projects/p1/sync-runs', () => HttpResponse.json([])),
  );
}

describe('ProjectDetail sync interval', () => {
  // This wiring (client-side min check -> disabled Save -> PATCH on submit)
  // is duplicated independently of AddProject's already-tested version, so
  // an inverted condition here would otherwise go uncaught.
  it('disables Save below the minimum and PATCHes the interval once it is valid', async () => {
    useBaseHandlers();
    let body: unknown = null;
    server.use(http.patch('/api/projects/p1', async ({ request }) => {
      body = await request.json();
      return new HttpResponse(null, { status: 204 });
    }));
    const user = userEvent.setup();
    renderProjectDetail();

    const interval = await screen.findByLabelText(/sync interval/i);
    const save = screen.getByRole('button', { name: /save interval/i });

    await user.clear(interval);
    await user.type(interval, '59');
    expect(save).toBeDisabled();

    await user.clear(interval);
    await user.type(interval, '120');
    expect(save).toBeEnabled();

    await user.click(save);

    await waitFor(() => {
      expect(body).toEqual({ syncIntervalMinutes: 120 });
    });
  });

  it('offers a link to the project’s issue list', async () => {
    useBaseHandlers();
    renderProjectDetail();

    expect(await screen.findByRole('link', { name: /browse issues/i }))
      .toHaveAttribute('href', '/projects/p1/issues');
  });

  it('links to the project’s metrics and checks', async () => {
    useBaseHandlers();
    renderProjectDetail();

    expect(await screen.findByRole('link', { name: /metrics/i }))
      .toHaveAttribute('href', '/projects/p1/metrics');
    expect(screen.getByRole('link', { name: /checks/i }))
      .toHaveAttribute('href', '/projects/p1/checks');
  });
});

function renderWithConnectionRoute() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1']}>
        <Routes>
          <Route path="/projects/:id" element={<ProjectDetail />} />
          <Route path="/connections/:id" element={<p>Connection page</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('ProjectDetail pause / resume', () => {
  it('pauses the scheduled sync and then offers to resume it', async () => {
    let current = { ...PROJECT };
    let body: unknown = null;
    useBaseHandlers();
    server.use(
      http.get('/api/projects/p1', () => HttpResponse.json(current)),
      http.patch('/api/projects/p1', async ({ request }) => {
        body = await request.json();
        current = { ...current, syncPaused: true };
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    renderProjectDetail();

    expect(screen.queryByText('Paused')).not.toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: /pause scheduled sync/i }));

    expect(await screen.findByRole('button', { name: /resume scheduled sync/i })).toBeInTheDocument();
    expect(body).toEqual({ syncPaused: true });
    expect(screen.getByText('Paused')).toBeInTheDocument();
    expect(screen.getByText(/scheduled sync is paused/i)).toBeInTheDocument();
  });

  it('resumes a paused project', async () => {
    let body: unknown = null;
    useBaseHandlers();
    server.use(
      http.get('/api/projects/p1', () => HttpResponse.json({ ...PROJECT, syncPaused: true })),
      http.patch('/api/projects/p1', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    renderProjectDetail();

    await user.click(await screen.findByRole('button', { name: /resume scheduled sync/i }));

    await waitFor(() => expect(body).toEqual({ syncPaused: false }));
  });
});

describe('ProjectDetail delete', () => {
  it('asks for confirmation, sends nothing on cancel, and returns to the connection on confirm', async () => {
    let deletes = 0;
    useBaseHandlers();
    server.use(http.delete('/api/projects/p1', () => {
      deletes += 1;
      return new HttpResponse(null, { status: 204 });
    }));
    const user = userEvent.setup();
    renderWithConnectionRoute();

    await user.click(await screen.findByRole('button', { name: /^delete project$/i }));
    const dialog = screen.getByRole('dialog', { name: /delete acme/i });
    await user.click(within(dialog).getByRole('button', { name: /cancel/i }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(deletes).toBe(0);

    await user.click(screen.getByRole('button', { name: /^delete project$/i }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: /yes, delete project/i }));

    expect(await screen.findByText('Connection page')).toBeInTheDocument();
    expect(deletes).toBe(1);
  });

  it('explains a 409 inside the dialog and stays on the project', async () => {
    useBaseHandlers();
    server.use(http.delete('/api/projects/p1', () =>
      HttpResponse.json({ title: 'A sync is running for this project.' }, { status: 409 })));
    const user = userEvent.setup();
    renderWithConnectionRoute();

    await user.click(await screen.findByRole('button', { name: /^delete project$/i }));
    const dialog = screen.getByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /yes, delete project/i }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/sync is running/i);
    expect(screen.queryByText('Connection page')).not.toBeInTheDocument();
  });
});
