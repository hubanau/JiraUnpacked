import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Card, ConfirmDialog } from '../../ui';
import { ApiError } from '../../api/errors';
import { deleteConnection, type Connection } from '../../api/connections';
import { queryKeys } from '../../api/queryKeys';

export function DeleteConnection({ connection, projectCount }: { connection: Connection; projectCount: number }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);

  const mutation = useMutation({
    mutationFn: () => deleteConnection(connection.id),
    onSuccess: () => {
      setConfirming(false);
      // Everything under this connection is gone: drop its cached queries rather than letting
      // them refetch into 404s, and refresh the list that showed it.
      queryClient.removeQueries({ queryKey: queryKeys.connection(connection.id) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.connections(), exact: true });
      navigate('/connections', { replace: true });
    },
  });

  const projects = projectCount === 1 ? '1 project' : `${projectCount} projects`;

  return (
    <Card className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h2 className="text-base font-medium text-[var(--ju-text)]">Delete connection</h2>
        <p className="text-sm text-[var(--ju-muted)]">
          Removes this connection, its projects and everything synced for them.
        </p>
      </div>
      <Button
        variant="danger"
        onClick={() => {
          mutation.reset();
          setConfirming(true);
        }}
      >
        Delete connection
      </Button>
      <ConfirmDialog
        open={confirming}
        title="Delete this connection?"
        confirmLabel="Yes, delete connection"
        onConfirm={() => mutation.mutate()}
        onCancel={() => setConfirming(false)}
        pending={mutation.isPending}
        error={errorText(mutation.error, 'Could not delete the connection.')}
      >
        <p>
          <strong>{connection.baseUrl}</strong> will be removed together with {projects} and all of their
          synced issues, history, queries, mappings and saved views. Nothing in Jira is changed. This
          cannot be undone.
        </p>
      </ConfirmDialog>
    </Card>
  );
}

function errorText(error: unknown, fallback: string): string | null {
  if (!error) return null;
  return error instanceof ApiError ? error.title : fallback;
}
