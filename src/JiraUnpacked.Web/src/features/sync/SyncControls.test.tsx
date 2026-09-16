import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { SyncControls } from './SyncControls';
import { SyncRunTable } from './SyncRunTable';
import type { SyncRun } from '../../api/sync';

function renderControls() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <SyncControls projectId="p1" />
    </QueryClientProvider>,
  );
}

const NEW_RUN: SyncRun = {
  id: 'r2',
  jiraProjectId: 'p1',
  status: 'Running',
  startedAt: '2026-01-01T00:00:00Z',
  finishedAt: null,
  issuesProcessed: 0,
  error: null,
  nextRunAt: null,
  kind: 'Sync',
  totalToSync: null,
  phase: null,
  phaseProcessed: null,
  phaseTotal: null,
};

describe('SyncControls', () => {
  it('offers Stop only while something is running', async () => {
    // A stop button with nothing to stop is a trap: it can only ever report failure.
    server.use(http.get('/api/projects/p1/sync-runs', () => HttpResponse.json([])));

    renderControls();
    await waitFor(() => expect(screen.getByRole('button', { name: /sync now/i })).toBeEnabled());

    expect(screen.queryByRole('button', { name: /^stop$/i })).not.toBeInTheDocument();
  });

  it('asks the server to stop, and lets the poll report the result', async () => {
    // Stopping is a REQUEST the run notices at its next checkpoint, so the row keeps reading
    // Running until it unwinds. Showing it as stopped straight away would be a guess that is
    // wrong for as long as the run takes to notice.
    let cancelled = 0;
    server.use(
      http.get('/api/projects/p1/sync-runs', () => HttpResponse.json([NEW_RUN])),
      http.post('/api/projects/p1/sync/cancel', () => { cancelled++; return new HttpResponse(null, { status: 202 }); }),
    );

    renderControls();

    const stop = await screen.findByRole('button', { name: /^stop$/i });
    await userEvent.click(stop);

    await waitFor(() => expect(cancelled).toBe(1));
    // Still Running, because the server has not said otherwise yet.
    expect(screen.getByRole('button', { name: /running/i })).toBeInTheDocument();
  });

  it('says so when there was nothing to stop', async () => {
    // 404 means the run ended between the page rendering and the click, or belongs to a process
    // that is gone. Either way the honest answer is that nothing was stopped.
    server.use(
      http.get('/api/projects/p1/sync-runs', () => HttpResponse.json([NEW_RUN])),
      http.post('/api/projects/p1/sync/cancel', () => new HttpResponse(null, { status: 404 })),
    );

    renderControls();
    await userEvent.click(await screen.findByRole('button', { name: /^stop$/i }));

    expect(await screen.findByText(/nothing is running to stop/i)).toBeInTheDocument();
  });

  it('disables both triggers while a run is in flight, and says the work is server-side', async () => {
    // The server accepts the run and performs it in the background, so the mutation settles in
    // milliseconds and its own pending flag says nothing about whether a sync is happening. The
    // buttons follow the run table instead — otherwise the button springs back to idle instantly
    // and the obvious second press earns a 409.
    server.use(http.get('/api/projects/p1/sync-runs', () => HttpResponse.json([NEW_RUN])));

    renderControls();

    await waitFor(() =>
      expect(screen.getByRole('button', { name: /running/i })).toBeDisabled());
    expect(screen.getByRole('button', { name: /re-derive/i })).toBeDisabled();
    expect(screen.getByText(/keeps going if you close this page/i)).toBeInTheDocument();
  });

  it('leaves the triggers usable when nothing is running', async () => {
    server.use(http.get('/api/projects/p1/sync-runs', () => HttpResponse.json([])));

    renderControls();

    await waitFor(() =>
      expect(screen.getByRole('button', { name: /sync now/i })).toBeEnabled());
    expect(screen.queryByText(/keeps going if you close this page/i)).not.toBeInTheDocument();
  });

  it('shows "already running" when Sync now hits a 409', async () => {
    server.use(http.post('/api/projects/p1/sync', () => new HttpResponse(null, { status: 409 })));
    const user = userEvent.setup();
    renderControls();

    await user.click(screen.getByRole('button', { name: /sync now/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/already running/i);
  });

  it('shows the rate-limit message when Re-derive hits a 429', async () => {
    server.use(
      http.post('/api/projects/p1/rederive', () => new HttpResponse(null, { status: 429 })),
    );
    const user = userEvent.setup();
    renderControls();

    await user.click(screen.getByRole('button', { name: /re-derive/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/too many attempts/i);
  });

  it('refetches the runs after a successful sync trigger', async () => {
    let getCalls = 0;
    server.use(
      http.get('/api/projects/p1/sync-runs', () => {
        getCalls++;
        return HttpResponse.json([]);
      }),
      http.post('/api/projects/p1/sync', () => HttpResponse.json(NEW_RUN)),
    );

    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <SyncControls projectId="p1" />
        <SyncRunTable projectId="p1" />
      </QueryClientProvider>,
    );

    // Wait for the table's initial fetch so there's an active observer on
    // the syncRuns query — invalidateQueries only refetches active queries.
    await screen.findByText(/no syncs yet/i);
    const before = getCalls;

    await user.click(screen.getByRole('button', { name: /sync now/i }));

    await waitFor(() => expect(getCalls).toBeGreaterThan(before));
  });
});
