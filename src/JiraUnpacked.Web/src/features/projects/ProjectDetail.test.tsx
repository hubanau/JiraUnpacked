import { render, screen, waitFor } from '@testing-library/react';
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
});
