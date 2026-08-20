import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Card, ErrorState, Spinner } from '../../ui';
import { ApiError } from '../../api/errors';
import { getFieldMappings, getIssueTypes, getStatusSemantics, listJiraStatuses } from '../../api/mappings';
import { getProject, MIN_SYNC_INTERVAL_MINUTES, updateSyncInterval } from '../../api/projects';
import { queryKeys } from '../../api/queryKeys';
import { FieldMappingsEditor } from '../config/FieldMappingsEditor';
import { ReadinessChecklist } from '../config/ReadinessChecklist';
import { deriveReadiness } from '../config/readiness';
import { StatusSemanticsEditor } from '../config/StatusSemanticsEditor';
import { SyncControls } from '../sync/SyncControls';
import { SyncRunTable } from '../sync/SyncRunTable';
import { SyncIntervalField } from './SyncIntervalField';

type TabKey = 'overview' | 'fields' | 'statuses';

// CF4 fills Fields and Statuses with the mapping editors; CF5 fills
// Overview with sync controls and run history. The tab strip itself is
// local state (no router nesting needed).
const TABS: ReadonlyArray<{ key: TabKey; label: string }> = [
  { key: 'overview', label: 'Overview' },
  { key: 'fields', label: 'Fields' },
  { key: 'statuses', label: 'Statuses' },
];

export default function ProjectDetail() {
  // Route always supplies :id here (see App.tsx); the '' fallback only
  // covers the type, not a real navigation state.
  const { id = '' } = useParams<{ id: string }>();
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = useState<TabKey>('overview');
  // null means "no local edit yet" — the field mirrors the loaded value
  // until the user types, and resets to null once a save succeeds.
  const [pendingInterval, setPendingInterval] = useState<number | null>(null);

  const { data: project, isPending, error } = useQuery({
    queryKey: queryKeys.project(id),
    queryFn: () => getProject(id),
  });

  // Readiness needs these three lists too, but the checklist itself must
  // never gate rendering of the project — Rules of Hooks means every one of
  // these stays above the pending/error return below, paired with `?? []`
  // and `.data` optional chaining since each is undefined while in flight.
  const fieldMappingsQuery = useQuery({
    queryKey: queryKeys.fieldMappings(id),
    queryFn: () => getFieldMappings(id),
  });
  const jiraStatusesQuery = useQuery({
    queryKey: queryKeys.jiraStatuses(id),
    queryFn: () => listJiraStatuses(id),
  });
  const statusSemanticsQuery = useQuery({
    queryKey: queryKeys.statusSemantics(id),
    queryFn: () => getStatusSemantics(id),
  });
  const issueTypesQuery = useQuery({
    queryKey: queryKeys.issueTypes(project?.jiraConnectionId ?? ''),
    queryFn: () => getIssueTypes(project?.jiraConnectionId ?? ''),
    enabled: Boolean(project?.jiraConnectionId),
  });

  const save = useMutation({
    mutationFn: (syncIntervalMinutes: number) => updateSyncInterval(id, syncIntervalMinutes),
    onSuccess: () => {
      setPendingInterval(null);
      void queryClient.invalidateQueries({ queryKey: queryKeys.project(id) });
    },
  });

  if (isPending) return <Spinner aria-label="Loading project" />;
  if (error) {
    return <ErrorState title={error instanceof ApiError ? error.title : 'Could not load project.'} />;
  }

  const interval = pendingInterval ?? project.syncIntervalMinutes;
  const tooLow = interval < MIN_SYNC_INTERVAL_MINUTES;

  return (
    <div className="flex flex-col gap-6">
      <header className="flex flex-col gap-3">
        <Link
          to={`/connections/${project.jiraConnectionId}`}
          className="text-sm text-[var(--ju-muted)] hover:underline"
        >
          &larr; Back to connection
        </Link>
        <div className="flex items-start justify-between gap-3">
          <div>
            <h1 className="text-lg font-semibold text-[var(--ju-text)]">{project.jiraProjectKey}</h1>
            <p className="text-sm text-[var(--ju-muted)]">
              Syncs every {project.syncIntervalMinutes} minutes
            </p>
          </div>
          <Link
            to={`/projects/${id}/issues`}
            className="text-sm font-medium text-[var(--ju-primary)] hover:underline"
          >
            Browse issues
          </Link>
        </div>

        <Card className="flex flex-col gap-3 sm:flex-row sm:items-end">
          <div className="flex-1">
            {save.error && (
              <p role="alert" className="mb-2 text-sm text-[var(--ju-danger)]">
                {save.error instanceof ApiError ? save.error.title : 'Could not update the sync interval.'}
              </p>
            )}
            <SyncIntervalField value={interval} onChange={setPendingInterval} />
          </div>
          <Button loading={save.isPending} disabled={tooLow} onClick={() => save.mutate(interval)}>
            Save interval
          </Button>
        </Card>
      </header>

      {fieldMappingsQuery.data && jiraStatusesQuery.data && statusSemanticsQuery.data && issueTypesQuery.data && (
        <ReadinessChecklist
          readiness={deriveReadiness({
            fieldMappings: fieldMappingsQuery.data,
            jiraStatuses: jiraStatusesQuery.data,
            statusSemantics: statusSemanticsQuery.data,
            issueTypes: issueTypesQuery.data,
          })}
          connectionId={project.jiraConnectionId}
          onSelectTab={setActiveTab}
        />
      )}

      <div>
        <div role="tablist" aria-label="Project sections" className="flex gap-4 border-b border-[var(--ju-border)]">
          {TABS.map((tab) => (
            <button
              key={tab.key}
              type="button"
              role="tab"
              id={`project-tab-${tab.key}`}
              aria-selected={activeTab === tab.key}
              aria-controls={`project-tabpanel-${tab.key}`}
              onClick={() => setActiveTab(tab.key)}
              className={`border-b-2 px-1 pb-2 text-sm font-medium ${
                activeTab === tab.key
                  ? 'border-[var(--ju-primary)] text-[var(--ju-text)]'
                  : 'border-transparent text-[var(--ju-muted)] hover:text-[var(--ju-text)]'
              }`}
            >
              {tab.label}
            </button>
          ))}
        </div>

        <div
          role="tabpanel"
          id={`project-tabpanel-${activeTab}`}
          aria-labelledby={`project-tab-${activeTab}`}
          className="pt-4"
        >
          {activeTab === 'overview' && (
            <div className="flex flex-col gap-4">
              <SyncControls projectId={id} />
              <SyncRunTable projectId={id} />
            </div>
          )}
          {activeTab === 'fields' && (
            <FieldMappingsEditor projectId={id} connectionId={project.jiraConnectionId} />
          )}
          {activeTab === 'statuses' && <StatusSemanticsEditor projectId={id} />}
        </div>
      </div>
    </div>
  );
}
