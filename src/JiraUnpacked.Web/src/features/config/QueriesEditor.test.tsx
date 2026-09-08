import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { QueriesEditor } from './QueriesEditor';

function renderEditor() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <QueriesEditor projectId="p1" projectKey="DEVOPS" />
    </QueryClientProvider>,
  );
}

const ONE_QUERY = [
  { id: 'q1', jiraProjectId: 'p1', jql: 'project = "DEVOPS" AND updated >= -30d', createdAtUtc: '2026-01-01T00:00:00Z' },
];

describe('QueriesEditor', () => {
  it('says the whole project is synced when there is no query', async () => {
    // "No queries" and "nothing is synced" look identical on an empty list, and the difference
    // matters enormously on a large project — so the empty state has to say which it is.
    server.use(http.get('/api/projects/p1/queries', () => HttpResponse.json([])));

    renderEditor();

    expect(await screen.findByText(/every issue in/i)).toBeInTheDocument();
    expect(screen.getByText('DEVOPS')).toBeInTheDocument();
  });

  it('warns that removing the last query goes back to syncing everything', async () => {
    // Deleting the last one silently widens the sync from a slice to the entire project. That is
    // the opposite of what "remove" suggests, so the button says so before it is pressed.
    server.use(http.get('/api/projects/p1/queries', () => HttpResponse.json(ONE_QUERY)));

    renderEditor();

    expect(await screen.findByRole('button', { name: /remove \(syncs all\)/i })).toBeInTheDocument();
    expect(screen.getByText(/remove it to go back to syncing every issue/i)).toBeInTheDocument();
  });

  it('sends the typed JQL and clears the box', async () => {
    let sent: unknown = null;
    server.use(
      http.get('/api/projects/p1/queries', () => HttpResponse.json([])),
      http.post('/api/projects/p1/queries', async ({ request }) => {
        sent = await request.json();
        return HttpResponse.json(ONE_QUERY[0], { status: 201 });
      }),
    );

    renderEditor();
    await userEvent.type(await screen.findByLabelText('JQL'), 'project = "DEVOPS"');
    await userEvent.click(screen.getByRole('button', { name: /add query/i }));

    await waitFor(() => expect(sent).toEqual({ jql: 'project = "DEVOPS"' }));
    await waitFor(() => expect(screen.getByLabelText('JQL')).toHaveValue(''));
  });

  it("shows Jira's own parse error when the JQL is rejected", async () => {
    // The server validates JQL against Jira before storing it. Jira names the offending field
    // far better than we could, so the detail is surfaced rather than replaced.
    server.use(
      http.get('/api/projects/p1/queries', () => HttpResponse.json([])),
      http.post('/api/projects/p1/queries', () =>
        HttpResponse.json(
          { title: 'Invalid JQL.', detail: "Field 'sprintt' does not exist or you do not have permission to view it." },
          { status: 400 },
        )),
    );

    renderEditor();
    await userEvent.type(await screen.findByLabelText('JQL'), 'sprintt = 4');
    await userEvent.click(screen.getByRole('button', { name: /add query/i }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/invalid jql/i);
    expect(alert).toHaveTextContent(/sprintt/);
  });

  it('will not submit an empty or whitespace-only query', async () => {
    server.use(http.get('/api/projects/p1/queries', () => HttpResponse.json([])));

    renderEditor();
    const submit = await screen.findByRole('button', { name: /add query/i });
    expect(submit).toBeDisabled();

    await userEvent.type(screen.getByLabelText('JQL'), '   ');
    expect(submit).toBeDisabled();
  });
});
