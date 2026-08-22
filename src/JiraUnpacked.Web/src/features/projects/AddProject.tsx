import { useEffect, useState, type FormEvent } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Card, EmptyState, ErrorState, Field, Select, Spinner } from '../../ui';
import { ApiError } from '../../api/errors';
import { listAvailableProjects } from '../../api/connections';
import { addProject, MIN_SYNC_INTERVAL_MINUTES } from '../../api/projects';
import { queryKeys } from '../../api/queryKeys';
import { SyncIntervalField } from './SyncIntervalField';

// The project key must be one Jira actually has, so callers use a picker
// (see the Select below) instead of a free-text field — that makes the
// server's "Unknown Jira project key." 400 unreachable by construction. A
// 409 means the project is already attached to this connection; every other
// status renders the server's own wording (400s carry the server's message;
// 502 carries the server's own ProblemDetails title when it sent one — e.g.
// a rejected stored credential — and toApiError's generic upstream-Jira
// message only as a fallback).
function addProjectError(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Could not add the project.';
  if (error.status === 409) return 'That project is already added.';
  return error.title;
}

export default function AddProject() {
  // Route always supplies :connectionId here (see App.tsx); the '' fallback
  // only covers the type, not a real navigation state.
  const { connectionId = '' } = useParams<{ connectionId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const [projectKey, setProjectKey] = useState('');
  const [syncInterval, setSyncInterval] = useState(MIN_SYNC_INTERVAL_MINUTES);

  const { data: available, isPending, error } = useQuery({
    queryKey: queryKeys.availableProjects(connectionId),
    queryFn: () => listAvailableProjects(connectionId),
  });

  // Default the picker to the first available project once the list loads,
  // so the Select never renders with options but no selection.
  useEffect(() => {
    if (available && available.length > 0 && projectKey === '') {
      setProjectKey(available[0].key);
    }
  }, [available, projectKey]);

  const mutation = useMutation({
    mutationFn: () => addProject(connectionId, projectKey, syncInterval),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.connectionProjects(connectionId) });
      navigate(`/projects/${created.id}`);
    },
  });

  if (isPending) return <Spinner aria-label="Loading available projects" />;
  if (error) {
    return (
      <ErrorState
        title={error instanceof ApiError ? error.title : 'Could not load available projects.'}
      />
    );
  }

  if (available.length === 0) {
    return (
      <EmptyState
        title="No projects available"
        description="Every project in this Jira site is already added, or the site has none."
      />
    );
  }

  const tooLow = syncInterval < MIN_SYNC_INTERVAL_MINUTES;

  function submit(e: FormEvent) {
    e.preventDefault();
    mutation.mutate();
  }

  return (
    <Card>
      <form onSubmit={submit} className="flex flex-col gap-4">
        <h1 className="text-lg font-semibold text-[var(--ju-text)]">Add project</h1>
        {mutation.error && <p role="alert">{addProjectError(mutation.error)}</p>}
        <Field label="Jira project" id="projectKey">
          <Select
            id="projectKey"
            value={projectKey}
            onChange={(e) => setProjectKey(e.target.value)}
          >
            {available.map((p) => (
              <option key={p.id} value={p.key}>{`${p.key} — ${p.name}`}</option>
            ))}
          </Select>
        </Field>
        <SyncIntervalField value={syncInterval} onChange={setSyncInterval} />
        <Button type="submit" loading={mutation.isPending} disabled={tooLow || projectKey === ''}>
          Add project
        </Button>
      </form>
    </Card>
  );
}
