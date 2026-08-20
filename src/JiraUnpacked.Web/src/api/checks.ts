import { apiFetch } from './client';
import type { PagedResult } from './issues';

export interface GapCheckInfo { id: string; name: string; description: string }

export interface GapCheckChild {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
  status: string | null;
  class: string;
}

export interface GapCheckViolation {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
  status: string | null;
  class: string;
  issueType: string | null;
  children: GapCheckChild[];
}

/**
 * `status` is 'Ok' | 'NotConfigured'. BOTH can carry zero violations and they
 * mean opposite things: Ok+zero is good news, NotConfigured+zero means the
 * check could not run at all. Never render them the same way.
 */
export interface GapCheckResult {
  check: GapCheckInfo;
  status: string;
  violations: PagedResult<GapCheckViolation>;
}

export const listChecks = (projectId: string) =>
  apiFetch<GapCheckInfo[]>(`/api/projects/${projectId}/checks`);

export const runCheck = (projectId: string, checkId: string, page = 1, pageSize = 50) =>
  apiFetch<GapCheckResult>(
    `/api/projects/${projectId}/checks/${encodeURIComponent(checkId)}?page=${page}&pageSize=${pageSize}`);
