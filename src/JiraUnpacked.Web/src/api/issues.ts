import { apiDownload, apiFetch, type Download } from './client';

export type FilterOperator =
  | 'Eq' | 'Neq' | 'In' | 'Lt' | 'Lte' | 'Gt' | 'Gte' | 'Contains' | 'StartsWith' | 'IsNull';

export type FieldType = 'DictName' | 'Decimal' | 'DateTime' | 'Text' | 'ParentRef';

export interface FilterFieldInfo {
  name: string;
  type: FieldType;
  operators: FilterOperator[];
}

/**
 * The server's closed filter-field allow-list (GET /api/filter-catalog).
 * Static and tenant-independent, so the query that fetches it (see
 * FilterBuilder) is given `staleTime: Infinity` — fetched once per page
 * load and reused across every mount, never mirrored client-side, so drift
 * between what the UI offers and what the server accepts is impossible
 * rather than merely detected.
 */
export interface FilterCatalog {
  fields: FilterFieldInfo[];
  maxConditions: number;
  maxInValues: number;
  /**
   * Guards on the boolean-group shape (`filter`). The builder below sends flat
   * `conditions`, which the server still accepts unchanged, so nothing here is
   * read yet — they are typed so a future tree editor sizes itself from the
   * server rather than re-introducing the hand-maintained mirror this endpoint
   * exists to have deleted.
   */
  maxGroups: number;
  maxDepth: number;
}

/** How a FilterGroup joins its parts — its conditions and its nested groups alike. */
export type FilterLogic = 'And' | 'Or';

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

/**
 * A boolean group, sent as `filter` instead of `conditions` (never both — the
 * server returns 400 rather than merging them). A group must contain at least
 * one part: an empty And matches everything and an empty Or matches nothing,
 * so the server refuses both rather than guessing.
 */
export interface FilterGroup {
  logic: FilterLogic;
  conditions?: FilterCondition[];
  groups?: FilterGroup[];
}

/** `conditions` (flat AND) or `filter` (boolean tree) — never both. */
export interface IssueFilterRequest {
  conditions?: FilterCondition[];
  filter?: FilterGroup;
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

export const getFilterCatalog = () => apiFetch<FilterCatalog>('/api/filter-catalog');

/** The values a dictionary field actually takes in this project. 400 for any other field. */
export const getFilterValues = (projectId: string, field: string) =>
  apiFetch<{ field: string; values: string[] }>(
    `/api/projects/${projectId}/filter-values?field=${encodeURIComponent(field)}`);

export interface ExportView {
  conditions: FilterCondition[];
  logic: FilterLogic;
  sortBy: string;
  sortDescending: boolean;
  columns: string[];
}

/**
 * The CSV of the view currently on screen. Fetched with the session rather than linked to — see
 * apiDownload for why a link cannot work. The server caps the export's size, which is what makes
 * holding it in memory acceptable.
 */
export const exportIssuesCsv = (projectId: string, view: ExportView): Promise<Download> => {
  const params = new URLSearchParams();
  if (view.conditions.length > 0) {
    params.set('filter', JSON.stringify(view.conditions));
    if (view.logic === 'Or') params.set('logic', 'Or');
  }
  params.set('sortBy', view.sortBy);
  params.set('sortDescending', String(view.sortDescending));
  params.set('cols', view.columns.join(','));
  return apiDownload(`/api/projects/${projectId}/issues/export.csv?${params.toString()}`);
};
