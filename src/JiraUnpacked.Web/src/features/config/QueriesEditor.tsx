import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Card, ErrorState, Field, Spinner } from '../../ui';
import { ApiError } from '../../api/errors';
import { addQuery, deleteQuery, listQueries } from '../../api/queries';
import { queryKeys } from '../../api/queryKeys';

/**
 * Which JQL a project's sync pulls.
 *
 * The behaviour worth being explicit about: with no query at all, a sync pulls the WHOLE
 * project. Queries narrow it. That is stated on screen in both directions — when the list is
 * empty, and when removing the last one would put it back — because "the list is empty" and
 * "nothing is synced" look identical otherwise, and guessing wrong either way is expensive on a
 * project with tens of thousands of issues.
 */
export function QueriesEditor({ projectId, projectKey }: { projectId: string; projectKey: string }) {
  const queryClient = useQueryClient();
  const [jql, setJql] = useState('');

  const queries = useQuery({
    queryKey: queryKeys.queries(projectId),
    queryFn: () => listQueries(projectId),
  });

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: queryKeys.queries(projectId) });

  const add = useMutation({
    mutationFn: (value: string) => addQuery(projectId, value),
    onSuccess: () => {
      setJql('');
      void invalidate();
    },
  });

  const remove = useMutation({
    mutationFn: (queryId: string) => deleteQuery(queryId),
    onSuccess: () => void invalidate(),
  });

  if (queries.isPending) return <Spinner aria-label="Loading queries" />;
  if (queries.error) {
    return (
      <ErrorState
        title={queries.error instanceof ApiError ? queries.error.title : 'Could not load queries.'}
      />
    );
  }

  const list = queries.data ?? [];
  const isLast = list.length === 1;

  function submit(e: FormEvent) {
    e.preventDefault();
    const trimmed = jql.trim();
    if (trimmed) add.mutate(trimmed);
  }

  return (
    <div className="flex flex-col gap-4">
      <Card className="flex flex-col gap-2">
        <h2 className="text-sm font-medium text-[var(--ju-text)]">What this project syncs</h2>
        {list.length === 0 ? (
          <p className="text-sm text-[var(--ju-text)]">
            Every issue in <span className="font-medium">{projectKey}</span>. Add a query below to
            sync only part of it — useful when the project is large.
          </p>
        ) : (
          <p className="text-sm text-[var(--ju-text)]">
            Only issues matching {list.length === 1 ? 'the query' : `any of the ${list.length} queries`}{' '}
            below. Remove {list.length === 1 ? 'it' : 'them all'} to go back to syncing every issue in{' '}
            <span className="font-medium">{projectKey}</span>.
          </p>
        )}
      </Card>

      {list.length > 0 && (
        <ul className="flex flex-col gap-2">
          {list.map((q) => (
            <li key={q.id}>
              <Card className="flex items-start justify-between gap-3">
                <code className="ju-numeric break-all text-sm text-[var(--ju-text)]">{q.jql}</code>
                <Button
                  variant="secondary"
                  loading={remove.isPending && remove.variables === q.id}
                  onClick={() => remove.mutate(q.id)}
                >
                  {isLast ? 'Remove (syncs all)' : 'Remove'}
                </Button>
              </Card>
            </li>
          ))}
        </ul>
      )}

      {remove.error && (
        <p role="alert" className="text-sm text-[var(--ju-danger)]">
          {remove.error instanceof ApiError ? remove.error.title : 'Could not remove the query.'}
        </p>
      )}

      <Card>
        <form onSubmit={submit} className="flex flex-col gap-3">
          {add.error && (
            <div role="alert" className="rounded-[var(--ju-radius)] border border-[var(--ju-danger)]/25 bg-[var(--ju-danger-soft)] px-3 py-2">
              <p className="text-sm font-medium text-[var(--ju-danger)]">
                {add.error instanceof ApiError ? add.error.title : 'Could not add the query.'}
              </p>
              {/* Jira's own parse error. It names the offending field far better than we could. */}
              {add.error instanceof ApiError && add.error.detail && (
                <p className="mt-0.5 text-xs text-[var(--ju-danger)]">{add.error.detail}</p>
              )}
            </div>
          )}

          <Field label="JQL" id="jql">
            <input
              id="jql"
              value={jql}
              onChange={(e) => setJql(e.target.value)}
              placeholder={`project = "${projectKey}" AND updated >= -90d`}
            />
          </Field>

          <div className="flex items-center gap-3">
            <Button type="submit" loading={add.isPending} disabled={jql.trim().length === 0}>
              Add query
            </Button>
            <p className="text-xs text-[var(--ju-muted)]">
              Checked against Jira before it is saved.
            </p>
          </div>
        </form>
      </Card>
    </div>
  );
}
