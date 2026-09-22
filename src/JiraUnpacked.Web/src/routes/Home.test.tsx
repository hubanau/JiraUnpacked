import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import Home from './Home';

function renderHome() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <Home />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const CONNECTION_1 = { id: 'c1', baseUrl: 'https://a.atlassian.net', email: 'a@b.c', createdAtUtc: '2026-01-01T00:00:00Z' };
const CONNECTION_2 = { id: 'c2', baseUrl: 'https://b.atlassian.net', email: 'd@e.f', createdAtUtc: '2026-01-01T00:00:00Z' };

const project = (id: string, connId: string, key: string, syncPaused = false) => ({
  id, jiraConnectionId: connId, jiraProjectKey: key, jiraProjectId: '1',
  syncIntervalMinutes: 60, syncPaused, createdAtUtc: '2026-01-01T00:00:00Z',
});

describe('Home', () => {
  it('offers Add project once projects already exist', async () => {
    // The gap this closes: the only link to the form was a connection's own detail page, and the
    // projects list linked to it in its EMPTY state only — so the action vanished from the
    // projects page the moment there was a project on it, which is when it is looked for.
    server.use(
      http.get('/api/connections', () => HttpResponse.json([CONNECTION_1])),
      http.get('/api/connections/c1/projects', () => HttpResponse.json([project('p1', 'c1', 'ACME')])),
    );

    renderHome();
    await screen.findByText('ACME');

    // One connection: the choice is already made, so no picker is put in the way.
    expect(screen.getByRole('link', { name: /add project/i }))
      .toHaveAttribute('href', '/connections/c1/projects/new');
    expect(screen.queryByLabelText(/connection to add/i)).not.toBeInTheDocument();
  });

  it('asks which connection when there is more than one', async () => {
    // A project is added TO a connection and there is no defensible default among several, so
    // the choice is offered rather than guessed at.
    server.use(
      http.get('/api/connections', () => HttpResponse.json([CONNECTION_1, CONNECTION_2])),
      http.get('/api/connections/c1/projects', () => HttpResponse.json([project('p1', 'c1', 'ACME')])),
      http.get('/api/connections/c2/projects', () => HttpResponse.json([])),
    );

    renderHome();
    await screen.findByText('ACME');

    const picker = screen.getByLabelText(/connection to add/i);
    expect(screen.getByRole('link', { name: /add project/i }))
      .toHaveAttribute('href', '/connections/c1/projects/new');

    await userEvent.selectOptions(picker, 'c2');
    expect(screen.getByRole('link', { name: /add project/i }))
      .toHaveAttribute('href', '/connections/c2/projects/new');
  });

  it('fans out one projects query per connection and flattens the results', async () => {
    server.use(
      http.get('/api/connections', () => HttpResponse.json([CONNECTION_1, CONNECTION_2])),
      http.get('/api/connections/c1/projects', () => HttpResponse.json([
        { id: 'p1', jiraConnectionId: 'c1', jiraProjectKey: 'ACME', jiraProjectId: '1', syncIntervalMinutes: 60, createdAtUtc: '2026-01-01T00:00:00Z' },
      ])),
      http.get('/api/connections/c2/projects', () => HttpResponse.json([
        { id: 'p2', jiraConnectionId: 'c2', jiraProjectKey: 'OPS', jiraProjectId: '2', syncIntervalMinutes: 120, createdAtUtc: '2026-01-01T00:00:00Z' },
      ])),
    );

    renderHome();

    expect(await screen.findByText('ACME')).toBeInTheDocument();
    expect(await screen.findByText('OPS')).toBeInTheDocument();
  });

  it('shows "No connections yet" when there are no connections at all', async () => {
    server.use(http.get('/api/connections', () => HttpResponse.json([])));

    renderHome();

    expect(await screen.findByText(/no connections yet/i)).toBeInTheDocument();
    expect(screen.queryByText(/no projects yet/i)).not.toBeInTheDocument();
  });

  it('shows "No projects yet" (distinct from the no-connections state) when a connection has no projects', async () => {
    server.use(
      http.get('/api/connections', () => HttpResponse.json([CONNECTION_1])),
      http.get('/api/connections/c1/projects', () => HttpResponse.json([])),
    );

    renderHome();

    expect(await screen.findByText(/no projects yet/i)).toBeInTheDocument();
    expect(screen.queryByText(/no connections yet/i)).not.toBeInTheDocument();
  });

  it('links each project to its issue list', async () => {
    server.use(
      http.get('/api/connections', () => HttpResponse.json([CONNECTION_1])),
      http.get('/api/connections/c1/projects', () => HttpResponse.json([
        { id: 'p1', jiraConnectionId: 'c1', jiraProjectKey: 'ACME', jiraProjectId: '1', syncIntervalMinutes: 60, createdAtUtc: '2026-01-01T00:00:00Z' },
      ])),
    );

    renderHome();

    expect(await screen.findByRole('link', { name: /issues/i }))
      .toHaveAttribute('href', '/projects/p1/issues');
  });

  it('marks projects whose scheduled sync is paused', async () => {
    server.use(
      http.get('/api/connections', () => HttpResponse.json([CONNECTION_1])),
      http.get('/api/connections/c1/projects', () => HttpResponse.json([
        project('p1', 'c1', 'ACME', true),
        project('p2', 'c1', 'BETA'),
      ])),
    );

    renderHome();

    const paused = await screen.findByText('ACME');
    expect(paused.closest('tr')).toHaveTextContent(/paused/i);
    expect(screen.getByText('BETA').closest('tr')).not.toHaveTextContent(/paused/i);
  });
});
