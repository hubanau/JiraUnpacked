import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import IssueList, { IssueListRoute } from './IssueList';
import type { FilterCondition, IssueListItem } from '../../api/issues';
import { MAX_PAGE_SIZE } from '../../api/issues';

function renderList({ initialConditions }: { initialConditions?: FilterCondition[] } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1/issues']}>
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList initialConditions={initialConditions} />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

// Drives a same-app navigation between two projects' issue lists, the way a
// real project switcher (or EX5's Home/ProjectDetail links) eventually will,
// so the regression test below exercises the actual route wiring rather
// than a contrived remount.
function NavigateButton({ to }: { to: string }) {
  const navigate = useNavigate();
  return (
    <button type="button" onClick={() => navigate(to)}>
      switch project
    </button>
  );
}

function renderRoute() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1/issues']}>
        <NavigateButton to="/projects/p2/issues" />
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueListRoute />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function row(jiraKey: string, overrides: Partial<IssueListItem> = {}): IssueListItem {
  return {
    jiraIssueId: jiraKey,
    jiraKey,
    summary: 'Some summary',
    status: 'Open',
    issueType: 'Story',
    priority: 'Medium',
    assignee: 'Alice',
    sprint: 'Sprint 1',
    team: 'Team A',
    storyPoints: 3,
    parentJiraIssueId: null,
    jiraCreatedAt: '2026-01-01T00:00:00Z',
    jiraUpdatedAt: '2026-01-02T00:00:00Z',
    ...overrides,
  };
}

describe('IssueList', () => {
  it('tells an unfiltered empty project to run a sync', async () => {
    server.use(
      http.post('/api/projects/p1/issues/query', () =>
        HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })),
    );

    renderList();

    expect(await screen.findByText(/no issues yet/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /go to sync/i })).toBeInTheDocument();
    expect(screen.queryByText(/no issues match/i)).not.toBeInTheDocument();
  });

  it('tells a filtered empty result to change the filter, not to sync', async () => {
    server.use(
      http.post('/api/projects/p1/issues/query', () =>
        HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })),
    );

    // Render with a condition already present, so the empty result is a
    // filtered one. EX3's builder produces this state interactively.
    renderList({ initialConditions: [{ field: 'status', op: 'Eq', value: 'Done' }] });

    expect(await screen.findByText(/no issues match this filter/i)).toBeInTheDocument();
    expect(screen.queryByText(/no issues yet/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /go to sync/i })).not.toBeInTheDocument();
  });

  it('requests page 2 when Next is clicked', async () => {
    const pages: number[] = [];
    server.use(
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        const body = (await request.json()) as { page: number };
        pages.push(body.page);
        return HttpResponse.json({ items: [row('ACME-1')], page: body.page, pageSize: 50, totalCount: 120 });
      }),
    );
    const user = userEvent.setup();
    renderList();
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: /next/i }));

    await waitFor(() => expect(pages).toContain(2));
  });

  it('never requests a pageSize above the API maximum', async () => {
    let sent = 0;
    server.use(
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        sent = ((await request.json()) as { pageSize: number }).pageSize;
        return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
      }),
    );
    renderList();
    await waitFor(() => expect(sent).toBeGreaterThan(0));
    expect(sent).toBeLessThanOrEqual(MAX_PAGE_SIZE);
  });

  it('keeps the previous results on screen when the server rejects a filter', async () => {
    // First response succeeds with one row; second (triggered by clicking
    // Next, which changes the request/query key) returns 400. The row must
    // stay rendered alongside the alert, not disappear.
    server.use(
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        const body = (await request.json()) as { page: number };
        if (body.page === 1) {
          return HttpResponse.json({ items: [row('ACME-1')], page: 1, pageSize: 50, totalCount: 120 });
        }
        return HttpResponse.json({ title: 'Unknown field: bogus' }, { status: 400 });
      }),
    );
    const user = userEvent.setup();
    renderList();
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: /next/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/unknown field/i);
    expect(screen.getByText('ACME-1')).toBeInTheDocument();
  });

  it('does not leak stale rows from one project into another after navigating between projects', async () => {
    // React Router does not remount on a param-only change at the same
    // route path, so page/conditions/lastGoodData could otherwise survive
    // a project switch. p1 succeeds with a row; switching to p2 (whose
    // request 400s) must show p2's error WITHOUT p1's row still on screen.
    server.use(
      http.post('/api/projects/p1/issues/query', () =>
        HttpResponse.json({ items: [row('ACME-1')], page: 1, pageSize: 50, totalCount: 1 })),
      http.post('/api/projects/p2/issues/query', () =>
        HttpResponse.json({ title: 'Unknown field: bogus' }, { status: 400 })),
    );
    const user = userEvent.setup();
    renderRoute();
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: /switch project/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/unknown field/i);
    expect(screen.queryByText('ACME-1')).not.toBeInTheDocument();
  });
});
