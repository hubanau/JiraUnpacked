import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { delay, http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import IssueList, { IssueListRoute } from './IssueList';
import type { IssueListItem } from '../../api/issues';
import { DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE } from '../../api/issues';
import { FILTER_CATALOG as CATALOG } from '../../test/filterCatalogFixture';
import { noSavedViews } from '../../test/viewsFixture';

function renderList() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1/issues']}>
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
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
    statusCategory: 'To Do',
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
  beforeEach(() => {
    server.use(
      http.get('/api/filter-catalog', () => HttpResponse.json(CATALOG)),
      noSavedViews,
      // Several tests below add a condition, whose default field is 'status' -- a dictionary
      // field -- so ConditionRow fires its own filter-values fetch regardless of what the test
      // itself cares about (see FilterBuilder.test.tsx's mockFilterCatalog for the same fix and
      // its rationale). Failing it keeps the value control the text input these tests already
      // interact with, without the MSW console noise from a request nothing handled.
      http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })),
    );
  });

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

    // Drive a real condition through the builder (EX3's actual UI), rather
    // than seeding state directly — IssueList no longer accepts a
    // test-only prop for this.
    const user = userEvent.setup({ delay: null });
    renderList();
    await user.click(await screen.findByRole('button', { name: /add condition/i }));
    await user.type(screen.getByLabelText(/value for condition 1/i), 'Done');

    expect(await screen.findByText(/no issues match this filter/i)).toBeInTheDocument();
    expect(screen.queryByText(/no issues yet/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /go to sync/i })).not.toBeInTheDocument();
  });

  it('does not flash the unfiltered empty state while the request after clearing a filter is still in flight', async () => {
    // Regression test: TanStack's placeholderData keeps `data` pointing at
    // the *previous* result for the whole in-flight duration of a new
    // query key. If a filtered request had returned 0 matches and the user
    // clears the filter, deriving the empty state from that stale
    // placeholder (instead of gating on isPlaceholderData) would show
    // "No issues yet — Run a sync" — complete with the Go-to-sync link —
    // on a project that is actually healthy, for as long as the new
    // (unfiltered) request is pending.
    //
    // gcTime: 0 evicts the unfiltered query's cache entry as soon as it
    // becomes inactive (i.e. as soon as a condition is added), so
    // clearing the filter back to it later genuinely goes through a
    // pending/placeholder phase instead of being served warm from cache —
    // the same as a real project the user is filtering for the first time.
    server.use(
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        // Reads either envelope. The SPA sends a `filter` group now that All/Any exists, but the
        // question this handler asks -- "was this request filtered?" -- is the same either way.
        const body = (await request.json()) as
          { conditions?: unknown[]; filter?: { conditions?: unknown[] } };
        const sent = body.filter?.conditions ?? body.conditions;
        if (sent && sent.length > 0) {
          return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
        }
        // Delayed so the pending/placeholder phase of the unfiltered
        // request is observable by the assertions below instead of
        // resolving before this test can inspect it.
        await delay(300);
        return HttpResponse.json({ items: [row('ACME-1')], page: 1, pageSize: 50, totalCount: 1 });
      }),
    );
    const user = userEvent.setup({ delay: null });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/projects/p1/issues']}>
          <Routes>
            <Route path="/projects/:id/issues" element={<IssueList />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await user.click(await screen.findByRole('button', { name: /add condition/i }));
    await user.type(screen.getByLabelText(/value for condition 1/i), 'Done');
    expect(await screen.findByText(/no issues match this filter/i)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /clear all/i }));

    // The unfiltered request is now in flight; `data` is still the
    // placeholder 0-item filtered result. Neither empty state — least of
    // all the "run a sync" one — may show while it settles.
    expect(screen.queryByText(/no issues yet/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/no issues match this filter/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /go to sync/i })).not.toBeInTheDocument();

    // The healthy project's rows do eventually load once the delayed
    // request settles.
    await screen.findByText('ACME-1');
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

  it('resets to page 1 when a filter condition changes', async () => {
    // Narrowing a filter can shrink the result set below the page the user
    // was sitting on — without this reset, that looks exactly like "no
    // matches this filter" when it is really just a stale page number.
    // EX3's FilterBuilder is the first thing that can drive `conditions`
    // through the UI, so this is the first place that can exercise it.
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

    // Changing the condition set (adding a row is enough) must send the
    // very next request back on page 1.
    await user.click(screen.getByRole('button', { name: /add condition/i }));

    await waitFor(() => expect(pages[pages.length - 1]).toBe(1));
  });

  it('requests the default page size, which itself never exceeds the API maximum', async () => {
    let sent = 0;
    server.use(
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        sent = ((await request.json()) as { pageSize: number }).pageSize;
        return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
      }),
    );
    renderList();
    await waitFor(() => expect(sent).toBeGreaterThan(0));
    // The request body actually sent must be DEFAULT_PAGE_SIZE — there is
    // no pageSize UI to vary it — and that constant must itself respect
    // the API's ceiling.
    expect(sent).toBe(DEFAULT_PAGE_SIZE);
    expect(DEFAULT_PAGE_SIZE).toBeLessThanOrEqual(MAX_PAGE_SIZE);
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
