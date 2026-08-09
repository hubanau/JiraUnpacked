import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import ProjectDetail from '../projects/ProjectDetail';

const PROJECT = {
  id: 'p1',
  jiraConnectionId: 'c1',
  jiraProjectKey: 'ACME',
  jiraProjectId: '100',
  syncIntervalMinutes: 60,
  createdAtUtc: '2026-01-01T00:00:00Z',
};

const STATUSES = [
  { id: '1', name: 'To Do' },
  { id: '2', name: 'Done' },
];

function renderProjectDetail() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1']}>
        <Routes>
          <Route path="/projects/:id" element={<ProjectDetail />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

// Setup deliberately left incomplete: no fields mapped, and only one of two
// statuses classified ("Done" is missing). Sync must stay enabled regardless.
function useIncompleteSetupHandlers() {
  server.use(
    http.get('/api/projects/p1', () => HttpResponse.json(PROJECT)),
    http.get('/api/projects/p1/field-mappings', () => HttpResponse.json([])),
    http.get('/api/projects/p1/jira-statuses', () => HttpResponse.json(STATUSES)),
    http.get('/api/projects/p1/status-semantics', () => HttpResponse.json([
      { jiraStatusId: '1', statusName: 'To Do', class: 'Active' },
    ])),
    http.get('/api/connections/c1/issue-types', () => HttpResponse.json([
      { jiraIssueTypeId: '1', name: 'Story', class: 'Other' },
    ])),
    http.get('/api/projects/p1/sync-runs', () => HttpResponse.json([])),
  );
}

describe('ReadinessChecklist (mounted in ProjectDetail)', () => {
  it('does not disable the sync button when setup is incomplete', async () => {
    useIncompleteSetupHandlers();
    renderProjectDetail();

    expect(await screen.findByText(/unclassified/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /sync now/i })).toBeEnabled();
  });

  it('names the unclassified statuses so the user knows what to fix', async () => {
    useIncompleteSetupHandlers();
    renderProjectDetail();

    // Exactly one status ("Done") is missing here, so the singular form
    // applies — this also pins the pluralisation, not just that some
    // element somewhere contains the word "Done".
    expect(await screen.findByText(/1 status unclassified: Done/i)).toBeInTheDocument();
  });
});
