import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/setup';
import type { JqlRequest, JqlResult } from '../../api/issues';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { noSavedViews } from '../../test/viewsFixture';
import IssueList from './IssueList';
import { JqlActions } from './JqlActions';

const REQUEST: JqlRequest = {
  filter: { logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] },
  sortBy: 'updatedAt',
  sortDescending: true,
};

const RESULT: JqlResult = {
  jql: 'project = "ACME" AND status = "Done" ORDER BY updated DESC, key ASC',
  url: 'https://acme.atlassian.net/issues/?jql=project%20%3D%20%22ACME%22',
  warnings: [],
};

function mockJql(result: JqlResult = RESULT): unknown[] {
  const bodies: unknown[] = [];
  server.use(http.post('/api/projects/p1/issues/jql', async ({ request }) => {
    bodies.push(await request.json());
    return HttpResponse.json(result);
  }));
  return bodies;
}

function renderActions(request: JqlRequest = REQUEST) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <JqlActions projectId="p1" request={request} />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('JqlActions', () => {
  it('copies the JQL of the filter it is given and says so', async () => {
    const bodies = mockJql();
    const user = userEvent.setup();
    const writeText = vi.spyOn(navigator.clipboard, 'writeText');
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Copy JQL' }));

    expect(await screen.findByRole('status')).toHaveTextContent('JQL copied');
    expect(writeText).toHaveBeenCalledWith(RESULT.jql);
    expect(bodies).toEqual([REQUEST]);
  });

  it('shows the JQL to copy by hand when the browser refuses', async () => {
    mockJql();
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new Error('denied'));
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Copy JQL' }));

    const dialog = await screen.findByRole('dialog', { name: 'Copy the JQL' });
    expect(within(dialog).getByLabelText('JQL')).toHaveValue(RESULT.jql);
    expect(screen.queryByText('JQL copied')).not.toBeInTheDocument();
  });

  it('opens the Jira link in a new tab without an opener or a referrer', async () => {
    mockJql();
    const user = userEvent.setup();
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Open in Jira' }));

    await waitFor(() => expect(open).toHaveBeenCalledWith(RESULT.url, '_blank', 'noopener,noreferrer'));
  });

  it('never opens a link that is not a web address', async () => {
    mockJql({ ...RESULT, url: 'javascript:alert(1)' });
    const user = userEvent.setup();
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Open in Jira' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The Jira link is not a web address.');
    expect(open).not.toHaveBeenCalled();
  });

  it('lists the differences once the JQL is known', async () => {
    const messages = [
      'Left out "history ChangeCount of Sprint" (JQL cannot count changes), so the JQL is wider than this filter: it can match issues this list does not show.',
      'JQL has no month unit for relative dates, so months are approximated as 30 days.',
    ];
    mockJql({ ...RESULT, warnings: messages.map((message) => ({ message })) });
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, 'writeText');
    renderActions();
    expect(screen.queryByRole('button', { name: /differences?$/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Copy JQL' }));
    await user.click(await screen.findByRole('button', { name: '2 differences' }));

    const dialog = screen.getByRole('dialog', { name: 'How the JQL differs from this filter' });
    expect(within(dialog).getAllByRole('listitem').map((li) => li.textContent)).toEqual(messages);
  });

  it('shows the server’s reason when the filter cannot be translated', async () => {
    server.use(http.post('/api/projects/p1/issues/jql', () =>
      HttpResponse.json({ title: "Unknown field 'bogus'." }, { status: 400 })));
    const user = userEvent.setup();
    renderActions();

    await user.click(screen.getByRole('button', { name: 'Copy JQL' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("Unknown field 'bogus'.");
  });
});

describe('JqlActions on the issue list', () => {
  it('asks for the JQL of the filter the list is queried with, and its sort', async () => {
    server.use(
      http.get('/api/filter-catalog', () => HttpResponse.json(FILTER_CATALOG)),
      noSavedViews,
      http.post('/api/projects/p1/issues/query', () =>
        HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })),
    );
    const bodies = mockJql();
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, 'writeText');
    const filter = {
      logic: 'And',
      conditions: [
        { field: 'summary', op: 'Contains', value: 'login' },
        { field: 'status', op: 'Eq', value: 'Done', disabled: true },
      ],
    };
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter
          initialEntries={[`/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(filter))}&sortBy=priority&sortDesc=false`]}
        >
          <Routes>
            <Route path="/projects/:id/issues" element={<IssueList />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await user.click(await screen.findByRole('button', { name: 'Copy JQL' }));

    await waitFor(() => expect(bodies).toEqual([{
      filter: { logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }] },
      sortBy: 'priority',
      sortDescending: false,
    }]));
  });
});
