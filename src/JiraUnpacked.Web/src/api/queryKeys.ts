/**
 * One place for every TanStack Query key. Centralised because the classic
 * failure is a mutation invalidating a key no query actually uses, leaving
 * stale data on screen with no error to notice.
 */
export const queryKeys = {
  connections: () => ['connections'] as const,
  connection: (id: string) => ['connections', id] as const,
  connectionFields: (id: string) => ['connections', id, 'fields'] as const,
  availableProjects: (id: string) => ['connections', id, 'projects-available'] as const,
  issueTypes: (connectionId: string) => ['connections', connectionId, 'issue-types'] as const,
  connectionProjects: (connectionId: string) => ['connections', connectionId, 'projects'] as const,
  project: (id: string) => ['projects', id] as const,
  jiraStatuses: (projectId: string) => ['projects', projectId, 'jira-statuses'] as const,
  fieldMappings: (projectId: string) => ['projects', projectId, 'field-mappings'] as const,
  statusSemantics: (projectId: string) => ['projects', projectId, 'status-semantics'] as const,
  syncRuns: (projectId: string) => ['projects', projectId, 'sync-runs'] as const,
  // The request is part of the key so a different filter or page is a
  // different cache entry rather than silently reusing the previous results.
  issues: (projectId: string, request: unknown) =>
    ['projects', projectId, 'issues', JSON.stringify(request ?? {})] as const,
  issueDetail: (projectId: string, jiraIssueId: string) =>
    ['projects', projectId, 'issues', jiraIssueId] as const,
  storyCounts: (projectId: string) => ['projects', projectId, 'metrics', 'story-counts'] as const,
  timeInProgress: (projectId: string) => ['projects', projectId, 'metrics', 'time-in-progress'] as const,
  throughput: (projectId: string, bucket: string, from: string, to: string) =>
    ['projects', projectId, 'metrics', 'throughput', bucket, from, to] as const,
  statusAging: (projectId: string, page: number) =>
    ['projects', projectId, 'metrics', 'status-aging', page] as const,
  checks: (projectId: string) => ['projects', projectId, 'checks'] as const,
  checkResult: (projectId: string, checkId: string, page: number) =>
    ['projects', projectId, 'checks', checkId, page] as const,
  // Global — no project segment: the catalog is static and tenant-independent.
  filterCatalog: () => ['filter-catalog'] as const,
} as const;
