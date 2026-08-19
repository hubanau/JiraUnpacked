import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
// Imported under an alias distinct from the type below purely so this test
// file's own imports don't collide; the component itself still exports as
// `IssueDetail` (see the alias handling inside IssueDetail.tsx).
import IssueDetailScreen from './IssueDetail';
import type { IssueDetail } from '../../api/issues';

function renderDetail() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1/issues/i1']}>
        <Routes>
          <Route path="/projects/:id/issues/:jiraIssueId" element={<IssueDetailScreen />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const detail = (over: Partial<IssueDetail> = {}): IssueDetail => ({
  issue: { jiraIssueId: 'i1', jiraKey: 'ACME-1', summary: 'Login is broken', status: 'Done',
    issueType: 'Bug', priority: null, assignee: 'Sam', sprint: null, team: null,
    storyPoints: 3, parentJiraIssueId: 'i0',
    jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-02-01T00:00:00Z' },
  parent: { jiraIssueId: 'i0', jiraKey: 'ACME-0', summary: 'Epic' },
  children: [{ jiraIssueId: 'i2', jiraKey: 'ACME-2', summary: 'Sub' }],
  changeHistory: [],
  ...over,
});

describe('IssueDetail', () => {
  it('links the parent and each child to their own detail route', async () => {
    // ids chosen to require encoding (a space and a slash) — 'i0'/'i2'
    // round-trip identically through encodeURIComponent whether or not it
    // is actually applied, so they would not catch a dropped encoding.
    server.use(http.get('/api/projects/p1/issues/i1', () => HttpResponse.json(detail({
      parent: { jiraIssueId: 'a b', jiraKey: 'ACME-0', summary: 'Epic' },
      children: [{ jiraIssueId: 'c/d', jiraKey: 'ACME-2', summary: 'Sub' }],
    }))));
    renderDetail();

    expect(await screen.findByRole('link', { name: /ACME-0/ }))
      .toHaveAttribute('href', '/projects/p1/issues/a%20b');
    expect(screen.getByRole('link', { name: /ACME-2/ }))
      .toHaveAttribute('href', '/projects/p1/issues/c%2Fd');
  });

  it('says there are no recorded changes rather than showing an error', async () => {
    server.use(http.get('/api/projects/p1/issues/i1', () =>
      HttpResponse.json(detail({ changeHistory: [] }))));
    renderDetail();

    expect(await screen.findByText(/no recorded changes/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('renders a null fromValue as a dash, never the string "null"', async () => {
    server.use(http.get('/api/projects/p1/issues/i1', () => HttpResponse.json(detail({
      changeHistory: [{ field: 'status', fromValue: null, toValue: 'Done',
        author: null, changedAt: '2026-01-15T00:00:00Z' }],
    }))));
    renderDetail();

    await screen.findByText('Done');
    expect(screen.queryByText('null')).not.toBeInTheDocument();
    expect(screen.getAllByText('—').length).toBeGreaterThanOrEqual(2); // fromValue and author
  });

  it('omits badges for null dictionary values', async () => {
    // React renders `null` as nothing, so an *unfiltered* badge would
    // render empty and queryByText('null') would still pass — assert
    // exactly which badges exist instead. The default fixture's issue has
    // status/issueType/assignee/storyPoints set and priority/sprint/team
    // null, so exactly those four (and none of the null three) must
    // render as badges (each Badge carries its label as `title`).
    server.use(http.get('/api/projects/p1/issues/i1', () => HttpResponse.json(detail())));
    renderDetail();

    await screen.findByText('Done');
    expect(screen.getByTitle('Status')).toHaveTextContent('Done');
    expect(screen.getByTitle('Type')).toHaveTextContent('Bug');
    expect(screen.getByTitle('Assignee')).toHaveTextContent('Sam');
    expect(screen.getByTitle('Points')).toHaveTextContent('3');
    expect(screen.queryByTitle('Priority')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Sprint')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Team')).not.toBeInTheDocument();
  });
});
