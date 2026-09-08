import { apiFetch } from './client';
import type { PagedResult } from './issues';

/** `status` is null for issues with no status FK — a real group, not an error. */
export interface StatusCount {
  status: string | null;
  class: string;
  count: number;
  storyPoints: number;
}

export interface StoryCountsReport {
  byStatus: StatusCount[];
  totalIssues: number;
  totalStoryPoints: number;
}

/** `averageDays` is null (NOT 0) when no issue qualifies, so the number is never a lie. */
export interface TimeInProgressReport {
  averageDays: number | null;
  completedIssuesConsidered: number;
  issuesExcluded: number;
}

export type ThroughputBucketSize = 'day' | 'week' | 'month';

export interface ThroughputBucket { periodStart: string; completed: number }

export interface ThroughputReport {
  bucket: string;
  from: string;
  to: string;
  buckets: ThroughputBucket[];
}

export interface AgingItem {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
  currentStatus: string | null;
  class: string;
  ageDays: number;
  enteredAt: string;
}

export interface AgingBand { band: string; count: number }

export interface StatusAgingReport {
  items: PagedResult<AgingItem>;
  histogram: AgingBand[];
}

export const getStoryCounts = (projectId: string) =>
  apiFetch<StoryCountsReport>(`/api/projects/${projectId}/metrics/story-counts`);

export const getTimeInProgress = (projectId: string) =>
  apiFetch<TimeInProgressReport>(`/api/projects/${projectId}/metrics/time-in-progress`);

export const getThroughput = (
  projectId: string,
  bucket?: ThroughputBucketSize,
  from?: string,
  to?: string,
) => {
  const qs = new URLSearchParams();
  if (bucket) qs.set('bucket', bucket);
  if (from) qs.set('from', from);
  if (to) qs.set('to', to);
  const suffix = qs.toString() ? `?${qs}` : '';
  return apiFetch<ThroughputReport>(`/api/projects/${projectId}/metrics/throughput${suffix}`);
};

export const getStatusAging = (projectId: string, page = 1, pageSize = 50) =>
  apiFetch<StatusAgingReport>(
    `/api/projects/${projectId}/metrics/status-aging?page=${page}&pageSize=${pageSize}`);

/**
 * Field churn — how often a changelog field changed per issue. `changes` counts
 * every entry including removals; `distinctValues` counts how many different
 * values it ever held. They differ when an issue leaves a sprint and returns to
 * the same one, which is a different problem from marching through five.
 */
export interface FieldChurnItem {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
  issueType: string | null;
  currentStatus: string | null;
  class: string;
  changes: number;
  distinctValues: number;
  firstChangedAt: string;
  lastChangedAt: string;
}

export interface FieldChurnBand { band: string; count: number }

export interface FieldChurnReport {
  field: string;
  /** Fields this project actually has history for — the dropdown is data, not a hardcoded list. */
  availableFields: string[];
  items: PagedResult<FieldChurnItem>;
  histogram: FieldChurnBand[];
}

export const getFieldChurn = (
  projectId: string,
  opts: { field?: string; minChanges?: number; statusClasses?: string[]; page?: number; pageSize?: number } = {},
) => {
  const qs = new URLSearchParams();
  if (opts.field) qs.set('field', opts.field);
  if (opts.minChanges !== undefined) qs.set('minChanges', String(opts.minChanges));
  // Repeated rather than comma-joined: the model binder takes statusClasses=A&statusClasses=B.
  for (const c of opts.statusClasses ?? []) qs.append('statusClasses', c);
  qs.set('page', String(opts.page ?? 1));
  qs.set('pageSize', String(opts.pageSize ?? 50));
  return apiFetch<FieldChurnReport>(`/api/projects/${projectId}/metrics/field-churn?${qs}`);
};
