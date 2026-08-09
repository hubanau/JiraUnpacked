import { render, screen } from '@testing-library/react';
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

describe('Home', () => {
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
});
