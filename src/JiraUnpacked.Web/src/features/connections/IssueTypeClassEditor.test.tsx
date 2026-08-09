import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { IssueTypeClassEditor } from './IssueTypeClassEditor';

function renderEditor(connectionId: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <IssueTypeClassEditor connectionId={connectionId} />
    </QueryClientProvider>,
  );
}

describe('IssueTypeClassEditor', () => {
  it('sends every issue type on save, not just the edited one', async () => {
    let body: unknown = null;
    server.use(
      http.get('/api/connections/c1/issue-types', () => HttpResponse.json([
        { jiraIssueTypeId: '1', name: 'Story', class: 'Other' },
        { jiraIssueTypeId: '2', name: 'Epic', class: 'Other' },
        { jiraIssueTypeId: '3', name: 'Bug', class: 'Other' },
      ])),
      http.put('/api/connections/c1/issue-types', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    renderEditor('c1');

    // Change ONLY "Epic" to Epic; the other two rows stay "Other" untouched.
    await user.selectOptions(await screen.findByLabelText(/class for epic/i), 'Epic');
    await user.click(screen.getByRole('button', { name: /save issue types/i }));

    await waitFor(() => {
      expect(body).toEqual({
        issueTypes: [
          { jiraIssueTypeId: '1', class: 'Other' },
          { jiraIssueTypeId: '2', class: 'Epic' },
          { jiraIssueTypeId: '3', class: 'Other' },
        ],
      });
    });
  });
});
