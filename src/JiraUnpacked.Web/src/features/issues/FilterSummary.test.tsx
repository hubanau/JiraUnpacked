import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import IssueList from './IssueList';
import type { IssueListItem } from '../../api/issues';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { mockViewsApi, noSavedViews, savedView } from '../../test/viewsFixture';

const ROW: IssueListItem = {
  jiraIssueId: '10001', jiraKey: 'ACME-1', summary: 'Login fails', status: 'Open', statusCategory: 'To Do',
  issueType: 'Bug', priority: 'High', assignee: 'Alice', sprint: 'Sprint 1', team: 'Core', storyPoints: 3,
  parentJiraIssueId: null, jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-01-02T00:00:00Z',
};

function mockList(totalCount: number, withViews = true) {
  server.use(
    http.get('/api/filter-catalog', () => HttpResponse.json(FILTER_CATALOG)),
    http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })),
    http.post('/api/projects/p1/issues/query', () =>
      HttpResponse.json({ items: [ROW], page: 1, pageSize: 50, totalCount })),
  );
  if (withViews) server.use(noSavedViews);
}

function renderAt(url: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const link = (filter: unknown) => `/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(filter))}`;
const summary = () => screen.getByRole('region', { name: 'Filter summary' });

describe('the filter summary', () => {
  it('opens a bare list with the builder expanded, reading All issues with its count', async () => {
    mockList(1);
    renderAt('/projects/p1/issues');

    expect(await screen.findByRole('button', { name: /add condition/i })).toBeInTheDocument();
    expect(summary()).toHaveTextContent('Showing: All issues');
    await waitFor(() => expect(summary()).toHaveTextContent('· 1 issue'));
    expect(screen.getByRole('button', { name: 'Collapse' })).toHaveAttribute('aria-expanded', 'true');
  });

  it('opens a link with a filter collapsed: the sentence and its count, no builder', async () => {
    mockList(42);
    renderAt(link({ logic: 'And', conditions: [{ field: 'status', op: 'In', values: ['Backlog', 'To Do'] }] }));

    const toggle = await screen.findByRole('button', { name: 'Edit filter' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(summary()).toHaveTextContent('Showing: Status is any of Backlog, To Do');
    await waitFor(() => expect(summary()).toHaveTextContent('· 42 issues'));
    expect(screen.queryByLabelText(/^match/i)).not.toBeInTheDocument();
  });

  it('expands and collapses on the toggle, and the sentence follows every edit', async () => {
    mockList(42);
    const user = userEvent.setup({ delay: null });
    renderAt(link({ logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }] }));

    await user.click(await screen.findByRole('button', { name: 'Edit filter' }));
    await user.type(screen.getByLabelText('Value for condition 1'), 's');
    expect(summary()).toHaveTextContent('Showing: Summary contains "logins"');

    await user.click(screen.getByRole('button', { name: 'Collapse' }));
    expect(screen.queryByLabelText('Value for condition 1')).not.toBeInTheDocument();
    expect(summary()).toHaveTextContent('Summary contains "logins"');
    expect(screen.getByRole('button', { name: 'Edit filter' })).toHaveAttribute('aria-expanded', 'false');
  });

  it('opens a default view with a filter collapsed, as loaded on mount', async () => {
    mockList(42, false);
    mockViewsApi([savedView({
      id: 'v-login', name: 'Login', isDefault: true,
      filter: { logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }] },
    })]);
    renderAt('/projects/p1/issues');

    expect(await screen.findByRole('button', { name: 'Edit filter' })).toBeInTheDocument();
    expect(summary()).toHaveTextContent('Showing: Summary contains "login"');
  });

  it('opens a link with only a sort expanded', async () => {
    mockList(42);
    renderAt('/projects/p1/issues?sortBy=summary');

    expect(await screen.findByRole('button', { name: 'Collapse' })).toBeInTheDocument();
  });
});
