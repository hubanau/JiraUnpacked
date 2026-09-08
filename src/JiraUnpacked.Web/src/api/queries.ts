import { apiFetch } from './client';

/** A JQL query attached to a project. Queries NARROW what a sync pulls. */
export interface JiraQuery {
  id: string;
  jiraProjectId: string;
  jql: string;
  createdAtUtc: string;
}

export const listQueries = (projectId: string) =>
  apiFetch<JiraQuery[]>(`/api/projects/${projectId}/queries`);

/**
 * Adds one. The server validates the JQL against Jira before storing it and answers 400 with
 * Jira's own parse errors in the ProblemDetails detail — worth surfacing verbatim, since Jira
 * names the offending field far better than we could.
 */
export const addQuery = (projectId: string, jql: string) =>
  apiFetch<JiraQuery>(`/api/projects/${projectId}/queries`, {
    method: 'POST',
    body: { jql },
  });

export const deleteQuery = (queryId: string) =>
  apiFetch<void>(`/api/queries/${queryId}`, { method: 'DELETE' });
