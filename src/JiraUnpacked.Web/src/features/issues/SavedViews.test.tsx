import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { delay, http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { mockViewsApi, savedView } from '../../test/viewsFixture';
import { expandFilter } from '../../test/expandFilter';
import { queryKeys } from '../../api/queryKeys';
import IssueList from './IssueList';
import { DEFAULT_COLUMNS } from './useListState';

type User = ReturnType<typeof userEvent.setup>;

const ITEM = {
  jiraIssueId: '10001', jiraKey: 'ACME-1', summary: 'Login fails',
  status: 'Backlog', issueType: 'Bug', priority: 'High', assignee: 'Ada',
  sprint: 'PRP 20', team: 'CI/CD', storyPoints: 3,
  parentJiraIssueId: null, jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-02-01T00:00:00Z',
};

/** The router's current query string, where a test can read it. */
function LocationProbe() {
  const { search } = useLocation();
  return <output data-testid="location">{search}</output>;
}

function NavButtons() {
  const navigate = useNavigate();
  return (
    <>
      <button type="button" onClick={() => navigate(-1)}>Go back</button>
      <button type="button" onClick={() => navigate('/projects/p1/issues')}>Open the bare list</button>
      <button type="button" onClick={() => navigate('/projects/p1/issues?sortBy=summary')}>
        Open the list sorted by summary
      </button>
    </>
  );
}

/**
 * Renders IssueList on the last of `entries` (so earlier entries are what Back returns to).
 * Returns the render result plus the QueryClient, so a test can inspect the mutation cache
 * directly when there's no DOM signal for "a request has settled" (e.g. it failed while the menu
 * that would have shown its error was already closed).
 */
function renderList(...entries: string[]) {
  const initial = entries.length > 0 ? entries : ['/projects/p1/issues'];
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const result = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={initial} initialIndex={initial.length - 1}>
        <LocationProbe />
        <NavButtons />
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
          <Route path="*" element={<p>Somewhere else</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { ...result, queryClient };
}

const urlParams = () => new URLSearchParams(screen.getByTestId('location').textContent ?? '');
const menu = () => screen.getByRole('group', { name: 'Saved views' });

/** Catalog, values and issues; returns every issue-query body in the order it was sent. */
function mockIssues(): Record<string, unknown>[] {
  const bodies: Record<string, unknown>[] = [];
  server.use(
    http.get('/api/filter-catalog', () => HttpResponse.json(FILTER_CATALOG)),
    http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })),
    http.post('/api/projects/p1/issues/query', async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      bodies.push(body);
      return HttpResponse.json({ items: [ITEM], page: body.page ?? 1, pageSize: 50, totalCount: 120 });
    }),
  );
  return bodies;
}

const LOGIN_FILTER = { logic: 'And' as const, conditions: [{ field: 'summary', op: 'Contains' as const, value: 'login' }] };

const BACKLOG = savedView({
  id: 'v-backlog', name: 'My backlog', filter: LOGIN_FILTER,
  columns: ['jiraKey', 'summary', 'priority'], sortBy: 'priority', sortDescending: false,
});
const TRIAGE = savedView({ id: 'v-triage', name: 'Triage', sortBy: 'createdAt' });

/** Opens the menu and chooses a view (waiting for the list, which may still be loading). */
async function openView(user: User, name: string) {
  await user.click(screen.getByRole('button', { name: /^Views/ }));
  await user.click(await within(menu()).findByRole('button', { name }));
}

describe('saved views — opening, modified, Save', () => {
  it('opens a view: the URL, the request and the editor follow, and Back returns to what you had', async () => {
    const bodies = mockIssues();
    mockViewsApi([BACKLOG, TRIAGE]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await openView(user, 'My backlog');

    expect(await screen.findByRole('button', { name: 'Views: My backlog' })).toBeInTheDocument();
    expect(Object.fromEntries(urlParams())).toEqual({
      filter: JSON.stringify(LOGIN_FILTER),
      sortBy: 'priority', sortDesc: 'false', cols: 'jiraKey,summary,priority', view: 'v-backlog',
    });
    expect(screen.getByLabelText('Value for condition 1')).toHaveValue('login');
    expect(screen.getByRole('button', { name: 'Sort by Priority' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sort by Status' })).not.toBeInTheDocument();
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({
      filter: LOGIN_FILTER, sortBy: 'priority', sortDescending: false, page: 1,
    }));

    await user.click(screen.getByRole('button', { name: 'Go back' }));

    expect(urlParams().get('sortBy')).toBe('summary');
    expect(urlParams().has('view')).toBe(false);
    expect(screen.getByRole('button', { name: 'Views' })).toBeInTheDocument();
  });

  it('switches views in one step: no request mixes one view’s filter with another’s sort, and no flash of modified', async () => {
    const bodies = mockIssues();
    mockViewsApi([BACKLOG, TRIAGE]);
    // Every text the page ever shows, including one replaced within the same act() flush: act runs
    // both commits before the observer's callback, so the passing values come from the records.
    const texts = new Set<string>();
    const observer = new MutationObserver((records) => {
      for (const record of records) {
        if (record.oldValue !== null) texts.add(record.oldValue);
        for (const node of [...record.addedNodes, ...record.removedNodes]) texts.add(node.textContent ?? '');
      }
    });
    observer.observe(document.body, {
      subtree: true, childList: true, characterData: true, characterDataOldValue: true,
    });
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await openView(user, 'Triage');
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ sortBy: 'createdAt' }));
    await openView(user, 'My backlog');
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ filter: LOGIN_FILTER, sortBy: 'priority' }));
    observer.disconnect();

    // Backlog's filter only ever travels with Backlog's own sort; nothing else carries a filter.
    for (const body of bodies.filter((b) => 'filter' in b)) {
      expect(body).toMatchObject({ filter: LOGIN_FILTER, sortBy: 'priority', sortDescending: false });
    }
    expect([...texts].filter((t) => t.includes('modified'))).toEqual([]);
  });

  it('starts an opened view on page 1, even when only its sort differs', async () => {
    const bodies = mockIssues();
    mockViewsApi([TRIAGE]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: /next/i }));
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ page: 2 }));

    await openView(user, 'Triage');

    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ page: 1, sortBy: 'createdAt' }));
  });

  it('marks the view modified on an edit, and unmarks it when the edit is undone', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');

    const value = screen.getByLabelText('Value for condition 1');
    await user.type(value, 's');
    expect(await screen.findByRole('button', { name: 'Views: My backlog • modified' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
    expect(urlParams().get('view')).toBe('v-backlog');

    await user.type(value, '{Backspace}');
    expect(await screen.findByRole('button', { name: 'Views: My backlog' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();

    // Sort is part of the view too.
    await user.click(screen.getByRole('button', { name: 'Sort by Summary' }));
    expect(await screen.findByRole('button', { name: 'Views: My backlog • modified' })).toBeInTheDocument();
  });

  it('Save overwrites the loaded view with the list as it is now, and the marker clears', async () => {
    mockIssues();
    const api = mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');
    await user.type(screen.getByLabelText('Value for condition 1'), 's');

    await user.click(await screen.findByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('button', { name: 'Views: My backlog' })).toBeInTheDocument();
    // Name and default flag are kept: isDefault is left out, which the server reads as unchanged.
    expect(api.requests).toEqual([{
      method: 'PUT', id: 'v-backlog', body: {
        name: 'My backlog',
        filter: { logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'logins' }] },
        columns: ['jiraKey', 'summary', 'priority'], sortBy: 'priority', sortDescending: false,
      },
    }]);
  });

  it('Save clears the marker when cols= names only columns the table does not know', async () => {
    mockIssues();
    const api = mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    const filter = encodeURIComponent(JSON.stringify(LOGIN_FILTER));
    renderList(`/projects/p1/issues?filter=${filter}&sortBy=priority&sortDesc=false&cols=bogus&view=v-backlog`);
    expect(await screen.findByRole('button', { name: 'Views: My backlog • modified' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('button', { name: 'Views: My backlog' })).toBeInTheDocument();
    // No column the table knows is no column list at all: the view goes back to the default columns.
    expect(api.requests).toEqual([{
      method: 'PUT', id: 'v-backlog',
      body: { name: 'My backlog', filter: LOGIN_FILTER, sortBy: 'priority', sortDescending: false },
    }]);
  });

  it('shows the server’s reason when Save fails, and keeps the view marked modified', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    server.use(http.put('/api/projects/p1/filters/:viewId', () =>
      HttpResponse.json({ title: 'A view may be at most 8192 bytes once saved.' }, { status: 400 })));
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');
    await user.type(screen.getByLabelText('Value for condition 1'), 's');

    await user.click(await screen.findByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('A view may be at most 8192 bytes once saved.');
    expect(screen.getByRole('button', { name: 'Views: My backlog • modified' })).toBeInTheDocument();
  });

  it('drops a failed Save’s alert once the list is edited, or Back leaves the view', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    server.use(http.put('/api/projects/p1/filters/:viewId', () =>
      HttpResponse.json({ title: 'A view may be at most 8192 bytes once saved.' }, { status: 400 })));
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');
    const value = screen.getByLabelText('Value for condition 1');
    await user.type(value, 's');
    await user.click(await screen.findByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('at most 8192 bytes');

    // A later edit: the alert was about a list that is no longer on screen.
    await user.type(value, 'x');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('at most 8192 bytes');

    // Back to the list before the view: the alert was about a view that is no longer loaded.
    await user.click(screen.getByRole('button', { name: 'Go back' }));
    expect(await screen.findByRole('button', { name: 'Views' })).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('points the trigger at the popover it opens, and claims no menu', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    const trigger = screen.getByRole('button', { name: 'Views' });
    // aria-haspopup="true" means a menu; the popover is a group of controls, not a role=menu.
    expect(trigger).not.toHaveAttribute('aria-haspopup');
    await user.click(trigger);
    expect(menu().id).not.toBe('');
    expect(trigger).toHaveAttribute('aria-controls', menu().id);
  });

  it('closes the menu on Escape (focus back on the button) and on a click outside', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList();
    await screen.findByText('ACME-1');

    const trigger = screen.getByRole('button', { name: 'Views' });
    await user.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(await within(menu()).findByRole('button', { name: 'My backlog' })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();

    await user.click(trigger);
    await user.click(screen.getByText(/Page 1 of/));
    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();
  });

  it('says Views unavailable when the views cannot load, and the issue list still works', async () => {
    mockIssues();
    server.use(http.get('/api/projects/p1/filters', () => new HttpResponse(null, { status: 500 })));
    const user = userEvent.setup();
    renderList();

    expect(await screen.findByText('ACME-1')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(await within(menu()).findByText('Views unavailable')).toBeInTheDocument();
    expect(within(menu()).queryByRole('button')).not.toBeInTheDocument();
  });
  it('keeps listing the views it has when a later refetch fails', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    const { queryClient } = renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(await within(menu()).findByRole('button', { name: 'My backlog' })).toBeInTheDocument();
    await user.keyboard('{Escape}');

    server.use(http.get('/api/projects/p1/filters', () => new HttpResponse(null, { status: 500 })));
    await queryClient.invalidateQueries({ queryKey: queryKeys.views('p1') });
    await waitFor(() => expect(queryClient.getQueryState(queryKeys.views('p1'))?.status).toBe('error'));

    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(within(menu()).getByRole('button', { name: 'My backlog' })).toBeInTheDocument();
    expect(within(menu()).queryByText('Views unavailable')).not.toBeInTheDocument();
  });
});

describe('saved views — managing', () => {
  it('saves the list as a new view and loads it', async () => {
    mockIssues();
    const api = mockViewsApi([]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary&sortDesc=false');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(await within(menu()).findByText('No saved views yet')).toBeInTheDocument();
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), '  By summary  ');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('button', { name: 'Views: By summary' })).toBeInTheDocument();
    expect(api.requests).toEqual([{
      method: 'POST',
      body: { name: 'By summary', columns: DEFAULT_COLUMNS, sortBy: 'summary', sortDescending: false },
    }]);
    expect(urlParams().get('view')).toBe(api.views[0].id);
    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();
  });

  it('says a taken name is taken, keeping the menu open with the name as typed', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), 'My backlog');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    expect(await within(menu()).findByRole('alert')).toHaveTextContent('A view with this name already exists');
    expect(within(menu()).getByLabelText('View name')).toHaveValue('My backlog');
  });

  it('shows the server’s own reason for any other refusal, such as the per-project cap', async () => {
    mockIssues();
    mockViewsApi([]);
    server.use(http.post('/api/projects/p1/filters', () =>
      HttpResponse.json({ title: 'A project may have at most 100 saved views.' }, { status: 409 })));
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), 'One too many');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    expect(await within(menu()).findByRole('alert')).toHaveTextContent('A project may have at most 100 saved views.');
  });

  it('needs a name: Save stays disabled while it is blank, and the field stops at 100 characters', async () => {
    mockIssues();
    mockViewsApi([]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    const name = within(menu()).getByLabelText('View name');
    const save = within(menu()).getByRole('button', { name: 'Save' });

    expect(save).toBeDisabled();
    await user.type(name, '   ');
    expect(save).toBeDisabled();
    expect(name).toHaveAttribute('maxlength', '100');
  });

  it('renames the loaded view without saving the edits made since opening it', async () => {
    mockIssues();
    const api = mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');
    await user.type(screen.getByLabelText('Value for condition 1'), 's');   // an unsaved edit

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    await user.click(within(menu()).getByRole('button', { name: 'Rename…' }));
    const name = within(menu()).getByLabelText('View name');
    expect(name).toHaveValue('My backlog');
    await user.clear(name);
    await user.type(name, 'Login bugs');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('button', { name: 'Views: Login bugs • modified' })).toBeInTheDocument();
    expect(api.requests).toEqual([{
      method: 'PUT', id: 'v-backlog', body: {
        name: 'Login bugs', filter: LOGIN_FILTER,
        columns: ['jiraKey', 'summary', 'priority'], sortBy: 'priority', sortDescending: false,
      },
    }]);
  });

  it('sets and removes the default, and stars the default in the list', async () => {
    mockIssues();
    const api = mockViewsApi([BACKLOG, TRIAGE]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'Triage');

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    await user.click(within(menu()).getByRole('button', { name: 'Set as default' }));
    await waitFor(() => expect(api.views.find((v) => v.id === 'v-triage')?.isDefault).toBe(true));
    // Only the flag moves: the view's stored definition goes back unchanged.
    expect(api.requests.at(-1)).toEqual({
      method: 'PUT', id: 'v-triage', body: { name: 'Triage', sortBy: 'createdAt', isDefault: true },
    });

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    const items = within(menu()).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('★ Default: Triage');
    expect(items[1]).toHaveTextContent('My backlog');
    expect(items[1]).not.toHaveTextContent('★');

    await user.click(within(menu()).getByRole('button', { name: 'Remove default' }));
    await waitFor(() => expect(api.views.some((v) => v.isDefault)).toBe(false));
    expect(api.requests.at(-1)?.body).toEqual({ name: 'Triage', sortBy: 'createdAt', isDefault: false });
  });

  it('deletes the loaded view after confirming, and leaves the list as it is', async () => {
    mockIssues();
    const api = mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    await user.click(within(menu()).getByRole('button', { name: 'Delete…' }));
    const dialog = screen.getByRole('dialog', { name: 'Delete view “My backlog”?' });
    await user.click(within(dialog).getByRole('button', { name: 'Delete view' }));

    // The label and the URL must update together — the very next, synchronous check after finding
    // the label is the URL, with no further await between them.
    const trigger = await screen.findByRole('button', { name: 'Views' });
    expect(trigger).toBeInTheDocument();
    expect(urlParams().has('view')).toBe(false);
    expect(urlParams().get('sortBy')).toBe('priority');
    expect(screen.getByLabelText('Value for condition 1')).toHaveValue('login');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(api.views).toEqual([]);
    expect(trigger).toHaveFocus();

    // Reopening shows the list without the deleted view — this is what actually proves the
    // deferred cache write (ViewsMenu's effect keyed on `loaded`, see the comment on
    // pendingDeleteId) ran, not merely that the URL moved on. Without it, the cache would still
    // hold the deleted view and this would show "My backlog" instead.
    await user.click(trigger);
    expect(await within(menu()).findByText('No saved views yet')).toBeInTheDocument();
  });

  it('keeps the confirmation open with the reason when a delete fails', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    server.use(http.delete('/api/projects/p1/filters/:viewId', () => new HttpResponse(null, { status: 500 })));
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    await user.click(within(menu()).getByRole('button', { name: 'Delete…' }));
    const dialog = screen.getByRole('dialog', { name: 'Delete view “My backlog”?' });
    await user.click(within(dialog).getByRole('button', { name: 'Delete view' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Something went wrong on the server.');
    expect(urlParams().get('view')).toBe('v-backlog');
  });

  it('returns focus to the menu item that opened the form when Cancel is clicked', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.click(within(menu()).getByRole('button', { name: 'Cancel' }));
    expect(within(menu()).getByRole('button', { name: 'Save as new view…' })).toHaveFocus();

    await user.click(within(menu()).getByRole('button', { name: 'Rename…' }));
    await user.click(within(menu()).getByRole('button', { name: 'Cancel' }));
    expect(within(menu()).getByRole('button', { name: 'Rename…' })).toHaveFocus();

    // Cancel keeps the popover open (unlike a successful save), so Escape still closes the whole thing.
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();
  });

  it('focuses the Views trigger after a successful save closes the menu', async () => {
    mockIssues();
    mockViewsApi([]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary&sortDesc=false');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), 'By summary');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('button', { name: 'Views: By summary' })).toHaveFocus();
  });

  it('does not leak a create/rename failure into the next time the menu opens', async () => {
    mockIssues();
    mockViewsApi([]);
    // A deferred response: the request stays "in flight" until the test explicitly resolves it,
    // so the menu can be closed while it is still pending — the exact window the stale-error bug
    // lived in (onError firing after close() had already cleared `error`).
    let resolveRequest: ((response: Response) => void) | undefined;
    server.use(http.post('/api/projects/p1/filters', () =>
      new Promise<Response>((resolve) => { resolveRequest = resolve; })));
    const user = userEvent.setup();
    const { queryClient } = renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), 'Too many');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    // Close the menu while the request is still unresolved, with an outside click (Escape is
    // covered by its own test). Not "ACME-1": that's a link to the issue detail route, which this
    // test doesn't mount.
    await user.click(screen.getByText(/Page 1 of/));
    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();

    // Now let it fail, with the menu already closed. There is no DOM signal for "the mutation has
    // settled" here (that's the whole point of the fix — closed means silent), so this reads
    // react-query's own mutation cache instead of guessing at a timeout.
    resolveRequest?.(HttpResponse.json({ title: 'A project may have at most 100 saved views.' }, { status: 409 }));
    await waitFor(() => {
      const mutations = queryClient.getMutationCache().getAll();
      expect(mutations.some((m) => m.state.status === 'error')).toBe(true);
    });

    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(within(menu()).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('submits the name form on Enter, not only by clicking Save', async () => {
    mockIssues();
    const api = mockViewsApi([]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary&sortDesc=false');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), 'By summary{Enter}');

    expect(await screen.findByRole('button', { name: 'Views: By summary' })).toBeInTheDocument();
    expect(api.requests).toEqual([{
      method: 'POST',
      body: { name: 'By summary', columns: DEFAULT_COLUMNS, sortBy: 'summary', sortDescending: false },
    }]);
  });

  it('closes the menu on Escape while a new view is still being saved', async () => {
    mockIssues();
    mockViewsApi([]);
    let resolveRequest: ((response: Response) => void) | undefined;
    server.use(http.post('/api/projects/p1/filters', () =>
      new Promise<Response>((resolve) => { resolveRequest = resolve; })));
    const user = userEvent.setup();
    const { queryClient } = renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    const trigger = screen.getByRole('button', { name: 'Views' });
    await user.click(trigger);
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), 'Pending');
    // Clicked, so focus sits on the Save button — which the pending request then disables.
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(within(menu()).getByRole('button', { name: /Save/ })).toBeDisabled());

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();

    resolveRequest?.(HttpResponse.json({ title: 'A project may have at most 100 saved views.' }, { status: 409 }));
    await waitFor(() => expect(queryClient.getMutationCache().getAll()[0]?.state.status).toBe('error'));
  });

  it('closes the whole menu on Escape from inside the name field, focus back on the trigger', async () => {
    mockIssues();
    mockViewsApi([]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    const trigger = screen.getByRole('button', { name: 'Views' });
    await user.click(trigger);
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), 'Draft');
    await user.keyboard('{Escape}');

    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });
});

describe('saved views — the default view and stale links', () => {
  it('opens the default view on a bare URL, replacing the entry rather than adding one', async () => {
    const bodies = mockIssues();
    mockViewsApi([{ ...BACKLOG, isDefault: true }, TRIAGE]);
    const user = userEvent.setup();
    renderList('/elsewhere?from=before', '/projects/p1/issues');

    expect(await screen.findByRole('button', { name: 'Views: My backlog' })).toBeInTheDocument();
    expect(urlParams().get('view')).toBe('v-backlog');
    // The very first request is already the default view's — no flash of the unfiltered list.
    expect(bodies[0]).toMatchObject({ filter: LOGIN_FILTER, sortBy: 'priority', sortDescending: false });

    await user.click(screen.getByRole('button', { name: 'Go back' }));

    expect(await screen.findByText('Somewhere else')).toBeInTheDocument();
    expect(urlParams().get('from')).toBe('before');
  });

  it('opens the default only once: Clear all and a later bare URL leave the list bare', async () => {
    mockIssues();
    mockViewsApi([{ ...BACKLOG, isDefault: true }]);
    const user = userEvent.setup();
    renderList();
    await screen.findByRole('button', { name: 'Views: My backlog' });

    await expandFilter();
    await user.click(screen.getByRole('button', { name: 'Clear all' }));
    expect(await screen.findByRole('button', { name: 'Views: My backlog • modified' })).toBeInTheDocument();
    expect(urlParams().has('filter')).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Open the bare list' }));

    expect(await screen.findByRole('button', { name: 'Views' })).toBeInTheDocument();
    expect(urlParams().toString()).toBe('');
  });

  it('leaves a URL that already says what to show alone, and does not wait for the views to send it', async () => {
    const bodies = mockIssues();
    server.use(http.get('/api/projects/p1/filters', async () => {
      await delay('infinite');
      return HttpResponse.json([]);
    }));
    renderList('/projects/p1/issues?sortBy=summary');

    expect(await screen.findByText('ACME-1')).toBeInTheDocument();
    expect(bodies[0]).toMatchObject({ sortBy: 'summary' });
  });

  it('does not open the default over list state in the URL', async () => {
    mockIssues();
    mockViewsApi([{ ...BACKLOG, isDefault: true }]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(await within(menu()).findByRole('button', { name: /My backlog/ })).toBeInTheDocument();
    expect(urlParams().has('view')).toBe(false);
    expect(urlParams().get('sortBy')).toBe('summary');
  });

  it('on a bare URL, sends nothing until the views are known', async () => {
    const bodies = mockIssues();
    let catalogServed = false;
    server.use(
      http.get('/api/filter-catalog', () => {
        catalogServed = true;
        return HttpResponse.json(FILTER_CATALOG);
      }),
      http.get('/api/projects/p1/filters', async () => {
        await delay('infinite');
        return HttpResponse.json([]);
      }),
    );
    renderList();

    await waitFor(() => expect(catalogServed).toBe(true));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(bodies).toEqual([]);
  });

  it('on a bare URL, goes ahead unfiltered when the views cannot load', async () => {
    const bodies = mockIssues();
    server.use(http.get('/api/projects/p1/filters', () => new HttpResponse(null, { status: 500 })));
    renderList();

    expect(await screen.findByText('ACME-1')).toBeInTheDocument();
    expect(bodies[0]).not.toHaveProperty('filter');
  });

  it('lets list state that arrives before the views win over the default', async () => {
    mockIssues();
    server.use(http.get('/api/projects/p1/filters', async () => {
      await delay(150);
      return HttpResponse.json([{ ...BACKLOG, isDefault: true }]);
    }));
    const user = userEvent.setup();
    renderList();

    await user.click(screen.getByRole('button', { name: 'Open the list sorted by summary' }));
    await screen.findByText('ACME-1');
    // Wait for the views to arrive — the moment a default would step in — rather than a guess
    // at how long they take.
    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(await within(menu()).findByRole('button', { name: /My backlog/ }, { timeout: 2000 })).toBeInTheDocument();

    expect(screen.getByRole('button', { name: 'Views' })).toBeInTheDocument();
    expect(urlParams().has('view')).toBe(false);
    expect(urlParams().get('sortBy')).toBe('summary');
  });

  it('drops a view id that names no view, and keeps the rest of the link', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const filter = encodeURIComponent('{"logic":"And","conditions":[{"field":"summary","op":"Contains","value":"crash"}]}');
    renderList(`/projects/p1/issues?view=gone&sortBy=summary&filter=${filter}`);

    await waitFor(() => expect(urlParams().has('view')).toBe(false));
    expect(urlParams().get('sortBy')).toBe('summary');
    expect(await screen.findByRole('button', { name: 'Views' })).toBeInTheDocument();
    await expandFilter();
    expect(screen.getByLabelText('Value for condition 1')).toHaveValue('crash');
  });
});
