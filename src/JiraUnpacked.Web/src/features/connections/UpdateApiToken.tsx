import { useState, type FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Card, Field } from '../../ui';
import { ApiError } from '../../api/errors';
import { updateConnectionCredentials, type Connection } from '../../api/connections';
import { queryKeys } from '../../api/queryKeys';

/**
 * Rotates the connection's API token. The server validates the new token against Jira before it
 * replaces the old one, so a failure here always leaves the working token in place — which is
 * why the error is shown verbatim: 400 (Jira refused the credentials) and 502 (Jira unreachable)
 * send the user to different fixes.
 */
export function UpdateApiToken({ connection }: { connection: Connection }) {
  const queryClient = useQueryClient();
  const [email, setEmail] = useState(connection.email);
  const [apiToken, setApiToken] = useState('');
  const [saved, setSaved] = useState(false);

  const mutation = useMutation({
    mutationFn: () => updateConnectionCredentials(connection.id, email, apiToken),
    onSuccess: (updated) => {
      setApiToken('');
      setSaved(true);
      queryClient.setQueryData(queryKeys.connection(connection.id), updated);
      void queryClient.invalidateQueries({ queryKey: queryKeys.connections(), exact: true });
    },
  });

  function submit(e: FormEvent) {
    e.preventDefault();
    setSaved(false);
    mutation.mutate();
  }

  return (
    <Card>
      <form onSubmit={submit} className="flex flex-col gap-3" aria-label="Update API token">
        <h2 className="text-base font-medium text-[var(--ju-text)]">Update API token</h2>
        {mutation.error && (
          <p role="alert" className="text-sm text-[var(--ju-danger)]">
            {mutation.error instanceof ApiError ? mutation.error.title : 'Could not update the API token.'}
          </p>
        )}
        {saved && (
          <p role="status" className="text-sm text-[var(--ju-success)]">
            API token updated.
          </p>
        )}
        <Field label="Email" id="rotate-email">
          <input
            id="rotate-email"
            type="email"
            autoComplete="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
        </Field>
        <Field
          label="New API token"
          id="rotate-token"
          hint="Checked with Jira before it replaces the current one. It cannot be displayed again."
        >
          <input
            id="rotate-token"
            type="password"
            autoComplete="off"
            value={apiToken}
            onChange={(e) => setApiToken(e.target.value)}
          />
        </Field>
        <div>
          <Button type="submit" loading={mutation.isPending} disabled={apiToken.trim() === ''}>
            Update token
          </Button>
        </div>
      </form>
    </Card>
  );
}
