import { apiFetch } from './client';

export type FilterOperator =
  | 'Eq' | 'Neq' | 'In' | 'Lt' | 'Lte' | 'Gt' | 'Gte' | 'Contains' | 'StartsWith' | 'IsNull';

/**
 * Mirrors the API's FilterCondition. `value` and `values` are mutually
 * exclusive: single-value operators send `value`, `In` sends `values`, and
 * `IsNull` sends neither. Sending the wrong one is accepted by TypeScript
 * and rejected by the server at runtime, so the builder must get it right.
 */
export interface FilterCondition {
  field: string;
  op: FilterOperator;
  value?: string | number;
  values?: (string | number)[];
}

export interface IssueFilterRequest {
  conditions?: FilterCondition[];
  page?: number;
  pageSize?: number;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** Dictionary fields arrive as resolved display names, not ids. Most are nullable. */
export interface IssueListItem {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
  status: string | null;
  issueType: string | null;
  priority: string | null;
  assignee: string | null;
  sprint: string | null;
  team: string | null;
  storyPoints: number | null;
  parentJiraIssueId: string | null;
  jiraCreatedAt: string;
  jiraUpdatedAt: string;
}

export interface IssueRef {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
}

export interface ChangeHistoryEntry {
  field: string;
  fromValue: string | null;
  toValue: string | null;
  author: string | null;
  changedAt: string;
}

export interface IssueDetail {
  issue: IssueListItem;
  parent: IssueRef | null;
  children: IssueRef[];
  changeHistory: ChangeHistoryEntry[];
}

export const MAX_PAGE_SIZE = 100;
export const DEFAULT_PAGE_SIZE = 50;

/**
 * The only list read this spec uses. POST /issues/query subsumes GET /issues:
 * FR-E4 guarantees empty/null conditions return the unfiltered page with the
 * same ordering and paging, so a second GET code path would add surface
 * without adding behaviour.
 */
export const queryIssues = (projectId: string, request: IssueFilterRequest) =>
  apiFetch<PagedResult<IssueListItem>>(`/api/projects/${projectId}/issues/query`, {
    method: 'POST', body: request,
  });

export const getIssueDetail = (projectId: string, jiraIssueId: string) =>
  apiFetch<IssueDetail>(`/api/projects/${projectId}/issues/${encodeURIComponent(jiraIssueId)}`);
