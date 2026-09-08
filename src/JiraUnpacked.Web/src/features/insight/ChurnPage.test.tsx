import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import ChurnPage from './ChurnPage';

const CHURN_URL = '/api/projects/p1/metrics/field-churn';

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1/churn']}>
        <Routes>
          <Route path="/projects/:id/churn" element={<ChurnPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const item = (over: Partial<Record<string, unknown>> = {}) => ({
  jiraIssueId: '10001', jiraKey: 'ACME-1', summary: 'carried', issueType: 'Story',
  currentStatus: 'In Progress', class: 'Active', changes: 4, distinctValues: 4,
  firstChangedAt: '2026-07-01T00:00:00Z', lastChangedAt: '2026-08-30T00:00:00Z', ...over,
});

const report = (over: Partial<Record<string, unknown>> = {}) => ({
  field: 'Sprint',
  availableFields: ['Sprint', 'assignee', 'status'],
  items: { items: [item()], page: 1, pageSize: 50, totalCount: 1 },
  histogram: [
    { band: '1', count: 0 }, { band: '2', count: 3 }, { band: '3', count: 1 },
    { band: '4', count: 1 }, { band: '5+', count: 0 },
  ],
  ...over,
});

describe('ChurnPage', () => {
  it('asks for Sprint, at least 2 changes, Active only — without being told', async () => {
    // The defaults ARE the question people come here to ask, so they must be in the first
    // request rather than something the user has to assemble.
    let seen: URL | null = null;
    server.use(http.get(CHURN_URL, ({ request }) => {
      seen = new URL(request.url);
      return HttpResponse.json(report());
    }));

    renderPage();
    expect(await screen.findByText('ACME-1')).toBeInTheDocument();

    expect(seen!.searchParams.get('field')).toBe('Sprint');
    expect(seen!.searchParams.get('minChanges')).toBe('2');
    expect(seen!.searchParams.getAll('statusClasses')).toEqual(['Active']);
  });

  it('offers only the fields the project actually has history for', async () => {
    // A hardcoded field list would be a guess at another site's Jira schema. The server reports
    // what its own changelog contains.
    server.use(http.get(CHURN_URL, () => HttpResponse.json(report())));

    renderPage();
    await screen.findByText('ACME-1');

    const select = screen.getByLabelText('Field');
    expect(within(select).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['Sprint', 'assignee', 'status']);
  });

  it('re-asks the server when the status classes change', async () => {
    // Class filtering happens server-side against the semantic layer, so toggling must refetch —
    // filtering the current page in the browser would silently drop everything off it.
    const urls: URL[] = [];
    server.use(http.get(CHURN_URL, ({ request }) => {
      urls.push(new URL(request.url));
      return HttpResponse.json(report());
    }));

    renderPage();
    await screen.findByText('ACME-1');

    await userEvent.click(screen.getByRole('checkbox', { name: 'Done' }));
    await screen.findByText('ACME-1');

    expect(urls.at(-1)!.searchParams.getAll('statusClasses')).toEqual(['Active', 'Done']);
  });

  it('shows both counts, because they answer different questions', async () => {
    server.use(http.get(CHURN_URL, () => HttpResponse.json(report({
      items: {
        items: [item({ changes: 3, distinctValues: 2 })],
        page: 1, pageSize: 50, totalCount: 1,
      },
    }))));

    renderPage();
    const row = (await screen.findByText('ACME-1')).closest('tr')!;

    // 3 changes across only 2 sprints: moved out and back, not marched through three.
    expect(within(row).getByText('3')).toBeInTheDocument();
    expect(within(row).getByText('2')).toBeInTheDocument();
  });

  it('says nothing churned rather than showing an empty table', async () => {
    server.use(http.get(CHURN_URL, () => HttpResponse.json(report({
      items: { items: [], page: 1, pageSize: 50, totalCount: 0 },
    }))));

    renderPage();

    expect(await screen.findByText(/no issue changed sprint that often/i)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('surfaces a server error instead of an empty result', async () => {
    // A failed request and "nothing churned" look identical if the error is swallowed, and the
    // wrong one of those two reads as good news.
    server.use(http.get(CHURN_URL, () =>
      HttpResponse.json({ title: 'Unknown status class.' }, { status: 400 })));

    renderPage();

    expect(await screen.findByText('Unknown status class.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});
