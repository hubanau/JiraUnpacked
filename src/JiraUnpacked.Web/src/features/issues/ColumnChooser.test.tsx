import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/setup';
import { setAuthBridge } from '../../api/client';
import IssueList from './IssueList';
import type { FilterCatalog } from '../../api/issues';

const CATALOG: FilterCatalog = {
  fields: [{ name: 'status', type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] }],
  maxConditions: 20, maxInValues: 100, maxGroups: 10, maxDepth: 3,
};

const ITEM = {
  jiraIssueId: '10001', jiraKey: 'ACME-1', summary: 'Login fails',
  status: 'Backlog', issueType: 'Bug', priority: 'High', assignee: 'Ada',
  sprint: 'PRP 20', team: 'CI/CD', storyPoints: 3,
  parentJiraIssueId: '99', jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-02-01T00:00:00Z',
};

function mockList(onRequest?: () => void) {
  server.use(
    http.get('/api/filter-catalog', () => HttpResponse.json(CATALOG)),
    http.post('/api/projects/p1/issues/query', () => {
      onRequest?.();
      return HttpResponse.json({ items: [ITEM], page: 1, pageSize: 50, totalCount: 1 });
    }),
  );
}

function renderList(url = '/projects/p1/issues') {
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

/**
 * The chooser opens a native <details>/<summary>. That is the right control — it discloses,
 * takes focus and toggles with no JavaScript — but jsdom's accessibility mapping gives <summary>
 * no role, so getByRole('button') cannot see it. Clicked by its text instead; what matters is
 * that a user clicking "Columns" gets the checkboxes.
 */
const openChooser = async (user: ReturnType<typeof userEvent.setup>) =>
  user.click(await screen.findByText(/^columns$/i));

describe('ColumnChooser', () => {
  it('offers the four columns the list already fetches but never shows', async () => {
    // Priority, Team, Parent and Created are in every response today and thrown away. Showing
    // them costs nothing that is not already being paid for.
    mockList();
    const user = userEvent.setup();
    renderList();

    await openChooser(user);

    for (const name of [/priority/i, /team/i, /parent/i, /created/i]) {
      expect(screen.getByRole('checkbox', { name })).toBeInTheDocument();
    }
  });

  it('will not let Key be switched off', async () => {
    // Key is the row's identity and its only link to the issue. A table of unlabelled rows is
    // not a smaller table, it is a broken one.
    mockList();
    const user = userEvent.setup();
    renderList();

    await openChooser(user);

    const key = screen.getByRole('checkbox', { name: /^key$/i });
    expect(key).toBeDisabled();
    expect(key).toBeChecked();
  });

  it('hides a column from the table without re-asking the server', async () => {
    // Presentation only: every column is already in the response, so toggling one must not
    // cost a round trip.
    let requests = 0;
    mockList(() => { requests += 1; });
    const user = userEvent.setup();
    renderList();

    await screen.findByRole('columnheader', { name: /sprint/i });
    const before = requests;

    await openChooser(user);
    await user.click(screen.getByRole('checkbox', { name: /sprint/i }));

    expect(screen.queryByRole('columnheader', { name: /sprint/i })).not.toBeInTheDocument();
    expect(requests).toBe(before);
  });

  it('renders the columns named in the URL, so a shared link shows the same table', async () => {
    mockList();
    renderList('/projects/p1/issues?cols=jiraKey,priority,team');

    expect(await screen.findByRole('columnheader', { name: /priority/i })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: /team/i })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: /summary/i })).not.toBeInTheDocument();
  });
});

describe('sortable headers', () => {
  it('sorts by a column when its header is clicked, and flips on a second click', async () => {
    const sent: Record<string, unknown>[] = [];
    server.use(
      http.get('/api/filter-catalog', () => HttpResponse.json(CATALOG)),
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        sent.push((await request.json()) as Record<string, unknown>);
        return HttpResponse.json({ items: [ITEM], page: 1, pageSize: 50, totalCount: 1 });
      }),
    );
    const user = userEvent.setup();
    renderList();

    await user.click(await screen.findByRole('button', { name: /sort by points/i }));
    await waitFor(() =>
      expect(sent.at(-1)).toMatchObject({ sortBy: 'storyPoints', sortDescending: true }));

    await user.click(screen.getByRole('button', { name: /sort by points/i }));
    await waitFor(() =>
      expect(sent.at(-1)).toMatchObject({ sortBy: 'storyPoints', sortDescending: false }));
  });

  it('tells assistive tech which column is sorted and which way', async () => {
    mockList();
    renderList('/projects/p1/issues?sortBy=storyPoints&sortDesc=false');

    const sorted = await screen.findByRole('columnheader', { name: /points/i });
    expect(sorted).toHaveAttribute('aria-sort', 'ascending');

    // Every other header must say "none" rather than nothing: an omitted aria-sort reads as
    // "not sortable", which is a different claim from "sortable, not currently sorted".
    expect(screen.getByRole('columnheader', { name: /summary/i })).toHaveAttribute('aria-sort', 'none');
  });

  it('starts a newly chosen column descending, which is what people expect of Points or a date', async () => {
    const sent: Record<string, unknown>[] = [];
    server.use(
      http.get('/api/filter-catalog', () => HttpResponse.json(CATALOG)),
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        sent.push((await request.json()) as Record<string, unknown>);
        return HttpResponse.json({ items: [ITEM], page: 1, pageSize: 50, totalCount: 1 });
      }),
    );
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary&sortDesc=false');

    await user.click(await screen.findByRole('button', { name: /sort by points/i }));

    await waitFor(() =>
      expect(sent.at(-1)).toMatchObject({ sortBy: 'storyPoints', sortDescending: true }));
  });
});

describe('CSV export', () => {
  const CSV = 'Key,Summary\r\nACME-1,Login fails\r\n';
  let requests: { url: URL; auth: string | null }[];
  let saved: { name: string; text: Promise<string> }[];

  // jsdom's Blob has no text(); FileReader is what it does implement.
  const readBlob = (blob: Blob) => new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error);
    reader.readAsText(blob);
  });

  beforeEach(() => {
    requests = [];
    saved = [];
    setAuthBridge({ getAccessToken: () => 'tok-1', refresh: async () => 'unauthenticated', onAuthFailure: () => {} });

    // jsdom implements neither object URLs nor downloads. Stand in for both and record what
    // the browser would have been asked to save.
    const blobs = new Map<string, Blob>();
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      value: (blob: Blob) => { const url = `blob:test/${blobs.size}`; blobs.set(url, blob); return url; },
    });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: () => {} });
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      saved.push({ name: this.download, text: readBlob(blobs.get(this.getAttribute('href')!)!) });
    });
  });

  afterEach(() => {
    setAuthBridge(null);
    vi.restoreAllMocks();
  });

  function mockExport(respond: () => Response = () => new HttpResponse(CSV, {
    headers: {
      'Content-Type': 'text/csv; charset=utf-8',
      'Content-Disposition': 'attachment; filename="ACME-issues-20260922.csv"',
    },
  })) {
    server.use(http.get('/api/projects/p1/issues/export.csv', ({ request }) => {
      requests.push({ url: new URL(request.url), auth: request.headers.get('authorization') });
      return respond();
    }));
  }

  it('downloads with the session token and saves under the name the server chose', async () => {
    // The regression: this was an <a href download>. A navigation carries cookies, not the
    // in-memory bearer token, so every export was a 401 the browser reported as
    // "File wasn't available on site".
    mockList();
    mockExport();
    const user = userEvent.setup();
    renderList();

    await user.click(await screen.findByRole('button', { name: /export csv/i }));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(requests[0].auth).toBe('Bearer tok-1');
    expect(saved[0].name).toBe('ACME-issues-20260922.csv');
    expect(await saved[0].text).toBe(CSV);
  });

  it('exports the filter, sort and columns on screen', async () => {
    mockList();
    mockExport();
    // The status condition on screen asks for its value picker's choices.
    server.use(http.get('/api/projects/p1/filter-values', () =>
      HttpResponse.json({ field: 'status', values: [{ name: 'Backlog', count: 1 }], emptyCount: 0 })));
    const user = userEvent.setup();
    const filter = encodeURIComponent('[{"field":"status","op":"Eq","value":"Backlog"}]');
    renderList(`/projects/p1/issues?filter=${filter}&logic=Or&sortBy=storyPoints&cols=jiraKey,summary`);

    await user.click(await screen.findByRole('button', { name: /export csv/i }));

    await waitFor(() => expect(requests).toHaveLength(1));
    const params = requests[0].url.searchParams;
    expect(params.get('sortBy')).toBe('storyPoints');
    expect(params.get('cols')).toBe('jiraKey,summary');
    // A legacy link (flat list + logic=Or) is exported as the tree it now means; no logic param.
    expect(params.has('logic')).toBe(false);
    expect(JSON.parse(params.get('filter')!)).toEqual({
      logic: 'Or', conditions: [{ field: 'status', op: 'Eq', value: 'Backlog' }],
    });
  });

  it('omits the filter entirely when there is none, rather than sending an empty one', async () => {
    // The server refuses an empty group rather than guessing whether it means all or nothing.
    mockList();
    mockExport();
    const user = userEvent.setup();
    renderList();

    await user.click(await screen.findByRole('button', { name: /export csv/i }));

    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].url.searchParams.has('filter')).toBe(false);
  });

  it('shows why the server refused the export, and saves nothing', async () => {
    // Saving the error body as the CSV would hand the user a file that looks like an export.
    mockList();
    mockExport(() => HttpResponse.json(
      { title: 'This view has 60000 issues; export is limited to 50000.' }, { status: 400 }));
    const user = userEvent.setup();
    renderList();

    await user.click(await screen.findByRole('button', { name: /export csv/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/limited to 50000/);
    expect(saved).toHaveLength(0);
  });
});
