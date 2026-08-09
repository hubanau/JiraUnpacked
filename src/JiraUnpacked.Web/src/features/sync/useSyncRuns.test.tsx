import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/setup';
import { SYNC_POLL_MS, useSyncRuns } from './useSyncRuns';
import type { SyncRun } from '../../api/sync';

function renderUseSyncRuns(projectId: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return renderHook(() => useSyncRuns(projectId), {
    wrapper: ({ children }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  });
}

const RUNNING_RUN: SyncRun = {
  id: 'r1',
  jiraProjectId: 'p1',
  status: 'Running',
  startedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  issuesProcessed: 0,
  error: null,
  nextRunAt: null,
  kind: 'Sync',
};

const SUCCEEDED_RUN: SyncRun = {
  ...RUNNING_RUN,
  status: 'Succeeded',
  finishedAt: '2026-01-01T00:05:00Z',
};

describe('useSyncRuns', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  // Requests are counted from inside the MSW handler and asserted as counts,
  // never as elapsed wall-clock time — a wall-clock assertion is flaky and
  // proves nothing about whether polling actually happened.

  it('polls while a run is Running', async () => {
    vi.useFakeTimers();
    let calls = 0;
    server.use(
      http.get('/api/projects/p1/sync-runs', () => {
        calls++;
        return HttpResponse.json([RUNNING_RUN]);
      }),
    );

    renderUseSyncRuns('p1');

    await act(() => vi.advanceTimersByTimeAsync(SYNC_POLL_MS * 2));

    // initial fetch + two polls at SYNC_POLL_MS and 2*SYNC_POLL_MS.
    expect(calls).toBeGreaterThanOrEqual(3);
  });

  it('stops polling once the last run finishes', async () => {
    vi.useFakeTimers();
    let calls = 0;
    server.use(
      http.get('/api/projects/p1/sync-runs', () => {
        calls++;
        // First response is Running (triggers a poll); every response after
        // that is Succeeded, so the poll scheduled from the first response
        // should be the last request that ever fires.
        return HttpResponse.json([calls === 1 ? RUNNING_RUN : SUCCEEDED_RUN]);
      }),
    );

    renderUseSyncRuns('p1');

    // Let the initial (Running) fetch resolve and the one poll it schedules
    // (which lands Succeeded) fire.
    await act(() => vi.advanceTimersByTimeAsync(SYNC_POLL_MS));
    const settled = calls;
    expect(settled).toBeGreaterThanOrEqual(2);

    // Advance several more intervals: since the last data is Succeeded, no
    // further timer should ever have been scheduled.
    await act(() => vi.advanceTimersByTimeAsync(SYNC_POLL_MS * 3));
    expect(calls).toBe(settled);
  });

  it('never polls a project with no runs', async () => {
    vi.useFakeTimers();
    let calls = 0;
    server.use(
      http.get('/api/projects/p1/sync-runs', () => {
        calls++;
        return HttpResponse.json([]);
      }),
    );

    renderUseSyncRuns('p1');

    await act(() => vi.advanceTimersByTimeAsync(SYNC_POLL_MS * 3));

    expect(calls).toBe(1);
  });
});
