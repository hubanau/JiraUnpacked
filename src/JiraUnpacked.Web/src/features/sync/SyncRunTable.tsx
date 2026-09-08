import { Badge, EmptyState, ErrorState, Spinner, TBody, TD, TH, THead, TR, Table } from '../../ui';
import { ApiError } from '../../api/errors';
import type { SyncRun, SyncRunStatus } from '../../api/sync';
import { useSyncRuns } from './useSyncRuns';
import { SyncProgress } from './SyncProgress';

const STATUS_TONE: Record<SyncRunStatus, 'neutral' | 'success' | 'warning' | 'danger'> = {
  Running: 'neutral',
  Succeeded: 'success',
  Failed: 'danger',
  PartiallyFailed: 'warning',
};

function formatDuration(run: SyncRun): string {
  if (!run.finishedAt) return 'running';
  const totalSeconds = Math.max(
    0,
    Math.round((new Date(run.finishedAt).getTime() - new Date(run.startedAt).getTime()) / 1000),
  );
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return minutes > 0 ? `${minutes}m ${seconds}s` : `${seconds}s`;
}

export function SyncRunTable({ projectId }: { projectId: string }) {
  const { data, isPending, error } = useSyncRuns(projectId);

  if (isPending) return <Spinner aria-label="Loading sync runs" />;
  if (error) {
    return (
      <ErrorState title={error instanceof ApiError ? error.title : 'Could not load sync runs.'} />
    );
  }
  if (data.length === 0) return <EmptyState title="No syncs yet" />;

  return (
    <Table>
      <THead>
        <TR>
          <TH>Kind</TH>
          <TH>Status</TH>
          <TH>Started</TH>
          <TH>Duration</TH>
          <TH>Issues</TH>
          <TH>Error</TH>
        </TR>
      </THead>
      <TBody>
        {data.map((run) => (
          <TR key={run.id}>
            <TD>{run.kind}</TD>
            <TD>
              <Badge tone={STATUS_TONE[run.status]} className="items-center gap-1">
                {run.status === 'Running' && <Spinner aria-label="Running" />}
                {run.status}
              </Badge>
            </TD>
            <TD>{new Date(run.startedAt).toLocaleString()}</TD>
            <TD>{formatDuration(run)}</TD>
            <TD>
              {run.status === 'Running'
                ? <SyncProgress run={run} />
                : run.issuesProcessed}
            </TD>
            <TD>{run.error ?? '—'}</TD>
          </TR>
        ))}
      </TBody>
    </Table>
  );
}
