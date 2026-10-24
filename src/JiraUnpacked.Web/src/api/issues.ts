import { apiDownload, apiFetch, type Download } from './client';

export type FilterOperator =
  | 'Eq' | 'Neq' | 'In' | 'NotIn' | 'Lt' | 'Lte' | 'Gt' | 'Gte'
  | 'Contains' | 'NotContains' | 'StartsWith' | 'IsNull' | 'IsNotNull'
  | 'Between' | 'InLast' | 'NotInLast' | 'InPeriod'
  | 'Was' | 'Changed' | 'ChangeCount';

export type FieldType = 'DictName' | 'Decimal' | 'DateTime' | 'Text' | 'ParentRef' | 'StatusCategory' | 'History';

/** "in the last" / "more than … ago" take `value: { amount, unit }`. Months are calendar months. */
export type RelativeUnit = 'hours' | 'days' | 'weeks' | 'months';
export interface RelativeAmount {
  amount: number;
  unit: RelativeUnit;
}

/** "in period" takes `value: { period, timeZone }`. Weeks start Monday; the zone is IANA, missing = UTC. */
export type PeriodKey =
  | 'today' | 'yesterday' | 'thisWeek' | 'lastWeek' | 'thisMonth' | 'lastMonth'
  | 'thisQuarter' | 'lastQuarter' | 'thisYear' | 'lastYear';
export interface PeriodValue {
  period: PeriodKey;
  timeZone?: string;
}

/**
 * A sprint resolved from its state when the query runs. An object, never a string, so it cannot
 * collide with a sprint someone NAMED "active".
 */
export type SmartSprintKey = 'active' | 'future' | 'open' | 'closed';
export interface SmartValue {
  smart: SmartSprintKey;
}

/** "number of changes" compares the count with one of these. */
export type HistoryCompare = 'Eq' | 'Neq' | 'Gt' | 'Gte' | 'Lt' | 'Lte';

/** A history condition's `during`: the same three shapes the date operators take. */
export type HistoryWindow =
  | { kind: 'inLast'; amount: number; unit: RelativeUnit }
  | { kind: 'inPeriod'; period: PeriodKey; timeZone?: string }
  | { kind: 'between'; from: string | null; to: string | null };

/**
 * The `history` field's value. "was": `values`; "changed": optional `from`, `to`, `by` and
 * `during`; "number of changes": `compare`, `count` and an optional `during`. null in a list is
 * (Empty). `historyField` is the name Jira reports ("status", "Sprint"), matched exactly.
 */
export interface HistoryValue {
  historyField: string;
  values?: (string | null)[];
  from?: (string | null)[];
  to?: (string | null)[];
  by?: string[];
  compare?: HistoryCompare;
  count?: number;
  during?: HistoryWindow;
}

/** An element of `values`: a name or number, null for (Empty), or a smart sprint value. Between: [from, to], null = open. */
export type ListValue = string | number | null | SmartValue;

/** `value`: a name, number or date — null for (Empty) in the editor only — or one of the object shapes above. */
export type ConditionValue = string | number | null | SmartValue | RelativeAmount | PeriodValue | HistoryValue;

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
  /** Guards on the boolean-group shape; the tree editor sizes itself from these. */
  maxGroups: number;
  maxDepth: number;
  /** Enabled history conditions a filter may hold — each is a subquery over the changelog. */
  maxHistoryConditions: number;
}

/** How a FilterGroup joins its parts — its conditions and its nested groups alike. */
export type FilterLogic = 'And' | 'Or';

/**
 * Mirrors the API's FilterCondition. `value` and `values` are mutually exclusive: single-value
 * operators send `value` (an object for InLast/NotInLast/InPeriod, or a smart sprint value on
 * Eq/Neq), `In`/`NotIn` send `values` (null = (Empty)), `Between` sends `values: [from, to]`
 * (null = open), and `IsNull`/`IsNotNull` send neither. A `value` of null exists only in the
 * editor (Eq/Neq on (Empty)); pruneTree turns it into IsNull/IsNotNull before anything is sent.
 * `disabled` parts are kept in the URL and skipped by the server.
 */
export interface FilterCondition {
  field: string;
  op: FilterOperator;
  value?: ConditionValue;
  values?: ListValue[];
  disabled?: boolean;
}

/**
 * A boolean group, sent as `filter` instead of `conditions` (never both — the
 * server returns 400 rather than merging them). A group must be written with
 * at least one part; parts switched off with `disabled` are skipped, and a
 * group whose every part is off is treated as absent.
 */
export interface FilterGroup {
  logic: FilterLogic;
  conditions?: FilterCondition[];
  groups?: FilterGroup[];
  disabled?: boolean;
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
  /** The status's category as Jira names it ("To Do", "In Progress", "Done"); null when there is none. */
  statusCategory: string | null;
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

export interface FilterValueOption {
  name: string;
  /** Issues in this project carrying the value — over the whole project, not the current filter. */
  count: number;
}

/** A smart sprint value and how many of this project's issues it matches now. */
export interface SmartValueOption {
  key: SmartSprintKey;
  label: string;
  count: number;
}

export interface FilterValues {
  field: string;
  values: FilterValueOption[];
  /** Issues in this project with no value for the field: the count behind (Empty). */
  emptyCount: number;
  /** Sprint only: the smart values, in the order to offer them. */
  smart?: SmartValueOption[];
}

/** The values a dictionary field actually takes in this project, with counts. 400 for any other field. */
export const getFilterValues = (projectId: string, field: string) =>
  apiFetch<FilterValues>(
    `/api/projects/${projectId}/filter-values?field=${encodeURIComponent(field)}`);

export interface HistoryFieldOption {
  name: string;
  /** Changelog rows this project has for the field. */
  count: number;
}

/** The changelog fields this project has history for, most-changed first (at most 200). */
export const getHistoryFields = (projectId: string) =>
  apiFetch<{ fields: HistoryFieldOption[] }>(`/api/projects/${projectId}/history-fields`);

/** `value`: the From/To values, Jira's ", "-joined lists split into elements. `author`: who made the changes. */
export type HistoryValueKind = 'value' | 'author';

export interface HistoryValueList {
  values: FilterValueOption[];
  /** Changelog sides (value) or rows (author) with nothing in them: the count behind (Empty). */
  emptyCount: number;
}

/** One history field's values or authors in this project, by name (at most 1000). An unknown field lists nothing. */
export const getHistoryValues = (projectId: string, field: string, kind: HistoryValueKind) =>
  apiFetch<HistoryValueList>(
    `/api/projects/${projectId}/history-values?field=${encodeURIComponent(field)}&kind=${kind}`);

/** What POST /issues/jql takes: the list's pruned filter (absent = none) and its sort. */
export interface JqlRequest {
  filter?: FilterGroup;
  sortBy: string;
  sortDescending: boolean;
}

/** One way the JQL differs from the filter: a part left out, an approximation, a sort JQL cannot do. */
export interface JqlWarning {
  message: string;
}

/** The filter as JQL, a link that opens it in this project's Jira, and every difference from the filter. */
export interface JqlResult {
  jql: string;
  url: string;
  warnings: JqlWarning[];
}

/** The same 404s and 400s as the issue query: the server checks the filter with the same compiler first. */
export const getIssueJql = (projectId: string, request: JqlRequest) =>
  apiFetch<JqlResult>(`/api/projects/${projectId}/issues/jql`, { method: 'POST', body: request });

export interface ExportView {
  /** The pruned tree the list was queried with; undefined = no filter. */
  filter?: FilterGroup;
  sortBy: string;
  sortDescending: boolean;
  columns: string[];
}

/**
 * The CSV of the view currently on screen. Fetched with the session rather than linked to — see
 * apiDownload for why a link cannot work. The server caps the export's size, which is what makes
 * holding it in memory acceptable. `filter` is always the tree: the server reads a JSON object as
 * a FilterGroup with its own logic, so no `logic` param is sent.
 */
export const exportIssuesCsv = (projectId: string, view: ExportView): Promise<Download> => {
  const params = new URLSearchParams();
  if (view.filter) params.set('filter', JSON.stringify(view.filter));
  params.set('sortBy', view.sortBy);
  params.set('sortDescending', String(view.sortDescending));
  params.set('cols', view.columns.join(','));
  return apiDownload(`/api/projects/${projectId}/issues/export.csv?${params.toString()}`);
};
