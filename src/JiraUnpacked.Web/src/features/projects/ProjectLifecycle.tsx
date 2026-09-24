import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, ConfirmDialog } from '../../ui';
import { ApiError } from '../../api/errors';
import { deleteProject, setSyncPaused, type Project } from '../../api/projects';
import { queryKeys } from '../../api/queryKeys';

function errorText(error: unknown, fallback: string): string | null {
  if (!error) return null;
  return error instanceof ApiError ? error.title : fallback;
}

/**
 * Pausing stops the scheduler only: "Sync now" keeps working, which is why the copy says
 * "scheduled".
 */
export function PauseToggle({ project }: { project: Project }) {
  const queryClient = useQueryClient();
  const mutation = useMutation({
    mutationFn: (paused: boolean) => setSyncPaused(project.id, paused),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.project(project.id), exact: true });
      void queryClient.invalidateQueries({ queryKey: queryKeys.connectionProjects(project.jiraConnectionId) });
    },
  });
  const error = errorText(mutation.error, 'Could not change the schedule.');

  return (
    <div className="flex flex-col gap-1">
      <Button
        variant="secondary"
        loading={mutation.isPending}
        onClick={() => mutation.mutate(!project.syncPaused)}
      >
        {project.syncPaused ? 'Resume scheduled sync' : 'Pause scheduled sync'}
      </Button>
      {error && (
        <p role="alert" className="text-sm text-[var(--ju-danger)]">
          {error}
        </p>
      )}
    </div>
  );
}

export function DeleteProject({ project }: { project: Project }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);

  const mutation = useMutation({
    mutationFn: () => deleteProject(project.id),
    onSuccess: () => {
      setConfirming(false);
      // Every per-project key starts with ['projects', id], so this drops them all rather than
      // letting them refetch into 404s.
      queryClient.removeQueries({ queryKey: queryKeys.project(project.id) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.connectionProjects(project.jiraConnectionId) });
      navigate(`/connections/${project.jiraConnectionId}`, { replace: true });
    },
  });

  return (
    <>
      <Button
        variant="danger"
        onClick={() => {
          mutation.reset();
          setConfirming(true);
        }}
      >
        Delete project
      </Button>
      <ConfirmDialog
        open={confirming}
        title={`Delete ${project.jiraProjectKey}?`}
        confirmLabel="Yes, delete project"
        onConfirm={() => mutation.mutate()}
        onCancel={() => setConfirming(false)}
        pending={mutation.isPending}
        error={errorText(mutation.error, 'Could not delete the project.')}
      >
        <p>
          All synced issues, change history, sync runs, queries, mappings and saved views for{' '}
          <strong>{project.jiraProjectKey}</strong> will be removed. The connection stays, and nothing in
          Jira is changed. This cannot be undone.
        </p>
      </ConfirmDialog>
    </>
  );
}
