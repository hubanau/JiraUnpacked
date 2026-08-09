import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Card } from '../../ui';
import { ApiError } from '../../api/errors';
import { runRederive, runSync } from '../../api/sync';
import { queryKeys } from '../../api/queryKeys';

// Both triggers land here: 409 means a run is already in flight for this
// project (regardless of which kind started it), and anything else already
// carries a user-facing message on ApiError.title (429's rate-limit text
// included — see toApiError).
function syncError(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Could not start the sync.';
  if (error.status === 409) return 'A sync is already running for this project.';
  return error.title;
}

export function SyncControls({ projectId }: { projectId: string }) {
  const queryClient = useQueryClient();

  // No optimistic updates: the table is the source of truth, so a
  // successful trigger just invalidates it. Invalidating also restarts
  // polling, since the refetch will pick up the new Running row.
  const invalidateRuns = () =>
    queryClient.invalidateQueries({ queryKey: queryKeys.syncRuns(projectId) });

  const sync = useMutation({
    mutationFn: () => runSync(projectId),
    onSuccess: () => {
      void invalidateRuns();
    },
  });

  const rederive = useMutation({
    mutationFn: () => runRederive(projectId),
    onSuccess: () => {
      void invalidateRuns();
    },
  });

  return (
    <Card className="flex flex-col gap-4">
      {sync.error && (
        <p role="alert" className="text-sm text-[var(--ju-danger)]">
          {syncError(sync.error)}
        </p>
      )}
      {rederive.error && (
        <p role="alert" className="text-sm text-[var(--ju-danger)]">
          {syncError(rederive.error)}
        </p>
      )}

      <div className="flex flex-wrap items-center gap-3">
        <Button loading={sync.isPending} onClick={() => sync.mutate()}>
          Sync now
        </Button>

        <Button variant="secondary" loading={rederive.isPending} onClick={() => rederive.mutate()}>
          Re-derive
        </Button>
      </div>

      <p className="text-xs text-[var(--ju-muted)]">
        Rebuild the modelled data from what was already pulled from Jira. Use this after changing
        field mappings or status semantics.
      </p>
    </Card>
  );
}
