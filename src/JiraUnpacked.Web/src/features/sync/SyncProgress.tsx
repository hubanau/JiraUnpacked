import type { SyncRun } from '../../api/sync';

/**
 * What each phase is actually doing, in the reader's terms. The bar only measures Fetching —
 * the other phases have no meaningful denominator — so they say so in words rather than
 * leaving a bar parked at 100% looking like a hang.
 */
const PHASE_LABEL: Record<string, string> = {
  Counting: 'Counting issues in Jira…',
  Fetching: 'Fetching issues',
  Changelog: 'Reading change history…',
  Deriving: 'Rebuilding the model…',
};

export function phaseLabel(run: SyncRun): string {
  return (run.phase && PHASE_LABEL[run.phase]) ?? 'Working…';
}

/**
 * Progress for one Running sync.
 *
 * The numbers are the CURRENT PHASE's, not the run's. Fetching counts issues pulled from Jira;
 * reading change history counts issues whose history has been read — a phase that can run for
 * many minutes on a large project, and used to sit behind a bar frozen at 100% because the bar
 * only ever measured the fetch.
 *
 * A null total means the phase cannot say — counting, deriving, or a run that is over. That is
 * NOT zero, and the difference matters: a determinate bar drawn against a zero total is a full
 * bar, which would be a lie. Unknown gets a count of what has been done so far and no bar.
 */
export function SyncProgress({ run }: { run: SyncRun }) {
  const label = phaseLabel(run);
  const total = run.phaseTotal;
  const done = run.phaseProcessed ?? 0;

  // Jira's count is approximate and can undershoot what paging returns, so the bar is clamped
  // rather than allowed to render past its own track.
  const pct = total && total > 0 ? Math.min(100, Math.round((done / total) * 100)) : null;

  return (
    <div className="flex min-w-48 flex-col gap-1">
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-xs text-[var(--ju-muted)]">{label}</span>
        <span className="ju-numeric text-xs text-[var(--ju-muted)]">
          {total === null ? `${done}` : `${done} / ${total}`}
          {pct !== null ? ` · ${pct}%` : ''}
        </span>
      </div>

      {pct !== null && (
        <div
          className="h-1.5 w-full overflow-hidden rounded bg-[var(--ju-surface-2)]"
          role="progressbar"
          aria-valuenow={done}
          aria-valuemin={0}
          aria-valuemax={total ?? undefined}
          aria-label={label}
        >
          <div
            className="h-1.5 rounded bg-[var(--ju-primary)] transition-[width] duration-500"
            style={{ width: `${pct}%` }}
          />
        </div>
      )}
    </div>
  );
}
