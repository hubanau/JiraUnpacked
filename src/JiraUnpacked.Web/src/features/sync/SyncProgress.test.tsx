import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { SyncRun } from '../../api/sync';
import { SyncProgress } from './SyncProgress';

const run = (over: Partial<SyncRun> = {}): SyncRun => ({
  id: 'r1', jiraProjectId: 'p1', status: 'Running',
  startedAt: '2026-09-08T10:00:00Z', finishedAt: null,
  issuesProcessed: 120, error: null, nextRunAt: null, kind: 'Sync',
  totalToSync: 480, phase: 'Fetching',
  phaseProcessed: 120, phaseTotal: 480, ...over,
});

describe('SyncProgress', () => {
  it('shows how far through it is, as a bar and as numbers', () => {
    render(<SyncProgress run={run()} />);

    expect(screen.getByText('120 / 480 · 25%')).toBeInTheDocument();
    const bar = screen.getByRole('progressbar');
    expect(bar).toHaveAttribute('aria-valuenow', '120');
    expect(bar).toHaveAttribute('aria-valuemax', '480');
  });

  it('draws no bar when the total is unknown', () => {
    // Null is not zero. A determinate bar against a zero total renders as FULL, which would
    // claim a finished sync — so an unknown total gets a count and no bar at all.
    render(<SyncProgress run={run({ phaseTotal: null })} />);

    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    expect(screen.getByText('120')).toBeInTheDocument();
  });

  it('never renders past 100% when Jira undercounts', () => {
    // The count is Jira's approximation and can undershoot what paging actually returns; the bar
    // is clamped rather than allowed to overflow its own track.
    render(<SyncProgress run={run({ phaseProcessed: 500, phaseTotal: 480 })} />);

    expect(screen.getByText('500 / 480 · 100%')).toBeInTheDocument();
  });

  it('counts the phase it is in, not the fetch it already finished', () => {
    // The whole point of the change: reading change history is its own long phase with its own
    // denominator. Before this the bar showed the FETCH numbers throughout, so it sat frozen at
    // 100% for the many minutes this phase can take, which reads as a hang.
    render(<SyncProgress run={run({
      phase: 'Changelog', issuesProcessed: 92673, totalToSync: 92673,
      phaseProcessed: 12000, phaseTotal: 92673,
    })} />);

    expect(screen.getByText(/change history/i)).toBeInTheDocument();
    expect(screen.getByText('12000 / 92673 · 13%')).toBeInTheDocument();
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '12000');
  });

  it('says what the run is doing in each phase', () => {
    // The bar only measures fetching. Without a phase label the later phases would leave it
    // parked at 100%, which reads as a hang rather than as work still happening.
    const { rerender } = render(<SyncProgress run={run({ phase: 'Counting' })} />);
    expect(screen.getByText(/counting issues/i)).toBeInTheDocument();

    rerender(<SyncProgress run={run({ phase: 'Changelog' })} />);
    expect(screen.getByText(/change history/i)).toBeInTheDocument();

    rerender(<SyncProgress run={run({ phase: 'Deriving' })} />);
    expect(screen.getByText(/rebuilding the model/i)).toBeInTheDocument();
  });

  it('falls back to a neutral label for a phase it does not recognise', () => {
    // A run written by a newer build must not render "undefined" in an older tab.
    render(<SyncProgress run={run({ phase: 'Something-New' })} />);
    expect(screen.getByText('Working…')).toBeInTheDocument();
  });
});
