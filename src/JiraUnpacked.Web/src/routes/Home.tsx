import { Link } from 'react-router-dom';
import { useQueries, useQuery } from '@tanstack/react-query';
import { Card, EmptyState, ErrorState, Spinner, TBody, TD, TH, THead, TR, Table } from '../ui';
import { ApiError } from '../api/errors';
import { listConnections } from '../api/connections';
import { listConnectionProjects } from '../api/projects';
import { queryKeys } from '../api/queryKeys';

// There is no GET /api/projects — the only project reads are scoped to a
// connection or a single project — so the all-projects landing fans out:
// list connections, then one connectionProjects query per connection, then
// flatten. Fine for a self-hosted tenant, which realistically has one or
// two connections.
export default function Home() {
  const connections = useQuery({ queryKey: queryKeys.connections(), queryFn: listConnections });

  const projectQueries = useQueries({
    queries: (connections.data ?? []).map((c) => ({
      queryKey: queryKeys.connectionProjects(c.id),
      queryFn: () => listConnectionProjects(c.id),
    })),
  });

  if (connections.isPending) return <Spinner aria-label="Loading connections" />;
  if (connections.error) {
    return (
      <ErrorState
        title={connections.error instanceof ApiError ? connections.error.title : 'Could not load connections.'}
      />
    );
  }

  if (connections.data.length === 0) {
    return (
      <EmptyState
        title="No connections yet"
        description="A connection is a Jira site plus an API token, so Jira Unpacked can read your issues."
        action={<Link to="/connections/new">Add connection</Link>}
      />
    );
  }

  const projectsPending = projectQueries.some((q) => q.isPending);
  if (projectsPending) return <Spinner aria-label="Loading projects" />;

  const projectsError = projectQueries.find((q) => q.error);
  if (projectsError) {
    return (
      <ErrorState
        title={
          projectsError.error instanceof ApiError ? projectsError.error.title : 'Could not load projects.'
        }
      />
    );
  }

  const projects = projectQueries.flatMap((q) => q.data ?? []);

  if (projects.length === 0) {
    return (
      <EmptyState
        title="No projects yet"
        description="Add a Jira project to one of your connections to start syncing its issues."
        action={<Link to={`/connections/${connections.data[0].id}`}>Go to connection</Link>}
      />
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-medium tracking-tight text-[var(--ju-text)]">Projects</h1>
      <Card>
        <Table>
          <THead>
            <TR>
              <TH>Project</TH>
              <TH>Sync interval</TH>
              <TH>Issues</TH>
            </TR>
          </THead>
          <TBody>
            {projects.map((project) => (
              <TR key={project.id}>
                <TD>
                  <Link to={`/projects/${project.id}`} className="font-medium text-[var(--ju-primary)] hover:underline">
                    {project.jiraProjectKey}
                  </Link>
                </TD>
                <TD>{project.syncIntervalMinutes} minutes</TD>
                <TD>
                  <Link to={`/projects/${project.id}/issues`} className="text-[var(--ju-primary)] hover:underline">
                    Issues
                  </Link>
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
      </Card>
    </div>
  );
}
