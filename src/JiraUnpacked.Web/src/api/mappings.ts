import { apiFetch } from './client';

export type FieldRole = 'Team' | 'Assignee' | 'Sprint' | 'StoryPoints' | 'Status';
export type StatusClass = 'Active' | 'Done' | 'Other';
export type IssueTypeClass = 'Other' | 'Epic';

/** The five roles, in the order the editor renders them. */
export const FIELD_ROLES: readonly FieldRole[] = ['Status', 'Assignee', 'Team', 'Sprint', 'StoryPoints'];
export const STATUS_CLASSES: readonly StatusClass[] = ['Active', 'Done', 'Other'];

export interface JiraStatus { id: string; name: string }
export interface FieldMapping { role: FieldRole; jiraFieldId: string }
export interface StatusSemantic { jiraStatusId: string; statusName: string; class: StatusClass }
export interface IssueTypeClassInfo { jiraIssueTypeId: string; name: string; class: IssueTypeClass }

export const listJiraStatuses = (projectId: string) =>
  apiFetch<JiraStatus[]>(`/api/projects/${projectId}/jira-statuses`);

export const getFieldMappings = (projectId: string) =>
  apiFetch<FieldMapping[]>(`/api/projects/${projectId}/field-mappings`);

/** Replace-all: `mappings` must be the COMPLETE set, not a patch. */
export const putFieldMappings = (projectId: string, mappings: FieldMapping[]) =>
  apiFetch<void>(`/api/projects/${projectId}/field-mappings`, {
    method: 'PUT', body: { mappings },
  });

export const getStatusSemantics = (projectId: string) =>
  apiFetch<StatusSemantic[]>(`/api/projects/${projectId}/status-semantics`);

/** Replace-all: `statuses` must be the COMPLETE set, not a patch. */
export const putStatusSemantics = (projectId: string, statuses: StatusSemantic[]) =>
  apiFetch<void>(`/api/projects/${projectId}/status-semantics`, {
    method: 'PUT', body: { statuses },
  });

export const getIssueTypes = (connectionId: string) =>
  apiFetch<IssueTypeClassInfo[]>(`/api/connections/${connectionId}/issue-types`);

/** Replace-all: `issueTypes` must be the COMPLETE set, not a patch. */
export const putIssueTypes = (
  connectionId: string,
  issueTypes: { jiraIssueTypeId: string; class: IssueTypeClass }[],
) =>
  apiFetch<void>(`/api/connections/${connectionId}/issue-types`, {
    method: 'PUT', body: { issueTypes },
  });
