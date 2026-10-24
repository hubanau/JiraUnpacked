import { render, screen, waitFor } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import IssueList from './IssueList';
import type { IssueListItem } from '../../api/issues';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { noSavedViews } from '../../test/viewsFixture';

const ROW: IssueListItem = {
  jiraIssueId: '10001', jiraKey: 'ACME-1', summary: 'Login fails', status: 'Open', statusCategory: 'To Do',
  issueType: 'Bug', priority: 'High', assignee: null, sprint: 'Sprint 1', team: 'Core', storyPoints: 3,
  parentJiraIssueId: '10000', jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-01-02T00:00:00Z',
};

type Body = { filter?: unknown; page?: number };

function setup(url = '/projects/p1/issues'): { bodies: Body[]; user: UserEvent } {
  const bodies: Body[] = [];
  server.use(
    http.get('/api/filter-catalog', () => HttpResponse.json(FILTER_CATALOG)),
    noSavedViews,
    http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })),
    http.post('/api/projects/p1/issues/query', async ({ request }) => {
      const body = (await request.json()) as Body;
      bodies.push(body);
      return HttpResponse.json({ items: [ROW], page: body.page ?? 1, pageSize: 50, totalCount: 120 });
    }),
  );
  const user = userEvent.setup({ delay: null });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { bodies, user };
}

const link = (filter: unknown, extra = '') =>
  `/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(filter))}${extra}`;

async function pick(user: UserEvent, cell: string, item: string) {
  await user.click(await screen.findByRole('button', { name: cell }));
  await user.click(screen.getByRole('button', { name: item }));
}

describe('click-to-filter', () => {
  it('filters to a cell’s value and shows the builder with it', async () => {
    const { bodies, user } = setup();

    await pick(user, 'Filter by Status Open', 'Filter to "Open"');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }],
    }));
    expect(screen.getByLabelText('Field for condition 1')).toHaveValue('status');
  });

  it('excludes a value, and an empty cell filters to empty or not empty', async () => {
    const { bodies, user } = setup();

    await pick(user, 'Filter by Status Open', 'Exclude "Open"');
    await pick(user, 'Filter by Assignee (Empty)', 'Filter to (Empty)');
    await pick(user, 'Filter by Assignee (Empty)', 'Exclude (Empty)');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And',
      conditions: [
        { field: 'status', op: 'Neq', value: 'Open' },
        { field: 'assignee', op: 'IsNull' },
        { field: 'assignee', op: 'IsNotNull' },
      ],
    }));
  });

  it('sends points as a number and parent as the parent’s id, and opens a collapsed builder', async () => {
    const { bodies, user } = setup(link(
      { logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }] },
      '&cols=jiraKey,status,storyPoints,parent',
    ));
    expect(await screen.findByRole('button', { name: 'Edit filter' })).toBeInTheDocument();

    await pick(user, 'Filter by Points 3', 'Filter to "3"');
    await pick(user, 'Filter by Parent 10000', 'Filter to "10000"');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And',
      conditions: [
        { field: 'summary', op: 'Contains', value: 'login' },
        { field: 'storyPoints', op: 'Eq', value: 3 },
        { field: 'parent', op: 'Eq', value: '10000' },
      ],
    }));
    expect(screen.getByRole('button', { name: 'Collapse' })).toBeInTheDocument();

    // Parent has no "is not": Exclude is offered, disabled, and says why.
    await user.click(screen.getByRole('button', { name: 'Filter by Parent 10000' }));
    const exclude = screen.getByRole('button', { name: 'Exclude "10000"' });
    expect(exclude).toBeDisabled();
    expect(exclude).toHaveAttribute('title', 'Not available for Parent');
  });

  it('wraps an Any root so a click always narrows', async () => {
    const any = {
      logic: 'Or',
      conditions: [{ field: 'priority', op: 'Eq', value: 'High' }, { field: 'priority', op: 'Eq', value: 'Low' }],
    };
    const { bodies, user } = setup(link(any));

    await pick(user, 'Filter by Status Open', 'Filter to "Open"');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }], groups: [any],
    }));
  });

  it('adds an identical condition only once', async () => {
    const { user } = setup(link({ logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }] }));

    await pick(user, 'Filter by Status Open', 'Filter to "Open"');

    expect(await screen.findByRole('button', { name: 'Collapse' })).toBeInTheDocument();
    expect(screen.getAllByLabelText(/^Field for condition/)).toHaveLength(1);
  });

  it('disables both items at the condition limit, with the usual hint', async () => {
    const full = {
      logic: 'And',
      conditions: Array.from({ length: 20 }, (_, i) => ({ field: 'summary', op: 'Contains', value: `w${i}` })),
    };
    const { user } = setup(link(full));

    await user.click(await screen.findByRole('button', { name: 'Filter by Status Open' }));

    for (const name of ['Filter to "Open"', 'Exclude "Open"']) {
      const item = screen.getByRole('button', { name });
      expect(item).toBeDisabled();
      expect(item).toHaveAttribute('title', 'Limit: 20 conditions');
    }
  });

  it('works from the keyboard and closes on Escape, focus back on the button', async () => {
    const { bodies, user } = setup();
    const trigger = await screen.findByRole('button', { name: 'Filter by Status Open' });
    trigger.focus();

    await user.keyboard('{Enter}');
    expect(screen.getByRole('group', { name: 'Filter by Status' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('group', { name: 'Filter by Status' })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();

    await user.keyboard('{Enter}');
    await user.tab();
    expect(screen.getByRole('button', { name: 'Filter to "Open"' })).toHaveFocus();
    await user.keyboard('{Enter}');

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }],
    }));
  });

  it('starts the filtered list on page 1', async () => {
    const { bodies, user } = setup();
    await user.click(await screen.findByRole('button', { name: /next/i }));
    await waitFor(() => expect(bodies.at(-1)?.page).toBe(2));

    await pick(user, 'Filter by Status Open', 'Filter to "Open"');

    await waitFor(() => expect(bodies.at(-1)).toMatchObject({
      page: 1, filter: { logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Open' }] },
    }));
  });
});
