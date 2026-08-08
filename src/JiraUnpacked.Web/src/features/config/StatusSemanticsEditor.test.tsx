import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { StatusSemanticsEditor } from './StatusSemanticsEditor';

function renderEditor() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <StatusSemanticsEditor projectId="p1" />
    </QueryClientProvider>,
  );
}

const STATUSES = [
  { id: '1', name: 'To Do' },
  { id: '2', name: 'In Progress' },
  { id: '3', name: 'Done' },
];

const SAVED_SEMANTICS = [
  { jiraStatusId: '1', statusName: 'To Do', class: 'Active' },
  { jiraStatusId: '3', statusName: 'Done', class: 'Done' },
];

function useDefaultHandlers(onPut: (body: unknown) => void) {
  server.use(
    http.get('/api/projects/p1/jira-statuses', () => HttpResponse.json(STATUSES)),
    http.get('/api/projects/p1/status-semantics', () => HttpResponse.json(SAVED_SEMANTICS)),
    http.put('/api/projects/p1/status-semantics', async ({ request }) => {
      onPut(await request.json());
      return new HttpResponse(null, { status: 204 });
    }),
  );
}

describe('StatusSemanticsEditor', () => {
  it('sends every classified status on save, not only the edited one', async () => {
    let body: unknown = null;
    useDefaultHandlers((b) => { body = b; });
    const user = userEvent.setup();
    renderEditor();

    // Change ONLY "In Progress" -> Active; "To Do" and "Done" stay as
    // already-saved values the user never touched this session.
    await user.selectOptions(await screen.findByLabelText(/class for in progress/i), 'Active');
    await user.click(screen.getByRole('button', { name: /save status semantics/i }));

    await waitFor(() => {
      expect(body).toEqual({
        statuses: [
          { jiraStatusId: '1', statusName: 'To Do', class: 'Active' },
          { jiraStatusId: '2', statusName: 'In Progress', class: 'Active' },
          { jiraStatusId: '3', statusName: 'Done', class: 'Done' },
        ],
      });
    });
  });

  it('flags statuses that have no class yet', async () => {
    server.use(
      http.get('/api/projects/p1/jira-statuses', () => HttpResponse.json([
        { id: '1', name: 'To Do' }, { id: '2', name: 'Done' },
      ])),
      http.get('/api/projects/p1/status-semantics', () => HttpResponse.json([
        { jiraStatusId: '1', statusName: 'To Do', class: 'Active' },
      ])),
    );
    renderEditor();

    expect(await screen.findByText(/unclassified/i)).toBeInTheDocument();
  });

  it('shows the upstream-Jira message when the live status list 502s', async () => {
    server.use(
      http.get('/api/projects/p1/jira-statuses', () => new HttpResponse(null, { status: 502 })),
      http.get('/api/projects/p1/status-semantics', () => HttpResponse.json([])),
    );
    renderEditor();

    expect(await screen.findByRole('alert')).toHaveTextContent(/jira did not respond as expected/i);
  });
});
