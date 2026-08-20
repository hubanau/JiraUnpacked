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
