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
} as const;
