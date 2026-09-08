import { apiFetch } from './client';

export type SyncRunStatus = 'Running' | 'Succeeded' | 'Failed' | 'PartiallyFailed';

export interface SyncRun {
  id: string;
  jiraProjectId: string;
  status: SyncRunStatus;
  startedAt: string;
  finishedAt: string | null;
  issuesProcessed: number;
  error: string | null;
  nextRunAt: string | null;
  kind: string;
  /** Issues the run expects to fetch. Null means unknown — NOT zero: the count call can fail,
   *  and a rederive fetches nothing at all. A bar must render the two differently. */
  totalToSync: number | null;
  /** What a Running run is doing: Counting, Fetching, Changelog, Deriving. Null once it ends. */
  phase: string | null;
}

export const listSyncRuns = (projectId: string) =>
  apiFetch<SyncRun[]>(`/api/projects/${projectId}/sync-runs`);

export const runSync = (projectId: string) =>
  apiFetch<SyncRun>(`/api/projects/${projectId}/sync`, { method: 'POST' });

export const runRederive = (projectId: string) =>
  apiFetch<SyncRun>(`/api/projects/${projectId}/rederive`, { method: 'POST' });
