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
}

export const listSyncRuns = (projectId: string) =>
  apiFetch<SyncRun[]>(`/api/projects/${projectId}/sync-runs`);

export const runSync = (projectId: string) =>
  apiFetch<SyncRun>(`/api/projects/${projectId}/sync`, { method: 'POST' });

export const runRederive = (projectId: string) =>
  apiFetch<SyncRun>(`/api/projects/${projectId}/rederive`, { method: 'POST' });
