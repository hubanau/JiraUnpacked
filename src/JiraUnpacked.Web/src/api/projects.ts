import { apiFetch } from './client';

export interface Project {
  id: string;
  jiraConnectionId: string;
  jiraProjectKey: string;
  jiraProjectId: string;
  syncIntervalMinutes: number;
  createdAtUtc: string;
}

/** The server rejects anything lower; mirrored here only as a client-side hint. */
export const MIN_SYNC_INTERVAL_MINUTES = 60;

export const listConnectionProjects = (connectionId: string) =>
  apiFetch<Project[]>(`/api/connections/${connectionId}/projects`);

export const getProject = (projectId: string) =>
  apiFetch<Project>(`/api/projects/${projectId}`);

export const addProject = (connectionId: string, projectKey: string, syncIntervalMinutes: number) =>
  apiFetch<Project>(`/api/connections/${connectionId}/projects`, {
    method: 'POST', body: { projectKey, syncIntervalMinutes },
  });

export const updateSyncInterval = (projectId: string, syncIntervalMinutes: number) =>
  apiFetch<void>(`/api/projects/${projectId}`, {
    method: 'PATCH', body: { syncIntervalMinutes },
  });
