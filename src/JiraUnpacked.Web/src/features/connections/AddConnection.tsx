import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Card, Field } from '../../ui';
import { ApiError } from '../../api/errors';
import { createConnection } from '../../api/connections';
import { queryKeys } from '../../api/queryKeys';

// The two failure modes must read differently: a 400 means the token/email
// is wrong, a 502 means Jira itself is unreachable. `toApiError` already
// gives each a distinct `title` (502 prefers the server's own ProblemDetails
// title — e.g. a rejected stored credential — and falls back to a generic
// upstream-Jira message only when the body carries none; 400 always uses
// the server's own message), so rendering `error.title` verbatim keeps them
// distinct — collapsing to one generic string would send a user with a
// Jira outage off to re-check credentials that were never the problem.
function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.title;
  return 'Could not add the connection.';
}

export default function AddConnection() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [baseUrl, setBaseUrl] = useState('');
  const [email, setEmail] = useState('');
  const [apiToken, setApiToken] = useState('');

  const mutation = useMutation({
    mutationFn: () => createConnection(baseUrl, email, apiToken),
    onSuccess: (connection) => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.connections() });
      navigate(`/connections/${connection.id}`);
    },
  });

  function submit(e: FormEvent) {
    e.preventDefault();
    mutation.mutate();
  }

  return (
    <Card>
      <form onSubmit={submit}>
        <h1>Add connection</h1>
        {mutation.error && <p role="alert">{errorMessage(mutation.error)}</p>}
        <Field label="Jira site URL" id="baseUrl">
          <input
            id="baseUrl"
            type="url"
            placeholder="https://your-site.atlassian.net"
            value={baseUrl}
            onChange={(e) => setBaseUrl(e.target.value)}
          />
        </Field>
        <Field label="Email" id="email">
          <input
            id="email"
            type="email"
            autoComplete="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
        </Field>
        <Field
          label="API token"
          id="apiToken"
          hint="Stored encrypted. It cannot be displayed again after saving."
        >
          <input
            id="apiToken"
            type="password"
            autoComplete="off"
            value={apiToken}
            onChange={(e) => setApiToken(e.target.value)}
          />
        </Field>
        <Button type="submit" loading={mutation.isPending}>Add connection</Button>
      </form>
    </Card>
  );
}
