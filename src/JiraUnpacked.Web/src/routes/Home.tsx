import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQueries, useQuery } from '@tanstack/react-query';
import { Card, EmptyState, ErrorState, Select, Spinner, TBody, TD, TH, THead, TR, Table } from '../ui';
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
        action={<Link to={`/connections/${connections.data[0].id}/projects/new`}>Add project</Link>}
      />
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-medium tracking-tight text-[var(--ju-text)]">Projects</h1>
        <AddProjectAction connections={connections.data} />
      </div>
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

/**
 * "Add project" from the projects list.
 *
 * Adding a project means adding it TO a connection — the form reads the available projects from
 * one Jira site — so this needs a connection before it can go anywhere. With a single connection
 * that choice is already made and asking would be noise; with several there is no defensible
 * default, so the choice is offered rather than guessed.
 *
 * This exists because the only route to the form used to be a connection's own detail page: the
 * projects list linked to it in its empty state and nowhere else, so once a project existed the
 * action disappeared from the page most likely to be looked at for it.
 */
function AddProjectAction({ connections }: { connections: { id: string; baseUrl: string }[] }) {
  const [connectionId, setConnectionId] = useState(connections[0]?.id ?? '');

  if (connections.length === 0) return null;

  const target = `/connections/${connections.length === 1 ? connections[0].id : connectionId}/projects/new`;

  return (
    <div className="flex items-center gap-2">
      {connections.length > 1 && (
        <Select
          aria-label="Connection to add the project to"
          value={connectionId}
          onChange={(e) => setConnectionId(e.target.value)}
        >
          {connections.map((c) => (
            <option key={c.id} value={c.id}>{hostOf(c.baseUrl)}</option>
          ))}
        </Select>
      )}
      <Link
        to={target}
        className="rounded-[var(--ju-radius)] bg-[var(--ju-primary)] px-3 py-2 text-sm font-medium text-white hover:opacity-90"
      >
        Add project
      </Link>
    </div>
  );
}

/** The Jira site, by host — the whole URL is too long for a picker and its host is the identity. */
function hostOf(baseUrl: string): string {
  try {
    return new URL(baseUrl).host;
  } catch {
    // A stored value that is not a parseable URL is still worth showing rather than blanking out.
    return baseUrl;
  }
}
