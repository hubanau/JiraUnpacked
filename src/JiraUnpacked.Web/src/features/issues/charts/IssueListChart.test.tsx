import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../../test/setup';
import { chartFixture } from '../../../test/chartFixture';
import { FILTER_CATALOG as CATALOG } from '../../../test/filterCatalogFixture';
import { noSavedViews } from '../../../test/viewsFixture';
import IssueList from '../IssueList';

let search = '';
function LocationProbe() {
  search = useLocation().search;
  return null;
}

function renderListAt(url: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/projects/:id/issues" element={<><IssueList /><LocationProbe /></>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const page = {
  items: [{
    jiraIssueId: '1', jiraKey: 'ACME-1', summary: 's', status: 'To Do', statusCategory: 'To Do',
    issueType: 'Story', priority: null, assignee: null, sprint: null, team: null, storyPoints: 3,
    parentJiraIssueId: null, jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-01-02T00:00:00Z',
  }],
  page: 1, pageSize: 50, totalCount: 1,
};

describe('IssueList — chart', () => {
  let chartRequests: { groupBy: string; filter?: unknown }[];
  let queryRequests: { filter?: unknown }[];

  beforeEach(() => {
    chartRequests = [];
    queryRequests = [];
    server.use(
      http.get('/api/filter-catalog', () => HttpResponse.json(CATALOG)),
      noSavedViews,
      http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })),
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        queryRequests.push(await request.json() as { filter?: unknown });
        return HttpResponse.json(page);
      }),
      http.post('/api/projects/p1/issues/chart', async ({ request }) => {
        chartRequests.push(await request.json() as { groupBy: string });
        return HttpResponse.json(chartFixture);
      }),
    );
  });

  it('opens from the toolbar, lives in the URL, and closes with ×', async () => {
    renderListAt('/projects/p1/issues');
    await screen.findByText('ACME-1');
    expect(chartRequests).toHaveLength(0);

    await userEvent.click(screen.getByRole('button', { name: 'Chart' }));

    expect(await screen.findByRole('button', { name: 'Filter to To Do: 5' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Chart' })).toHaveAttribute('aria-pressed', 'true');
    expect(search).toBe('?chart=status');

    await userEvent.click(screen.getByRole('button', { name: 'Close chart' }));
    expect(screen.queryByLabelText('Group by')).toBeNull();
    expect(search).toBe('');
  });

  it('switches measure and chart type without asking the server again', async () => {
    renderListAt('/projects/p1/issues?chart=status');
    await screen.findByText('Total: 11 issues');

    await userEvent.click(screen.getByRole('button', { name: 'Story points' }));
    await userEvent.click(screen.getByRole('button', { name: 'Table' }));

    expect(await screen.findByText('Total: 21 points · 3 issues without points')).toBeInTheDocument();
    expect(screen.getAllByRole('table')).toHaveLength(2);   // the list's and the chart's
    expect(chartRequests).toHaveLength(1);
  });

  it('asks again when the field changes', async () => {
    renderListAt('/projects/p1/issues?chart=status');
    await screen.findByText('Total: 11 issues');

    await userEvent.selectOptions(screen.getByLabelText('Group by'), 'team');

    await waitFor(() => expect(chartRequests.map((r) => r.groupBy)).toEqual(['status', 'team']));
  });

  it('narrows the list to a clicked bar, and keeps the old chart up while the new one loads', async () => {
    renderListAt('/projects/p1/issues?chart=status');
    await screen.findByText('Total: 11 issues');
    // Held until the test has looked at the panel mid-load: a timed delay would race the asserts.
    let release = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    server.use(http.post('/api/projects/p1/issues/chart', async ({ request }) => {
      chartRequests.push(await request.json() as { groupBy: string });
      await gate;
      return HttpResponse.json({ ...chartFixture, totalIssues: 5, groups: [chartFixture.groups[0]] });
    }));

    await userEvent.click(screen.getByRole('button', { name: 'Filter to To Do: 5' }));

    const filtered = { logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'To Do' }] };
    await waitFor(() => expect(queryRequests.at(-1)?.filter).toEqual(filtered));
    await waitFor(() => expect(chartRequests.at(-1)?.filter).toEqual(filtered));
    expect(screen.getByText('Total: 11 issues')).toBeInTheDocument();   // the previous chart, not a blank panel
    release();
    expect(await screen.findByText('Total: 5 issues')).toBeInTheDocument();
  });
});
