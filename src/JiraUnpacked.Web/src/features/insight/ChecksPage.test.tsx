import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import ChecksPage from './ChecksPage';
import type { GapCheckResult } from '../../api/checks';
import type { Project } from '../../api/projects';

const DEFAULT_PROJECT: Project = {
  id: 'p1',
  jiraConnectionId: 'conn1',
  jiraProjectKey: 'ACME',
  jiraProjectId: '10000',
  syncIntervalMinutes: 60,
  createdAtUtc: '2026-01-01T00:00:00Z',
};

// The centrepiece fixture: an identical zero-violation payload shape used
// for both the 'Ok' (clean run) and 'NotConfigured' (never ran) states —
// only `status` (or `violations`) differs between tests below.
const okEmpty = (over: Partial<GapCheckResult> = {}): GapCheckResult => ({
  check: { id: 'epic-gaps', name: 'Epic gaps', description: 'd' },
  status: 'Ok',
  violations: { items: [], page: 1, pageSize: 50, totalCount: 0 },
  ...over,
});

/**
 * Every ChecksPage render fires the project fetch (for the NotConfigured
 * link) and the checks catalog fetch, in addition to whichever check result
 * a test cares about — so every test mocks all three (MSW's
 * onUnhandledRequest: 'error' would fail the test if one were missed). The
 * catalog always contains exactly the one check under test, so
 * selectFirstCheck can find it by its fixed "Epic gaps" name.
 */
function mockCheck(result: GapCheckResult) {
  server.use(
    http.get('/api/projects/:id', () => HttpResponse.json(DEFAULT_PROJECT)),
    http.get('/api/projects/:id/checks', () => HttpResponse.json([result.check])),
    http.get('/api/projects/:id/checks/:checkId', () => HttpResponse.json(result)),
  );
}

function renderChecksPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1/checks']}>
        <Routes>
          <Route path="/projects/:id/checks" element={<ChecksPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function selectFirstCheck() {
  renderChecksPage();
  await userEvent.click(await screen.findByRole('button', { name: 'Epic gaps' }));
}

describe('ChecksPage', () => {
  it('renders a clean run as an explicit success', async () => {
    mockCheck(okEmpty());
    await selectFirstCheck();

    expect(await screen.findByText(/no violations found/i)).toBeInTheDocument();
  });

  // The centrepiece: identical zero-violation payload, opposite meaning.
  it('renders NotConfigured as a warning that shares no copy with the clean-run success', async () => {
    mockCheck(okEmpty({ status: 'NotConfigured' }));
    await selectFirstCheck();

    expect(await screen.findByText(/could not run/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /issue type/i })).toBeInTheDocument();

    // Must NOT read as a clean result.
    expect(screen.queryByText(/no violations found/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/0 violations/i)).not.toBeInTheDocument();
  });

  it('links the NotConfigured warning to the connection issue-type editor', async () => {
    mockCheck(okEmpty({ status: 'NotConfigured' }));
    await selectFirstCheck();

    expect(await screen.findByRole('link', { name: /issue type/i }))
      .toHaveAttribute('href', '/connections/conn1');
  });

  it('treats an unrecognised status the same as NotConfigured, never as success', async () => {
    mockCheck(okEmpty({ status: 'SomeFutureStatus' }));
    await selectFirstCheck();

    expect(await screen.findByText(/could not run/i)).toBeInTheDocument();
    expect(screen.queryByText(/no violations found/i)).not.toBeInTheDocument();
  });

  it('lists violations and links each to its issue detail', async () => {
    mockCheck(okEmpty({ violations: { items: [{
      jiraIssueId: 'i1', jiraKey: 'ACME-1', summary: 'Epic', status: 'Done',
      class: 'Done', issueType: 'Epic', children: [],
    }], page: 1, pageSize: 50, totalCount: 1 } }));
    await selectFirstCheck();

    expect(await screen.findByRole('link', { name: /ACME-1/ }))
      .toHaveAttribute('href', '/projects/p1/issues/i1');
  });

  it('renders nullable violation fields as an em dash, never the string "null"', async () => {
    mockCheck(okEmpty({ violations: { items: [{
      jiraIssueId: 'i1', jiraKey: 'ACME-1', summary: null, status: null,
      class: 'Done', issueType: null, children: [],
    }], page: 1, pageSize: 50, totalCount: 1 } }));
    await selectFirstCheck();

    await screen.findByRole('link', { name: /ACME-1/ });
    expect(screen.getAllByText('—').length).toBeGreaterThanOrEqual(3);
    expect(screen.queryByText('null')).not.toBeInTheDocument();
  });

  it('expands a violation to show its offending children', async () => {
    mockCheck(okEmpty({ violations: { items: [{
      jiraIssueId: 'i1', jiraKey: 'ACME-1', summary: 'Epic', status: 'Done',
      class: 'Done', issueType: 'Epic',
      children: [
        { jiraIssueId: 'c1', jiraKey: 'ACME-2', summary: 'Open child', status: 'To Do', class: 'Active' },
        { jiraIssueId: 'c2', jiraKey: 'ACME-3', summary: 'Another', status: 'To Do', class: 'Active' },
      ],
    }], page: 1, pageSize: 50, totalCount: 1 } }));
    await selectFirstCheck();
    await screen.findByText('ACME-1');

    // Children are the evidence for the violation, so they must be reachable.
    expect(screen.queryByText('ACME-2')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /show children|expand/i }));

    expect(await screen.findByText('ACME-2')).toBeInTheDocument();
    expect(screen.getByText('ACME-3')).toBeInTheDocument();
  });
});
