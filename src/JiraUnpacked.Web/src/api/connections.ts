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
