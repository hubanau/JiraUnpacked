# Web UI Spec 2 (Connect & Configure) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Take the signed-in-but-empty SPA from Spec 1 to a working setup flow — add a Jira connection, add a project, map fields and statuses, run a sync, and watch it finish.

**Architecture:** Four thin React-free API modules over Spec 1's `apiFetch`, one central `queryKeys` module so mutations invalidate keys queries actually use, and feature folders (`connections`, `projects`, `config`, `sync`) holding screens plus their hooks. The only genuinely designed piece is a **pure `readiness` function** that derives per-project setup completeness from data already fetched — the API exposes no such endpoint, and the endpoints form a dependency chain the UI must make visible.

**Tech Stack:** React 19.2 · TypeScript strict · Vite 6 · Tailwind v4 · React Router 7 · TanStack Query 5 · Vitest + React Testing Library + MSW. No new dependencies.

## Global Constraints

- **NO backend change at all.** Every endpoint already exists and is tested. If a screen appears to need a new endpoint, **re-scope the screen and report it** — never widen the API.
- **NO new runtime dependency.** Exactly `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`. No combobox, table, form, or date library.
- **No new UI primitive** unless a screen genuinely cannot be built from the existing eleven in `src/ui/`.
- **Layering:** `src/ui/` imports neither `src/api/` nor `src/auth/`; `src/api/client.ts` imports neither React nor `../auth` (the `AuthBridge` seam).
- **Do not modify** `session.ts`'s `refreshOnce()` single-flight logic or any `hits`/`refreshHits` assertion.
- Access token never persisted; **no token, password, or request body logged** (no `console.*` of them).
- `QueryClient` keeps `retry: false` and `refetchOnWindowFocus: false`.
- **`PUT /field-mappings` and `PUT /status-semantics` are replace-all** — those editors must submit the **complete** set, never a partial patch, or the server deletes the rows they omitted. **`PUT /issue-types` is additive** (unlisted types keep their class) and validates the whole batch before mutating, so one unknown id rejects it entirely; sending the full set there is harmless but not required. **No optimistic updates** on configuration writes.
- Tests target logic, **no snapshot tests**. No test may pass if the behaviour it names is broken.
- The .NET suite (**278**) stays green and untouched; the FR-B2 gate is not touched.
- Enums serialise **by name** — model them as TypeScript string unions.
- Every task ends with a commit. Never push, open a PR, or merge.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/api/queryKeys.ts` | The single source of TanStack Query keys |
| `src/api/connections.ts` | Connection CRUD + Jira discovery (fields, available projects) |
| `src/api/projects.ts` | Project add/list/get/update-interval |
| `src/api/mappings.ts` | Jira statuses, field mappings, status semantics, issue-type classes |
| `src/api/sync.ts` | Trigger sync/rederive, list runs |
| `src/features/connections/*` | Connections list, add form, detail, issue-type editor |
| `src/features/projects/*` | Add project, project detail shell, sync-interval field |
| `src/features/config/readiness.ts` | **Pure** readiness derivation |
| `src/features/config/*Editor.tsx` | Field-mappings and status-semantics editors |
| `src/features/sync/*` | Sync controls, run table, polling hook |
| `src/routes/Home.tsx` | Becomes the projects landing (modified) |
| `src/shell/SideNav.tsx` | Gains Connections + Projects (modified) |
| `src/App.tsx` | New routes (modified) |

**A constraint discovered while planning, which shapes the navigation:** there is **no `GET /api/projects`** — the only project reads are `GET /api/connections/{connectionId}/projects` and `GET /api/projects/{projectId}`. So an "all projects" view must fetch connections and then one query per connection. That fan-out is acceptable here (a self-hosted tenant realistically has one or two connections) and is the correct call given "no backend change", but it must be written as a dependent query rather than a single fetch. Task CF6 does this explicitly.

---

## Task 1 (CF1): API layer and query keys

**Files:**
- Create: `src/JiraUnpacked.Web/src/api/queryKeys.ts`, `connections.ts`, `projects.ts`, `mappings.ts`, `sync.ts`
- Test: `src/JiraUnpacked.Web/src/api/spec2-endpoints.test.ts`

**Interfaces:**
- Consumes: `apiFetch<T>(path, options?)` and `ApiError` from Spec 1's `./client` / `./errors`.
- Produces: every type and wrapper the later tasks import (listed in the code below).

- [ ] **Step 1: Write `src/api/queryKeys.ts`**

```ts
/**
 * One place for every TanStack Query key. Centralised because the classic
 * failure is a mutation invalidating a key no query actually uses, leaving
 * stale data on screen with no error to notice.
 */
export const queryKeys = {
  connections: () => ['connections'] as const,
  connection: (id: string) => ['connections', id] as const,
  connectionFields: (id: string) => ['connections', id, 'fields'] as const,
  availableProjects: (id: string) => ['connections', id, 'projects-available'] as const,
  issueTypes: (connectionId: string) => ['connections', connectionId, 'issue-types'] as const,
  connectionProjects: (connectionId: string) => ['connections', connectionId, 'projects'] as const,
  project: (id: string) => ['projects', id] as const,
  jiraStatuses: (projectId: string) => ['projects', projectId, 'jira-statuses'] as const,
  fieldMappings: (projectId: string) => ['projects', projectId, 'field-mappings'] as const,
  statusSemantics: (projectId: string) => ['projects', projectId, 'status-semantics'] as const,
  syncRuns: (projectId: string) => ['projects', projectId, 'sync-runs'] as const,
} as const;
```

- [ ] **Step 2: Write `src/api/connections.ts`**

Match `api/auth.ts`'s style: thin arrow-function wrappers, no React import.

```ts
import { apiFetch } from './client';

export interface Connection {
  id: string;
  baseUrl: string;
  email: string;
  createdAtUtc: string;
}

export interface JiraField {
  id: string;
  name: string;
  custom: boolean;
}

export interface JiraProjectSummary {
  id: string;
  key: string;
  name: string;
}

export const listConnections = () => apiFetch<Connection[]>('/api/connections');

export const getConnection = (id: string) => apiFetch<Connection>(`/api/connections/${id}`);

export const createConnection = (baseUrl: string, email: string, apiToken: string) =>
  apiFetch<Connection>('/api/connections', {
    method: 'POST', body: { baseUrl, email, apiToken },
  });

export const listConnectionFields = (id: string) =>
  apiFetch<JiraField[]>(`/api/connections/${id}/fields`);

export const listAvailableProjects = (id: string) =>
  apiFetch<JiraProjectSummary[]>(`/api/connections/${id}/projects-available`);
```

- [ ] **Step 3: Write `src/api/projects.ts`**

```ts
import { apiFetch } from './client';

export interface Project {
  id: string;
  jiraConnectionId: string;
  jiraProjectKey: string;
  jiraProjectId: string;
  syncIntervalMinutes: number;
  createdAtUtc: string;
}

/** The server rejects anything lower; mirrored here only as a client-side hint. */
export const MIN_SYNC_INTERVAL_MINUTES = 60;

export const listConnectionProjects = (connectionId: string) =>
  apiFetch<Project[]>(`/api/connections/${connectionId}/projects`);

export const getProject = (projectId: string) =>
  apiFetch<Project>(`/api/projects/${projectId}`);

export const addProject = (connectionId: string, projectKey: string, syncIntervalMinutes: number) =>
  apiFetch<Project>(`/api/connections/${connectionId}/projects`, {
    method: 'POST', body: { projectKey, syncIntervalMinutes },
  });

export const updateSyncInterval = (projectId: string, syncIntervalMinutes: number) =>
  apiFetch<void>(`/api/projects/${projectId}`, {
    method: 'PATCH', body: { syncIntervalMinutes },
  });
```

- [ ] **Step 4: Write `src/api/mappings.ts`**

```ts
import { apiFetch } from './client';

export type FieldRole = 'Team' | 'Assignee' | 'Sprint' | 'StoryPoints' | 'Status';
export type StatusClass = 'Active' | 'Done' | 'Other';
export type IssueTypeClass = 'Other' | 'Epic';

/** The five roles, in the order the editor renders them. */
export const FIELD_ROLES: readonly FieldRole[] = ['Status', 'Assignee', 'Team', 'Sprint', 'StoryPoints'];
export const STATUS_CLASSES: readonly StatusClass[] = ['Active', 'Done', 'Other'];

export interface JiraStatus { id: string; name: string }
export interface FieldMapping { role: FieldRole; jiraFieldId: string }
export interface StatusSemantic { jiraStatusId: string; statusName: string; class: StatusClass }
export interface IssueTypeClassInfo { jiraIssueTypeId: string; name: string; class: IssueTypeClass }

export const listJiraStatuses = (projectId: string) =>
  apiFetch<JiraStatus[]>(`/api/projects/${projectId}/jira-statuses`);

export const getFieldMappings = (projectId: string) =>
  apiFetch<FieldMapping[]>(`/api/projects/${projectId}/field-mappings`);

/** Replace-all: `mappings` must be the COMPLETE set, not a patch. */
export const putFieldMappings = (projectId: string, mappings: FieldMapping[]) =>
  apiFetch<void>(`/api/projects/${projectId}/field-mappings`, {
    method: 'PUT', body: { mappings },
  });

export const getStatusSemantics = (projectId: string) =>
  apiFetch<StatusSemantic[]>(`/api/projects/${projectId}/status-semantics`);

/** Replace-all: `statuses` must be the COMPLETE set, not a patch. */
export const putStatusSemantics = (projectId: string, statuses: StatusSemantic[]) =>
  apiFetch<void>(`/api/projects/${projectId}/status-semantics`, {
    method: 'PUT', body: { statuses },
  });

export const getIssueTypes = (connectionId: string) =>
  apiFetch<IssueTypeClassInfo[]>(`/api/connections/${connectionId}/issue-types`);

/**
 * Additive, NOT replace-all: only the listed types change and unlisted ones
 * keep their class. The whole batch is validated before anything mutates, so
 * one unknown id rejects the request with 400 {error}. Sending the complete
 * set is harmless (unchanged rows are rewritten their current value) but is
 * not required — no fetch-merge dance is needed here.
 */
export const putIssueTypes = (
  connectionId: string,
  issueTypes: { jiraIssueTypeId: string; class: IssueTypeClass }[],
) =>
  apiFetch<void>(`/api/connections/${connectionId}/issue-types`, {
    method: 'PUT', body: { issueTypes },
  });
```

- [ ] **Step 5: Write `src/api/sync.ts`**

```ts
import { apiFetch } from './client';

export type SyncRunStatus = 'Running' | 'Succeeded' | 'Failed' | 'PartiallyFailed';

export interface SyncRun {
  id: string;
  jiraProjectId: string;
  status: SyncRunStatus;
  startedAt: string;
  finishedAt: string | null;
  issuesProcessed: number;
  error: string | null;
  nextRunAt: string | null;
  kind: string;
}

export const listSyncRuns = (projectId: string) =>
  apiFetch<SyncRun[]>(`/api/projects/${projectId}/sync-runs`);

export const runSync = (projectId: string) =>
  apiFetch<SyncRun>(`/api/projects/${projectId}/sync`, { method: 'POST' });

export const runRederive = (projectId: string) =>
  apiFetch<SyncRun>(`/api/projects/${projectId}/rederive`, { method: 'POST' });
```

- [ ] **Step 6: Write the failing test**

`src/api/spec2-endpoints.test.ts`:
```ts
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { ApiError } from './errors';
import { createConnection, listConnections } from './connections';
import { addProject, updateSyncInterval } from './projects';
import { putFieldMappings, putIssueTypes, putStatusSemantics } from './mappings';
import { listSyncRuns, runSync } from './sync';
import { queryKeys } from './queryKeys';

describe('connections api', () => {
  it('lists connections', async () => {
    server.use(http.get('/api/connections', () =>
      HttpResponse.json([{ id: 'c1', baseUrl: 'https://x.atlassian.net', email: 'a@b.c', createdAtUtc: '2026-01-01T00:00:00Z' }])));

    await expect(listConnections()).resolves.toHaveLength(1);
  });

  it('surfaces a 400 from create as an ApiError with the server title', async () => {
    server.use(http.post('/api/connections', () =>
      HttpResponse.json({ title: 'Jira credentials could not be validated.' }, { status: 400 })));

    await expect(createConnection('https://x', 'a@b.c', 'tok'))
      .rejects.toMatchObject({ status: 400, title: 'Jira credentials could not be validated.' });
  });

  it('surfaces a 502 from create as the upstream-Jira message', async () => {
    server.use(http.post('/api/connections', () => new HttpResponse(null, { status: 502 })));

    const err = await createConnection('https://x', 'a@b.c', 'tok').catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(502);
  });
});

describe('projects api', () => {
  it('posts the project key and interval', async () => {
    let body: unknown = null;
    server.use(http.post('/api/connections/c1/projects', async ({ request }) => {
      body = await request.json();
      return HttpResponse.json({
        id: 'p1', jiraConnectionId: 'c1', jiraProjectKey: 'ACME', jiraProjectId: '10001',
        syncIntervalMinutes: 60, createdAtUtc: '2026-01-01T00:00:00Z',
      }, { status: 201 });
    }));

    await addProject('c1', 'ACME', 60);

    expect(body).toEqual({ projectKey: 'ACME', syncIntervalMinutes: 60 });
  });

  it('propagates a 409 duplicate', async () => {
    server.use(http.post('/api/connections/c1/projects', () => new HttpResponse(null, { status: 409 })));
    await expect(addProject('c1', 'ACME', 60)).rejects.toMatchObject({ status: 409 });
  });

  it('PATCHes the interval and tolerates a 204 with no body', async () => {
    server.use(http.patch('/api/projects/p1', () => new HttpResponse(null, { status: 204 })));
    await expect(updateSyncInterval('p1', 120)).resolves.toBeUndefined();
  });
});

describe('mappings api sends replace-all bodies', () => {
  it('wraps field mappings under `mappings`', async () => {
    let body: unknown = null;
    server.use(http.put('/api/projects/p1/field-mappings', async ({ request }) => {
      body = await request.json();
      return new HttpResponse(null, { status: 204 });
    }));

    await putFieldMappings('p1', [{ role: 'Status', jiraFieldId: 'status' }]);

    expect(body).toEqual({ mappings: [{ role: 'Status', jiraFieldId: 'status' }] });
  });

  it('wraps status semantics under `statuses`', async () => {
    let body: unknown = null;
    server.use(http.put('/api/projects/p1/status-semantics', async ({ request }) => {
      body = await request.json();
      return new HttpResponse(null, { status: 204 });
    }));

    await putStatusSemantics('p1', [{ jiraStatusId: '1', statusName: 'To Do', class: 'Active' }]);

    expect(body).toEqual({ statuses: [{ jiraStatusId: '1', statusName: 'To Do', class: 'Active' }] });
  });

  it('wraps issue types under `issueTypes` and surfaces the {error} 400 shape', async () => {
    server.use(http.put('/api/connections/c1/issue-types', () =>
      HttpResponse.json({ error: 'Unknown jiraIssueTypeId: 99' }, { status: 400 })));

    await expect(putIssueTypes('c1', [{ jiraIssueTypeId: '99', class: 'Epic' }]))
      .rejects.toMatchObject({ status: 400, title: 'Unknown jiraIssueTypeId: 99' });
  });
});

describe('sync api', () => {
  it('lists runs', async () => {
    server.use(http.get('/api/projects/p1/sync-runs', () => HttpResponse.json([])));
    await expect(listSyncRuns('p1')).resolves.toEqual([]);
  });

  it('propagates a 409 already-running from a trigger', async () => {
    server.use(http.post('/api/projects/p1/sync', () => new HttpResponse(null, { status: 409 })));
    await expect(runSync('p1')).rejects.toMatchObject({ status: 409 });
  });
});

describe('queryKeys', () => {
  it('nests project-scoped keys under the project so one invalidation clears them', () => {
    expect(queryKeys.syncRuns('p1')).toEqual(['projects', 'p1', 'sync-runs']);
    expect(queryKeys.fieldMappings('p1')).toEqual(['projects', 'p1', 'field-mappings']);
    expect(queryKeys.connectionProjects('c1')).toEqual(['connections', 'c1', 'projects']);
  });
});
```

- [ ] **Step 7: Run to verify it fails, then passes**

Run: `npm run test -- spec2-endpoints`
Expected: FAIL first (unresolved modules), then PASS (12 tests) once Steps 1–5 are in place.

- [ ] **Step 8: Full suite and commit**

Run: `npm run test && npm run typecheck && npm run lint` — all green (47 prior + 12 new = 59).

```bash
git add src/JiraUnpacked.Web/src/api
git commit -m "feat(web): typed API wrappers and query keys for connections, projects, mappings, sync"
```

---

## Task 2 (CF2): Connections

**Files:**
- Create: `src/features/connections/ConnectionsList.tsx`, `AddConnection.tsx`, `ConnectionDetail.tsx`, `IssueTypeClassEditor.tsx`
- Modify: `src/App.tsx` (add `/connections` and `/connections/:id` inside the guarded shell)
- Test: `src/features/connections/AddConnection.test.tsx`, `IssueTypeClassEditor.test.tsx`

**Interfaces:**
- Consumes (CF1): `listConnections`, `getConnection`, `createConnection`, `getIssueTypes`, `putIssueTypes`, `IssueTypeClassInfo`, `IssueTypeClass`, `queryKeys`.
- Produces: routes `/connections` and `/connections/:id`; `ConnectionDetail` renders the connection's projects list (CF3 adds the **Add project** action into it).

- [ ] **Step 1: Implement `ConnectionsList.tsx`**

`useQuery({ queryKey: queryKeys.connections(), queryFn: listConnections })`. While loading render `<Spinner />`; on error render `<ErrorState title={...} />` (remember `ErrorState` takes **plain strings**, so map with `e instanceof ApiError ? e.title : 'Could not load connections.'`).

Empty → `<EmptyState title="No connections yet" description="A connection is a Jira site plus an API token, so Jira Unpacked can read your issues." action={<Link to="/connections/new">Add connection</Link>} />`.

Otherwise a `Card` per connection showing `baseUrl`, `email`, and the created date, each linking to `/connections/:id`. A header **Add connection** button routes to `/connections/new`.

- [ ] **Step 2: Implement `AddConnection.tsx`**

Three `Field`s — `baseUrl` (`type="url"`, placeholder `https://your-site.atlassian.net`), `email`, and `apiToken` (**`type="password"`**). Below the token field, hint text: *"Stored encrypted. It cannot be displayed again after saving."*

Submit via `useMutation({ mutationFn: () => createConnection(baseUrl, email, apiToken) })`. The call validates against Jira live and is slow, so the submit `Button` uses `loading={mutation.isPending}`. On success, `queryClient.invalidateQueries({ queryKey: queryKeys.connections() })` and navigate to the new connection's detail page.

**The two failure modes must read differently** — one means the token is wrong, the other that Jira is down:

```tsx
function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.title;   // 400 and 502 already carry distinct titles
  return 'Could not add the connection.';
}
```
Render it in an alert region. (`toApiError` maps 502 to the upstream-Jira message by status alone, and the 400 carries the server's "Jira credentials could not be validated.", so simply rendering `error.title` yields two distinct messages — do not collapse them into one generic string.)

- [ ] **Step 3: Implement `ConnectionDetail.tsx`**

Reads `:id` from `useParams`. Header with the connection's `baseUrl` and `email`. Two sections: **Projects** (CF3 fills this in; for now render the list from `listConnectionProjects` with an `EmptyState` when empty) and **Issue types** (`<IssueTypeClassEditor connectionId={id} />`).

- [ ] **Step 4: Implement `IssueTypeClassEditor.tsx` (replace-all)**

```tsx
export function IssueTypeClassEditor({ connectionId }: { connectionId: string }) {
  const queryClient = useQueryClient();
  const { data, isPending, error } = useQuery({
    queryKey: queryKeys.issueTypes(connectionId),
    queryFn: () => getIssueTypes(connectionId),
  });

  // Local edits keyed by issue-type id; the submit merges them back over the
  // full fetched list so untouched rows are still sent (the PUT is replace-all).
  const [edits, setEdits] = useState<Record<string, IssueTypeClass>>({});

  const save = useMutation({
    mutationFn: () =>
      putIssueTypes(
        connectionId,
        (data ?? []).map((it) => ({
          jiraIssueTypeId: it.jiraIssueTypeId,
          class: edits[it.jiraIssueTypeId] ?? it.class,
        })),
      ),
    onSuccess: () => {
      setEdits({});
      void queryClient.invalidateQueries({ queryKey: queryKeys.issueTypes(connectionId) });
    },
  });

  if (isPending) return <Spinner aria-label="Loading issue types" />;
  if (error) return <ErrorState title={error instanceof ApiError ? error.title : 'Could not load issue types.'} />;

  return (
    <section>
      <h2>Issue types</h2>
      <p>Classify which issue type represents an epic. Epic gap-checks cannot run without it.</p>
      {save.error && (
        <p role="alert">{save.error instanceof ApiError ? save.error.title : 'Could not save issue types.'}</p>
      )}
      <Table>
        <THead><TR><TH>Issue type</TH><TH>Class</TH></TR></THead>
        <TBody>
          {data.map((it) => (
            <TR key={it.jiraIssueTypeId}>
              <TD>{it.name}</TD>
              <TD>
                <Field label={`Class for ${it.name}`} id={`itc-${it.jiraIssueTypeId}`}>
                  <Select
                    id={`itc-${it.jiraIssueTypeId}`}
                    value={edits[it.jiraIssueTypeId] ?? it.class}
                    onChange={(e) =>
                      setEdits((p) => ({ ...p, [it.jiraIssueTypeId]: e.target.value as IssueTypeClass }))}
                  >
                    <option value="Other">Other</option>
                    <option value="Epic">Epic</option>
                  </Select>
                </Field>
              </TD>
            </TR>
          ))}
        </TBody>
      </Table>
      <Button loading={save.isPending} onClick={() => save.mutate()}>Save issue types</Button>
    </section>
  );
}
```

- [ ] **Step 5: Write the failing tests**

`AddConnection.test.tsx` — the two failure modes must be **distinguishable**:
```tsx
it('shows the credentials message on 400', async () => {
  server.use(http.post('/api/connections', () =>
    HttpResponse.json({ title: 'Jira credentials could not be validated.' }, { status: 400 })));
  // fill the three fields, submit
  expect(await screen.findByRole('alert')).toHaveTextContent(/credentials could not be validated/i);
});

it('shows the upstream-Jira message on 502, not the credentials message', async () => {
  server.use(http.post('/api/connections', () => new HttpResponse(null, { status: 502 })));
  // fill the three fields, submit
  const alert = await screen.findByRole('alert');
  expect(alert).toHaveTextContent(/jira did not respond/i);
  expect(alert).not.toHaveTextContent(/credentials/i);
});

it('renders the API token as a password field', () => {
  expect(screen.getByLabelText(/api token/i)).toHaveAttribute('type', 'password');
});
```

`IssueTypeClassEditor.test.tsx` — the replace-all guard:
```tsx
it('sends every issue type on save, not just the edited one', async () => {
  server.use(
    http.get('/api/connections/c1/issue-types', () => HttpResponse.json([
      { jiraIssueTypeId: '1', name: 'Story', class: 'Other' },
      { jiraIssueTypeId: '2', name: 'Epic',  class: 'Other' },
      { jiraIssueTypeId: '3', name: 'Bug',   class: 'Other' },
    ])),
    http.put('/api/connections/c1/issue-types', async ({ request }) => {
      body = await request.json();
      return new HttpResponse(null, { status: 204 });
    }),
  );
  // change ONLY "Epic" to Epic, then save
  expect(body).toEqual({ issueTypes: [
    { jiraIssueTypeId: '1', class: 'Other' },
    { jiraIssueTypeId: '2', class: 'Epic'  },
    { jiraIssueTypeId: '3', class: 'Other' },
  ]});
});
```
Note the rationale here differs from CF4's. `PUT /issue-types` is **additive**, so sending only the changed row would *not* lose data. The test pins the full-set send anyway because it keeps this editor's shape identical to CF4's genuinely replace-all editors — one mental model for all three — and because the endpoint validates the entire batch, so a full send also proves every id is still valid. Do **not** carry the "silently deletes the rest" reasoning over to this endpoint; that applies to field mappings and status semantics only.

- [ ] **Step 6: Wire the routes in `App.tsx`**

Inside the existing `<Route element={<RequireAuth />}><Route element={<AppShell />}>` block, add:
```tsx
<Route path="/connections" element={<ConnectionsList />} />
<Route path="/connections/new" element={<AddConnection />} />
<Route path="/connections/:id" element={<ConnectionDetail />} />
```
Leave `QueryClient`'s options untouched.

- [ ] **Step 7: Run and commit**

Run: `npm run test && npm run typecheck && npm run lint` — green.

```bash
git add src/JiraUnpacked.Web/src/features/connections src/JiraUnpacked.Web/src/App.tsx
git commit -m "feat(web): connections list, add form, detail, and issue-type classes"
```

---

## Task 3 (CF3): Projects

**Files:**
- Create: `src/features/projects/AddProject.tsx`, `ProjectDetail.tsx`, `SyncIntervalField.tsx`
- Modify: `src/App.tsx`, `src/features/connections/ConnectionDetail.tsx` (add the **Add project** action)
- Test: `src/features/projects/AddProject.test.tsx`

**Interfaces:**
- Consumes (CF1): `listAvailableProjects`, `addProject`, `getProject`, `updateSyncInterval`, `listConnectionProjects`, `MIN_SYNC_INTERVAL_MINUTES`, `queryKeys`.
- Produces: route `/connections/:connectionId/projects/new` and `/projects/:id`; `ProjectDetail` exposes a **tab area** that CF4 (Fields, Statuses) and CF5 (Overview) fill.

- [ ] **Step 1: Implement `AddProject.tsx`**

The project key must be one Jira actually has, so use a **picker**, never a free-text field — this makes the server's "Unknown Jira project key." 400 unreachable by construction:

```tsx
const { data: available, isPending, error } = useQuery({
  queryKey: queryKeys.availableProjects(connectionId),
  queryFn: () => listAvailableProjects(connectionId),
});
```
Render a `Select` whose options are `` `${p.key} — ${p.name}` `` with `value={p.key}`. Plus `<SyncIntervalField />` (Step 3), defaulting to `60`.

Submit with `useMutation`; on success invalidate `queryKeys.connectionProjects(connectionId)` and navigate to `/projects/${created.id}`.

Error copy — map by status so each failure is actionable:
```tsx
function addProjectError(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Could not add the project.';
  if (error.status === 409) return 'That project is already added.';
  return error.title;   // 400s carry the server's own wording; 502 carries the upstream-Jira message
}
```

- [ ] **Step 2: Implement `ProjectDetail.tsx`**

Reads `:id`. Header: `jiraProjectKey`, a link back to its connection, and the current interval. Below it a tab strip — **Overview**, **Fields**, **Statuses** — held in local state (no router nesting needed; keep it simple). Overview is the default; CF5 fills it, CF4 fills Fields and Statuses. For now render a placeholder per tab so the shell is testable.

CF6 inserts the readiness checklist between the header and the tabs.

- [ ] **Step 3: Implement `SyncIntervalField.tsx`**

A reusable numeric `Field` used by both AddProject and ProjectDetail:

```tsx
export function SyncIntervalField({
  value, onChange, error,
}: { value: number; onChange: (v: number) => void; error?: string }) {
  const tooLow = value < MIN_SYNC_INTERVAL_MINUTES;
  return (
    <Field
      label="Sync interval (minutes)"
      id="sync-interval"
      hint={`Minimum ${MIN_SYNC_INTERVAL_MINUTES} minutes.`}
      error={error ?? (tooLow ? `Sync interval must be at least ${MIN_SYNC_INTERVAL_MINUTES} minutes.` : undefined)}
    >
      <input
        id="sync-interval"
        type="number"
        min={MIN_SYNC_INTERVAL_MINUTES}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
      />
    </Field>
  );
}
```
The client check is a hint that saves a round-trip; the server remains authoritative, so a server 400 must still be rendered when it occurs.

In `ProjectDetail`'s header, the same field plus a **Save interval** button calling `updateSyncInterval`, invalidating `queryKeys.project(id)` on success.

- [ ] **Step 4: Write the failing tests**

`AddProject.test.tsx`:
```tsx
it('offers the connection’s real projects as options, not a text field', async () => {
  server.use(http.get('/api/connections/c1/projects-available', () => HttpResponse.json([
    { id: '1', key: 'ACME', name: 'Acme Platform' },
    { id: '2', key: 'OPS',  name: 'Operations' },
  ])));
  expect(await screen.findByRole('option', { name: 'ACME — Acme Platform' })).toBeInTheDocument();
});

it('reads a 409 as an already-added project', async () => {
  server.use(http.post('/api/connections/c1/projects', () => new HttpResponse(null, { status: 409 })));
  // pick a project, submit
  expect(await screen.findByRole('alert')).toHaveTextContent(/already added/i);
});

it('rejects 59 client-side and accepts 60', async () => {
  const user = userEvent.setup();
  renderAddProject();
  const interval = await screen.findByLabelText(/sync interval/i);
  const submit = screen.getByRole('button', { name: /add project/i });

  await user.clear(interval);
  await user.type(interval, '59');
  expect(screen.getByRole('alert')).toHaveTextContent(/at least 60 minutes/i);
  expect(submit).toBeDisabled();

  await user.clear(interval);
  await user.type(interval, '60');
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  expect(submit).toBeEnabled();
});

it('still surfaces the server 400 when the client thought the interval was fine', async () => {
  server.use(http.post('/api/connections/c1/projects', () =>
    HttpResponse.json({ title: 'Sync interval must be at least 60 minutes.' }, { status: 400 })));
  // submit with 60
  expect(await screen.findByRole('alert')).toHaveTextContent(/at least 60 minutes/i);
});
```

- [ ] **Step 5: Wire routes and the connection-detail action**

In `App.tsx`, inside the guarded shell:
```tsx
<Route path="/connections/:connectionId/projects/new" element={<AddProject />} />
<Route path="/projects/:id" element={<ProjectDetail />} />
```
In `ConnectionDetail.tsx`, the Projects section gains an **Add project** link to `/connections/${id}/projects/new`, and each project links to `/projects/${p.id}`.

- [ ] **Step 6: Run and commit**

Run: `npm run test && npm run typecheck && npm run lint` — green.

```bash
git add src/JiraUnpacked.Web/src/features/projects src/JiraUnpacked.Web/src/features/connections src/JiraUnpacked.Web/src/App.tsx
git commit -m "feat(web): add project from a picker, project detail shell, and sync interval"
```

---

## Task 4 (CF4): Semantic configuration editors

**Files:**
- Create: `src/features/config/FieldMappingsEditor.tsx`, `StatusSemanticsEditor.tsx`
- Modify: `src/features/projects/ProjectDetail.tsx` (mount them in the Fields and Statuses tabs)
- Test: `src/features/config/FieldMappingsEditor.test.tsx`, `StatusSemanticsEditor.test.tsx`

**Interfaces:**
- Consumes (CF1): `listConnectionFields`, `getFieldMappings`, `putFieldMappings`, `listJiraStatuses`, `getStatusSemantics`, `putStatusSemantics`, `FIELD_ROLES`, `STATUS_CLASSES`, types, `queryKeys`. Consumes (CF3): `ProjectDetail`'s tab area, and the project's `jiraConnectionId` (needed to fetch the field list, which is connection-scoped).
- Produces: both editors, mounted in ProjectDetail.

- [ ] **Step 1: Implement `FieldMappingsEditor.tsx`**

Five rows, one per `FieldRole`. A real Jira has hundreds of fields, so an unfiltered `<select>` is unusable — but a custom combobox would mean new ARIA machinery and risk. **Use a text filter input above a native `<select>`**: the filter narrows the options, the `<select>` stays a native accessible control, and its value is the field **id**.

```tsx
export function FieldMappingsEditor({ projectId, connectionId }: { projectId: string; connectionId: string }) {
  const queryClient = useQueryClient();
  const fields = useQuery({ queryKey: queryKeys.connectionFields(connectionId), queryFn: () => listConnectionFields(connectionId) });
  const saved = useQuery({ queryKey: queryKeys.fieldMappings(projectId), queryFn: () => getFieldMappings(projectId) });

  const [filter, setFilter] = useState('');
  const [chosen, setChosen] = useState<Partial<Record<FieldRole, string>>>({});

  if (fields.isPending || saved.isPending) return <Spinner aria-label="Loading field mappings" />;
  if (fields.error || saved.error) return <ErrorState title="Could not load fields." />;

  const current = (role: FieldRole) =>
    chosen[role] ?? saved.data.find((m) => m.role === role)?.jiraFieldId ?? '';

  const visible = fields.data.filter((f) =>
    f.name.toLowerCase().includes(filter.trim().toLowerCase()));

  // Replace-all: send every role that has a field, including ones untouched
  // this session. Sending only edits would delete the rest server-side.
  const save = useMutation({
    mutationFn: () => putFieldMappings(
      projectId,
      FIELD_ROLES
        .map((role) => ({ role, jiraFieldId: current(role) }))
        .filter((m) => m.jiraFieldId !== ''),
    ),
    onSuccess: () => {
      setChosen({});
      void queryClient.invalidateQueries({ queryKey: queryKeys.fieldMappings(projectId) });
    },
  });

  return (
    <section>
      <h2>Field mappings</h2>
      {save.error && (
        <p role="alert">
          {save.error instanceof ApiError ? save.error.title : 'Could not save field mappings.'}
        </p>
      )}
      <Field label="Filter fields" id="field-filter" hint="A Jira site can expose hundreds of fields.">
        <input id="field-filter" value={filter} onChange={(e) => setFilter(e.target.value)} />
      </Field>
      {FIELD_ROLES.map((role) => (
        <Field key={role} label={role} id={`fm-${role}`}>
          <Select
            id={`fm-${role}`}
            value={current(role)}
            onChange={(e) => setChosen((p) => ({ ...p, [role]: e.target.value }))}
          >
            <option value="">— not mapped —</option>
            {visible.map((f) => (
              <option key={f.id} value={f.id}>{f.name}</option>
            ))}
          </Select>
        </Field>
      ))}
      <Button loading={save.isPending} onClick={() => save.mutate()}>Save field mappings</Button>
    </section>
  );
}
```

Note the `<option value={f.id}>{f.name}</option>` split: the user reads the **name**, the form submits the **id**. Submitting the label would send `"Story Points"` where the API expects `"cf_10001"`.

- [ ] **Step 2: Implement `StatusSemanticsEditor.tsx`**

One row per status from `listJiraStatuses(projectId)` (live from Jira → can 502; render the upstream message). Each row: the status name and a `Select` of `STATUS_CLASSES`, defaulting to the saved semantic when one exists, otherwise empty. Rows with no class show a `Badge tone="warning"` reading **Unclassified**, and a line above the table explains that unclassified statuses make cycle-time and throughput uncomputable.

Type the local edit state so "no class yet" is representable, then narrow on submit. Save sends the **complete** list — every classified status, with `statusName` carried through from the Jira status (the API's `StatusSemanticItem` requires it):

```tsx
// '' means "not classified yet" — a state the API has no representation for,
// so it must exist only in local state and be filtered out before the PUT.
const [chosen, setChosen] = useState<Record<string, StatusClass | ''>>({});

const current = (statusId: string): StatusClass | '' =>
  chosen[statusId] ?? saved.data.find((s) => s.jiraStatusId === statusId)?.class ?? '';

const save = useMutation({
  mutationFn: () => putStatusSemantics(
    projectId,
    statuses.data
      .map((s) => ({ jiraStatusId: s.id, statusName: s.name, class: current(s.id) }))
      .filter((s): s is StatusSemantic => s.class !== ''),
  ),
  onSuccess: () => {
    setChosen({});
    void queryClient.invalidateQueries({ queryKey: queryKeys.statusSemantics(projectId) });
  },
});
```

The type predicate is what makes `class: StatusClass | ''` narrow to `StatusSemantic` — without the explicit `''` in the state type, TypeScript would infer `StatusClass` and the predicate would be a lie.

- [ ] **Step 3: Write the failing tests — the replace-all guard is the centrepiece**

`FieldMappingsEditor.test.tsx`:
```tsx
it('sends every mapped role on save, not only the edited one', async () => {
  server.use(
    http.get('/api/connections/c1/fields', () => HttpResponse.json([
      { id: 'status',    name: 'Status',       custom: false },
      { id: 'assignee',  name: 'Assignee',     custom: false },
      { id: 'cf_10001',  name: 'Story Points', custom: true  },
    ])),
    http.get('/api/projects/p1/field-mappings', () => HttpResponse.json([
      { role: 'Status',   jiraFieldId: 'status'   },
      { role: 'Assignee', jiraFieldId: 'assignee' },
    ])),
    http.put('/api/projects/p1/field-mappings', async ({ request }) => {
      body = await request.json();
      return new HttpResponse(null, { status: 204 });
    }),
  );
  // change ONLY StoryPoints -> cf_10001, then save
  expect(body).toEqual({ mappings: [
    { role: 'Status',      jiraFieldId: 'status'   },
    { role: 'Assignee',    jiraFieldId: 'assignee' },
    { role: 'StoryPoints', jiraFieldId: 'cf_10001' },
  ]});
});

it('filters options by name and submits the field id, not the label', async () => {
  const user = userEvent.setup();
  renderEditor();
  await screen.findByLabelText('StoryPoints');

  await user.type(screen.getByLabelText(/filter fields/i), 'story');

  // The filter narrows the options...
  expect(screen.getByRole('option', { name: 'Story Points' })).toBeInTheDocument();
  expect(screen.queryByRole('option', { name: 'Assignee' })).not.toBeInTheDocument();

  // ...and the value submitted is the id, never the visible label.
  await user.selectOptions(screen.getByLabelText('StoryPoints'), 'cf_10001');
  await user.click(screen.getByRole('button', { name: /save field mappings/i }));

  expect(body).toMatchObject({
    mappings: expect.arrayContaining([{ role: 'StoryPoints', jiraFieldId: 'cf_10001' }]),
  });
  expect(JSON.stringify(body)).not.toContain('Story Points');
});
```

`StatusSemanticsEditor.test.tsx`:
```tsx
it('sends every classified status on save, not only the edited one', async () => { /* same shape */ });

it('flags statuses that have no class yet', async () => {
  server.use(
    http.get('/api/projects/p1/jira-statuses', () => HttpResponse.json([
      { id: '1', name: 'To Do' }, { id: '2', name: 'Done' },
    ])),
    http.get('/api/projects/p1/status-semantics', () => HttpResponse.json([
      { jiraStatusId: '1', statusName: 'To Do', class: 'Active' },
    ])),
  );
  expect(await screen.findByText(/unclassified/i)).toBeInTheDocument();
});
```
Assert on the exact array — a partial-patch implementation passes a "did it PUT?" test but fails these, and in production it silently deletes the user's other mappings.

- [ ] **Step 4: Mount in ProjectDetail and run**

Fields tab → `<FieldMappingsEditor projectId={id} connectionId={project.jiraConnectionId} />`; Statuses tab → `<StatusSemanticsEditor projectId={id} />`.

Run: `npm run test && npm run typecheck && npm run lint` — green.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Web/src/features/config src/JiraUnpacked.Web/src/features/projects/ProjectDetail.tsx
git commit -m "feat(web): field-mapping and status-semantics editors with replace-all saves"
```

---

## Task 5 (CF5): Sync controls and run history

**Files:**
- Create: `src/features/sync/SyncControls.tsx`, `SyncRunTable.tsx`, `useSyncRuns.ts`
- Modify: `src/features/projects/ProjectDetail.tsx` (Overview tab)
- Test: `src/features/sync/useSyncRuns.test.tsx`, `SyncControls.test.tsx`

**Interfaces:**
- Consumes (CF1): `listSyncRuns`, `runSync`, `runRederive`, `SyncRun`, `SyncRunStatus`, `queryKeys`.
- Produces: `useSyncRuns(projectId)` returning the query result; `SyncControls` and `SyncRunTable` mounted in the Overview tab.

- [ ] **Step 1: Implement `useSyncRuns.ts` — poll only while something is running**

```ts
/** 3s while any run is Running; stops entirely when none is. A fixed
 *  forever-poll would hammer the API for a table that never changes. */
export const SYNC_POLL_MS = 3000;

export function useSyncRuns(projectId: string) {
  return useQuery({
    queryKey: queryKeys.syncRuns(projectId),
    queryFn: () => listSyncRuns(projectId),
    refetchInterval: (query) => {
      const runs = query.state.data as SyncRun[] | undefined;
      return runs?.some((r) => r.status === 'Running') ? SYNC_POLL_MS : false;
    },
  });
}
```

- [ ] **Step 2: Implement `SyncControls.tsx`**

Two buttons. **Sync now** → `runSync`; **Re-derive** → `runRederive`, with the explanation *"Rebuild the modelled data from what was already pulled from Jira. Use this after changing field mappings or status semantics."* — that is exactly when it is needed, and it never calls Jira.

Both mutations invalidate `queryKeys.syncRuns(projectId)` on success so the table updates and polling restarts.

```tsx
function syncError(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Could not start the sync.';
  if (error.status === 409) return 'A sync is already running for this project.';
  return error.title;   // 429 already carries the rate-limit message from toApiError
}
```
The `sync` limiter is **5/min per user**, and a user clicking twice will trip it — so the 429 path is a normal case, not an edge case.

- [ ] **Step 3: Implement `SyncRunTable.tsx`**

Columns: Kind, Status, Started, Duration, Issues, Error. Status uses `Badge`: `Succeeded`→`success`, `Failed`→`danger`, `PartiallyFailed`→`warning`, `Running`→`neutral` with a `Spinner`. Duration is `finishedAt − startedAt`, or "running" when `finishedAt` is null. `error` renders verbatim — `SyncRun.Error` is server-curated (Phase 7 Spec 3 fixed the raw-exception leak), so it is safe to display. Empty → `EmptyState` "No syncs yet".

- [ ] **Step 4: Write the failing polling tests — assert request counts with fake timers**

`useSyncRuns.test.tsx`:
```tsx
it('polls while a run is Running', async () => {
  vi.useFakeTimers();
  let calls = 0;
  server.use(http.get('/api/projects/p1/sync-runs', () => {
    calls++;
    return HttpResponse.json([{ id: 'r1', jiraProjectId: 'p1', status: 'Running', startedAt: '2026-01-01T00:00:00Z', finishedAt: null, issuesProcessed: 0, error: null, nextRunAt: null, kind: 'Sync' }]);
  }));
  // render the hook, await the first fetch
  await vi.advanceTimersByTimeAsync(SYNC_POLL_MS * 2);
  expect(calls).toBeGreaterThanOrEqual(3);   // initial + two polls
});

it('stops polling once the last run finishes', async () => {
  // first response Running, subsequent responses Succeeded
  // after the Succeeded response lands, advance 3 more intervals
  const settled = calls;
  await vi.advanceTimersByTimeAsync(SYNC_POLL_MS * 3);
  expect(calls).toBe(settled);   // no further requests
});

it('never polls a project with no runs', async () => {
  // handler returns []
  await vi.advanceTimersByTimeAsync(SYNC_POLL_MS * 3);
  expect(calls).toBe(1);
});
```
Assert **request counts**, not elapsed time — a wall-clock assertion is flaky and proves nothing.

`SyncControls.test.tsx`: a 409 renders "already running"; a 429 renders the rate-limit message; a successful trigger refetches the runs query.

- [ ] **Step 5: Mount in the Overview tab, run, and commit**

Run: `npm run test && npm run typecheck && npm run lint` — green.

```bash
git add src/JiraUnpacked.Web/src/features/sync src/JiraUnpacked.Web/src/features/projects/ProjectDetail.tsx
git commit -m "feat(web): sync controls and run history with run-aware polling"
```

---

## Task 6 (CF6): Readiness checklist, navigation, and integration

**Files:**
- Create: `src/features/config/readiness.ts`, `src/features/config/ReadinessChecklist.tsx`
- Modify: `src/features/projects/ProjectDetail.tsx`, `src/shell/SideNav.tsx`, `src/routes/Home.tsx`
- Test: `src/features/config/readiness.test.ts`, `src/features/config/ReadinessChecklist.test.tsx`

**Interfaces:**
- Consumes (CF1–CF5): the mapping/status/issue-type queries, `ProjectDetail`, `SyncControls`.
- Produces: `deriveReadiness(input): Readiness` (pure), the checklist component, and the finished navigation.

- [ ] **Step 1: Write the failing readiness test — pure, no rendering**

`readiness.test.ts`:
```ts
import { describe, expect, it } from 'vitest';
import { deriveReadiness } from './readiness';

const base = {
  fieldMappings: [{ role: 'Status' as const, jiraFieldId: 'status' }],
  jiraStatuses: [{ id: '1', name: 'To Do' }, { id: '2', name: 'Done' }],
  statusSemantics: [
    { jiraStatusId: '1', statusName: 'To Do', class: 'Active' as const },
    { jiraStatusId: '2', statusName: 'Done',  class: 'Done'   as const },
  ],
  issueTypes: [{ jiraIssueTypeId: '1', name: 'Epic', class: 'Epic' as const }],
};

describe('deriveReadiness', () => {
  it('is complete when all three steps are satisfied', () => {
    const r = deriveReadiness(base);
    expect(r.fieldMappings.ready).toBe(true);
    expect(r.statusSemantics.ready).toBe(true);
    expect(r.issueTypes.ready).toBe(true);
    expect(r.allReady).toBe(true);
  });

  it('is not ready when no field is mapped', () => {
    expect(deriveReadiness({ ...base, fieldMappings: [] }).fieldMappings.ready).toBe(false);
  });

  // The subtle one: SOME statuses classified is NOT ready. A "non-empty"
  // check would pass here and let a user believe reporting is trustworthy.
  it('is not ready when status coverage is only partial', () => {
    const r = deriveReadiness({ ...base, statusSemantics: [base.statusSemantics[0]] });
    expect(r.statusSemantics.ready).toBe(false);
    expect(r.statusSemantics.missingStatusNames).toEqual(['Done']);
    expect(r.allReady).toBe(false);
  });

  it('is ready with zero statuses to classify (nothing is missing)', () => {
    const r = deriveReadiness({ ...base, jiraStatuses: [], statusSemantics: [] });
    expect(r.statusSemantics.ready).toBe(true);
  });

  it('is not ready when no issue type is classed Epic', () => {
    const r = deriveReadiness({ ...base, issueTypes: [{ jiraIssueTypeId: '1', name: 'Story', class: 'Other' }] });
    expect(r.issueTypes.ready).toBe(false);
  });

  it('ignores semantics for statuses Jira no longer returns', () => {
    const r = deriveReadiness({
      ...base,
      statusSemantics: [...base.statusSemantics, { jiraStatusId: '99', statusName: 'Gone', class: 'Other' }],
    });
    expect(r.statusSemantics.ready).toBe(true);
  });
});
```

- [ ] **Step 2: Implement `readiness.ts` (pure)**

```ts
import type { FieldMapping, IssueTypeClassInfo, JiraStatus, StatusSemantic } from '../../api/mappings';

export interface ReadinessInput {
  fieldMappings: FieldMapping[];
  jiraStatuses: JiraStatus[];
  statusSemantics: StatusSemantic[];
  issueTypes: IssueTypeClassInfo[];
}

export interface ReadinessStep { ready: boolean }
export interface StatusStep extends ReadinessStep { missingStatusNames: string[] }

export interface Readiness {
  fieldMappings: ReadinessStep;
  statusSemantics: StatusStep;
  issueTypes: ReadinessStep;
  allReady: boolean;
}

export function deriveReadiness(input: ReadinessInput): Readiness {
  const classified = new Set(input.statusSemantics.map((s) => s.jiraStatusId));
  // Every status Jira currently reports must be classified. Partial coverage
  // is NOT ready: cycle-time and throughput silently exclude the unclassified
  // ones, which looks like real data rather than a configuration gap.
  const missingStatusNames = input.jiraStatuses
    .filter((s) => !classified.has(s.id))
    .map((s) => s.name);

  const fieldMappings = { ready: input.fieldMappings.length > 0 };
  const statusSemantics = { ready: missingStatusNames.length === 0, missingStatusNames };
  const issueTypes = { ready: input.issueTypes.some((t) => t.class === 'Epic') };

  return {
    fieldMappings,
    statusSemantics,
    issueTypes,
    allReady: fieldMappings.ready && statusSemantics.ready && issueTypes.ready,
  };
}
```

- [ ] **Step 3: Implement `ReadinessChecklist.tsx`**

Three rows, each a `Badge` (`success` when ready, `warning` otherwise) plus a link to the tab that fixes it:

| Step | Incomplete copy |
|---|---|
| Field mappings | "No fields mapped — team, assignee, sprint, and story points will stay empty." → Fields tab |
| Status semantics | "N statuses unclassified: *names* — cycle time and throughput cannot be computed until every status has a class." → Statuses tab |
| Epic issue type | "No issue type is classed Epic — epic gap-checks will report *not configured*." → the connection's issue-types editor |

When `allReady`, render a single success line instead of three rows.

**The checklist never disables anything.** Re-syncing a deliberately partially-configured project is legitimate, and blocking it would be paternalistic.

- [ ] **Step 4: Write the failing checklist test**

```tsx
it('does not disable the sync button when setup is incomplete', async () => {
  // render ProjectDetail with mappings empty and statuses unclassified
  expect(await screen.findByText(/statuses unclassified/i)).toBeInTheDocument();
  expect(screen.getByRole('button', { name: /sync now/i })).toBeEnabled();
});

it('names the unclassified statuses so the user knows what to fix', async () => {
  expect(await screen.findByText(/Done/)).toBeInTheDocument();
});
```
The first test encodes a deliberate product decision — if a later change gates sync on readiness, it must fail.

- [ ] **Step 5: Mount the checklist and finish navigation**

In `ProjectDetail`, fetch the three lists and render `<ReadinessChecklist readiness={deriveReadiness({...})} projectId={id} connectionId={project.jiraConnectionId} />` between the header and the tabs.

`SideNav.tsx` gains **Connections** (`/connections`) and **Projects** (`/`) alongside Home — replace the lone Home link with these two.

`Home.tsx` becomes the projects landing. **There is no `GET /api/projects`**, so fan out: query `listConnections()`, then one `listConnectionProjects` per connection via `useQueries`, and flatten. This is acceptable because a self-hosted tenant has one or two connections; do **not** add a backend endpoint for it.

```tsx
const connections = useQuery({ queryKey: queryKeys.connections(), queryFn: listConnections });
const projectQueries = useQueries({
  queries: (connections.data ?? []).map((c) => ({
    queryKey: queryKeys.connectionProjects(c.id),
    queryFn: () => listConnectionProjects(c.id),
  })),
});
```
Empty (no connections) → `EmptyState` "No connections yet" linking to `/connections/new`. Connections but no projects → `EmptyState` "No projects yet" linking to the connection. Otherwise a table of projects linking to `/projects/:id`.

- [ ] **Step 6: Full verification**

Run from `src/JiraUnpacked.Web/`: `npm run test && npm run typecheck && npm run lint && npm run build` — all green; report the count.
Run from the repo root: `dotnet test JiraUnpacked.sln --configuration Release` — **278 passed**, unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): readiness checklist, navigation, and projects landing"
```

---

## Final verification (controller, after all tasks)

- [ ] From `src/JiraUnpacked.Web/`: `npm run typecheck && npm run lint && npm run test && npm run build` → all green.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → **278 passed**, 0 failed (unchanged — this spec touches no .NET file).
- [ ] `git diff develop..HEAD --stat -- src/JiraUnpacked.Api src/JiraUnpacked.Domain src/JiraUnpacked.Infrastructure tests/` → **empty**. Any backend file in this diff is a Critical defect.
- [ ] `git diff develop..HEAD -- src/JiraUnpacked.Web/package.json` → no dependency added.
- [ ] `grep -rn "from '\.\./api\|from '\.\./auth" src/JiraUnpacked.Web/src/ui/` → no matches (layering intact).
- [ ] `grep -rn "console\.\(log\|debug\|info\)" src/JiraUnpacked.Web/src/` → no matches printing a token, password, or request body.
- [ ] `git diff develop..HEAD -- src/JiraUnpacked.Web/src/auth/session.ts` → empty (single-flight untouched).
- [ ] Confirm `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is unmodified.
- [ ] Push `feat/web-ui-connect-configure`; PR → develop; CI green on **both** jobs; squash-merge. The `develop → main` release is a separate, user-approved step.

## Notes for execution

- **The replace-all tests are the most valuable in this plan.** All three configuration PUTs replace the entire set. An editor that sends only what the user touched passes a naive "did it save?" test and silently deletes the rest of their configuration on the server. Assert the exact submitted array in every editor.
- **Partial status coverage is not readiness.** A `length > 0` check would report a project ready while cycle time silently ignores the unclassified statuses — data that looks real and is wrong.
- **Readiness informs, never gates.** There is an explicit test that sync stays enabled while setup is incomplete.
- **Poll only while a run is Running.** Assert request counts under fake timers; never assert on elapsed wall-clock time.
- The API token is write-only — the server never returns it, so no screen may attempt to display one.
- Deferred to later specs: issue list + FR-E4 filter builder + issue detail (Spec 3); metrics, gap-checks, admin (Spec 4). Known API gaps worth a future backend spec: no connection update/delete, no global project list, no sub-run progress.
