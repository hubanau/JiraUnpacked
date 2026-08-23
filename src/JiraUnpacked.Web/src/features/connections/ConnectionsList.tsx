import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Card, EmptyState, ErrorState, Spinner } from '../../ui';
import { ApiError } from '../../api/errors';
import { listConnections } from '../../api/connections';
import { queryKeys } from '../../api/queryKeys';

export default function ConnectionsList() {
  const { data, isPending, error } = useQuery({
    queryKey: queryKeys.connections(),
    queryFn: listConnections,
  });

  if (isPending) return <Spinner aria-label="Loading connections" />;
  if (error) {
    return (
      <ErrorState
        title={error instanceof ApiError ? error.title : 'Could not load connections.'}
      />
    );
  }

  if (data.length === 0) {
    return (
      <EmptyState
        title="No connections yet"
        description="A connection is a Jira site plus an API token, so Jira Unpacked can read your issues."
        action={<Link to="/connections/new">Add connection</Link>}
      />
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-medium tracking-tight text-[var(--ju-text)]">Connections</h1>
        <Link to="/connections/new">Add connection</Link>
      </div>
      <ul className="flex flex-col gap-3">
        {data.map((connection) => (
          <li key={connection.id}>
            <Link to={`/connections/${connection.id}`}>
              <Card>
                <p className="font-medium text-[var(--ju-text)]">{connection.baseUrl}</p>
                <p className="text-sm text-[var(--ju-muted)]">{connection.email}</p>
                <p className="text-xs text-[var(--ju-muted)]">
                  Added {new Date(connection.createdAtUtc).toLocaleDateString()}
                </p>
              </Card>
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}
