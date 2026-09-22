import { apiFetch } from './client';

export interface Connection {
  id: string;
  baseUrl: string;
  email: string;
  createdAtUtc: string;
}

export interface JiraField {
  id: string;
  name: string;
  custom: boolean;
}

export interface JiraProjectSummary {
  id: string;
  key: string;
  name: string;
}

export const listConnections = () => apiFetch<Connection[]>('/api/connections');

export const getConnection = (id: string) => apiFetch<Connection>(`/api/connections/${id}`);

export const createConnection = (baseUrl: string, email: string, apiToken: string) =>
  apiFetch<Connection>('/api/connections', {
    method: 'POST', body: { baseUrl, email, apiToken },
  });

export const listConnectionFields = (id: string) =>
  apiFetch<JiraField[]>(`/api/connections/${id}/fields`);

export const listAvailableProjects = (id: string) =>
  apiFetch<JiraProjectSummary[]>(`/api/connections/${id}/projects-available`);

/**
 * Replaces the stored API token (and the email, when given). The server checks the new pair
 * against Jira before storing anything, so a 400 here means Jira refused it and the old token
 * is still in place. The token is never returned.
 */
export const updateConnectionCredentials = (id: string, email: string, apiToken: string) =>
  apiFetch<Connection>(`/api/connections/${id}`, {
    method: 'PATCH', body: { email, apiToken },
  });

/** Deletes the connection, its projects and all of their data. 409 while any of them is syncing. */
export const deleteConnection = (id: string) =>
  apiFetch<void>(`/api/connections/${id}`, { method: 'DELETE' });
