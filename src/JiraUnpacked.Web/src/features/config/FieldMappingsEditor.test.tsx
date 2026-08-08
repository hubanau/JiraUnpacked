import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { FieldMappingsEditor } from './FieldMappingsEditor';

function renderEditor() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <FieldMappingsEditor projectId="p1" connectionId="c1" />
    </QueryClientProvider>,
  );
}

const FIELDS = [
  { id: 'status', name: 'Status', custom: false },
  { id: 'assignee', name: 'Assignee', custom: false },
  { id: 'cf_10001', name: 'Story Points', custom: true },
];

const SAVED_MAPPINGS = [
  { role: 'Status', jiraFieldId: 'status' },
  { role: 'Assignee', jiraFieldId: 'assignee' },
];

function useDefaultHandlers(onPut: (body: unknown) => void) {
  server.use(
    http.get('/api/connections/c1/fields', () => HttpResponse.json(FIELDS)),
    http.get('/api/projects/p1/field-mappings', () => HttpResponse.json(SAVED_MAPPINGS)),
    http.put('/api/projects/p1/field-mappings', async ({ request }) => {
      onPut(await request.json());
      return new HttpResponse(null, { status: 204 });
    }),
  );
}

describe('FieldMappingsEditor', () => {
  it('sends every mapped role on save, not only the edited one', async () => {
    let body: unknown = null;
    useDefaultHandlers((b) => { body = b; });
    const user = userEvent.setup();
    renderEditor();

    // Change ONLY StoryPoints -> cf_10001; Status and Assignee stay as
    // already-saved values the user never touched this session.
    await user.selectOptions(await screen.findByLabelText('StoryPoints'), 'cf_10001');
    await user.click(screen.getByRole('button', { name: /save field mappings/i }));

    await waitFor(() => {
      expect(body).toEqual({
        mappings: [
          { role: 'Status', jiraFieldId: 'status' },
          { role: 'Assignee', jiraFieldId: 'assignee' },
          { role: 'StoryPoints', jiraFieldId: 'cf_10001' },
        ],
      });
    });
  });

  it('filters options by name and submits the field id, not the label', async () => {
    let body: unknown = null;
    useDefaultHandlers((b) => { body = b; });
    const user = userEvent.setup();
    renderEditor();
    await screen.findByLabelText('StoryPoints');

    await user.type(screen.getByLabelText(/filter fields/i), 'story');

    // The filter is shared across every role's select, so "Story Points"
    // legitimately appears in all five once narrowed — scope to the row
    // under test. "Assignee" should be narrowed out everywhere, so that
    // check stays unscoped.
    const storyPointsSelect = screen.getByLabelText('StoryPoints');
    expect(within(storyPointsSelect).getByRole('option', { name: 'Story Points' })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'Assignee' })).not.toBeInTheDocument();

    // ...and the value submitted is the id, never the visible label.
    await user.selectOptions(storyPointsSelect, 'cf_10001');
    await user.click(screen.getByRole('button', { name: /save field mappings/i }));

    await waitFor(() => {
      expect(body).toMatchObject({
        mappings: expect.arrayContaining([{ role: 'StoryPoints', jiraFieldId: 'cf_10001' }]),
      });
    });
    expect(JSON.stringify(body)).not.toContain('Story Points');
  });

  it('shows an error and does not crash when the field list fails to load', async () => {
    server.use(
      http.get('/api/connections/c1/fields', () => new HttpResponse(null, { status: 500 })),
      http.get('/api/projects/p1/field-mappings', () => HttpResponse.json(SAVED_MAPPINGS)),
    );
    renderEditor();

    expect(await screen.findByRole('alert')).toHaveTextContent(/could not load fields/i);
  });
});
