import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Card, EmptyState, ErrorState, Spinner } from '../../ui';
import { ApiError } from '../../api/errors';
import { getConnection } from '../../api/connections';
import { listConnectionProjects } from '../../api/projects';
import { queryKeys } from '../../api/queryKeys';
import { IssueTypeClassEditor } from './IssueTypeClassEditor';

export default function ConnectionDetail() {
  // Route always supplies :id here (see App.tsx); the '' fallback only
  // covers the type, not a real navigation state.
  const { id = '' } = useParams<{ id: string }>();

  const connectionQuery = useQuery({
    queryKey: queryKeys.connection(id),
    queryFn: () => getConnection(id),
  });

  const projectsQuery = useQuery({
    queryKey: queryKeys.connectionProjects(id),
    queryFn: () => listConnectionProjects(id),
  });

  if (connectionQuery.isPending) return <Spinner aria-label="Loading connection" />;
  if (connectionQuery.error) {
    return (
      <ErrorState
        title={
          connectionQuery.error instanceof ApiError
            ? connectionQuery.error.title
            : 'Could not load connection.'
        }
      />
    );
  }

  const connection = connectionQuery.data;

  return (
    <div className="flex flex-col gap-6">
      <header>
        <h1 className="text-xl font-medium tracking-tight text-[var(--ju-text)]">{connection.baseUrl}</h1>
        <p className="text-sm text-[var(--ju-muted)]">{connection.email}</p>
      </header>

      <section>
        <div className="flex items-center justify-between">
          <h2>Projects</h2>
          <Link to={`/connections/${id}/projects/new`}>Add project</Link>
        </div>
        {projectsQuery.isPending && <Spinner aria-label="Loading projects" />}
        {projectsQuery.error && (
          <ErrorState
            title={
              projectsQuery.error instanceof ApiError
                ? projectsQuery.error.title
                : 'Could not load projects.'
            }
          />
        )}
        {projectsQuery.data && projectsQuery.data.length === 0 && (
          <EmptyState
            title="No projects yet"
            description="Add a Jira project to this connection to start syncing its issues."
          />
        )}
        {projectsQuery.data && projectsQuery.data.length > 0 && (
          <ul className="flex flex-col gap-2">
            {projectsQuery.data.map((project) => (
              <li key={project.id}>
                <Link to={`/projects/${project.id}`}>
                  <Card>
                    <p className="font-medium text-[var(--ju-text)]">{project.jiraProjectKey}</p>
                    <p className="text-xs text-[var(--ju-muted)]">
                      Syncs every {project.syncIntervalMinutes} minutes
                    </p>
                  </Card>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>

      <IssueTypeClassEditor connectionId={id} />
    </div>
  );
}
