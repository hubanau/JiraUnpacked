import { http, HttpResponse } from 'msw';
import type { SavedView, SaveViewRequest } from '../api/views';
import { server } from './setup';

/** No saved views, for any project: for tests that mount IssueList but are not about views. */
export const noSavedViews = http.get('/api/projects/:projectId/filters', () => HttpResponse.json([]));

export function savedView(overrides: Partial<SavedView> & Pick<SavedView, 'id' | 'name'>): SavedView {
  return {
    conditions: null, filter: null, columns: null, sortBy: null, sortDescending: null, isDefault: false,
    createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-01T00:00:00Z',
    ...overrides,
  };
}

export interface ViewsApiMock {
  /** The server's current list — reassigned by every write, so read it through this object. */
  views: SavedView[];
  requests: { method: 'POST' | 'PUT' | 'DELETE'; id?: string; body?: SaveViewRequest }[];
}

/**
 * The saved-view endpoints of one project, backed by an in-memory list that behaves like the
 * server: names unique (409), one default (taking it clears the others), listed default-first then
 * by name. Stateful, because the SPA refetches after every write and a static list would undo it.
 */
export function mockViewsApi(initial: SavedView[], projectId = 'p1'): ViewsApiMock {
  const mock: ViewsApiMock = { views: [...initial], requests: [] };
  const base = `/api/projects/${projectId}/filters`;
  let created = 0;

  const sorted = () => [...mock.views].sort((a, b) =>
    Number(b.isDefault) - Number(a.isDefault) || a.name.toLowerCase().localeCompare(b.name.toLowerCase()));
  const nameTaken = (name: string, except?: string) => mock.views.some((v) => v.name === name && v.id !== except);
  const taken = (name: string) =>
    HttpResponse.json({ title: `A view named '${name}' already exists in this project.` }, { status: 409 });
  const clearDefaults = () => {
    mock.views = mock.views.map((v) => (v.isDefault ? { ...v, isDefault: false } : v));
  };
  const fromBody = (id: string, body: SaveViewRequest, previous?: SavedView): SavedView => ({
    id,
    name: body.name,
    conditions: null,
    filter: body.filter ?? null,
    columns: body.columns ?? null,
    sortBy: body.sortBy ?? null,
    sortDescending: body.sortDescending ?? null,
    isDefault: body.isDefault ?? previous?.isDefault ?? false,
    createdAt: previous?.createdAt ?? '2026-09-23T00:00:00Z',
    updatedAt: '2026-09-23T12:00:00Z',
  });

  server.use(
    http.get(base, () => HttpResponse.json(sorted())),
    http.post(base, async ({ request }) => {
      const body = (await request.json()) as SaveViewRequest;
      mock.requests.push({ method: 'POST', body });
      if (nameTaken(body.name)) return taken(body.name);
      created += 1;
      const saved = fromBody(`created-${created}`, body);
      if (saved.isDefault) clearDefaults();
      mock.views = [...mock.views, saved];
      return HttpResponse.json(saved, { status: 201 });
    }),
    http.put(`${base}/:viewId`, async ({ request, params }) => {
      const id = String(params.viewId);
      const body = (await request.json()) as SaveViewRequest;
      mock.requests.push({ method: 'PUT', id, body });
      const previous = mock.views.find((v) => v.id === id);
      if (!previous) return new HttpResponse(null, { status: 404 });
      if (nameTaken(body.name, id)) return taken(body.name);
      const saved = fromBody(id, body, previous);
      if (saved.isDefault) clearDefaults();
      mock.views = mock.views.map((v) => (v.id === id ? saved : v));
      return HttpResponse.json(saved);
    }),
    http.delete(`${base}/:viewId`, ({ params }) => {
      const id = String(params.viewId);
      mock.requests.push({ method: 'DELETE', id });
      if (!mock.views.some((v) => v.id === id)) return new HttpResponse(null, { status: 404 });
      mock.views = mock.views.filter((v) => v.id !== id);
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return mock;
}
