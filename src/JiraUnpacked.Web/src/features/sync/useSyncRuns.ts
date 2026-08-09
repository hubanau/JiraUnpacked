import { useQuery } from '@tanstack/react-query';
import { listSyncRuns, type SyncRun } from '../../api/sync';
import { queryKeys } from '../../api/queryKeys';

/** 3s while any run is Running; stops entirely when none is. A fixed
 *  forever-poll would hammer the API for a table that never changes. */
export const SYNC_POLL_MS = 3000;

export function useSyncRuns(projectId: string) {
  return useQuery({
    queryKey: queryKeys.syncRuns(projectId),
    queryFn: () => listSyncRuns(projectId),
    refetchInterval: (query) => {
      const runs = query.state.data as SyncRun[] | undefined;
      return runs?.some((r) => r.status === 'Running') ? SYNC_POLL_MS : false;
    },
  });
}
