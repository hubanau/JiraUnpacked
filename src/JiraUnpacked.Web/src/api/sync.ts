import { apiFetch } from './client';

/** Cancelled is not Failed: someone stopped this run on purpose, and it is not coloured as a
 *  fault — red rows should mean something went wrong. */
export type SyncRunStatus = 'Running' | 'Succeeded' | 'Failed' | 'PartiallyFailed' | 'Cancelled';

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
  /** Progress within the CURRENT phase. Null when the phase cannot say, or the run is over —
   *  each phase counts something different, so these reset as the run moves between them. */
  phaseProcessed: number | null;
  phaseTotal: number | null;
}

export const listSyncRuns = (projectId: string) =>
  apiFetch<SyncRun[]>(`/api/projects/${projectId}/sync-runs`);

export const runSync = (projectId: string) =>
  apiFetch<SyncRun>(`/api/projects/${projectId}/sync`, { method: 'POST' });

export const runRederive = (projectId: string) =>
  apiFetch<SyncRun>(`/api/projects/${projectId}/rederive`, { method: 'POST' });

/** Asks the running sync to stop. 202: it stops at its next checkpoint, not instantly, so the
 *  row keeps reading Running until it unwinds — the poll picks the change up. */
export const cancelSync = (projectId: string) =>
  apiFetch<void>(`/api/projects/${projectId}/sync/cancel`, { method: 'POST' });
