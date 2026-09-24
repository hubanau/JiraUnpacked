import { apiFetch } from './client';
import type { FilterCondition, FilterGroup } from './issues';

/**
 * A saved view: a filter plus the list's columns and sort, private to its user and project. The
 * API still calls it a saved filter (the route is /filters); the SPA calls it what it now is.
 * Every member the view does not store comes back null — "use the list's default".
 * `conditions` is the flat list older clients could write; this SPA only ever writes `filter`.
 */
export interface SavedView {
  id: string;
  name: string;
  conditions: FilterCondition[] | null;
  filter: FilterGroup | null;
  columns: string[] | null;
  sortBy: string | null;
  sortDescending: boolean | null;
  isDefault: boolean;
  createdAt: string;
  updatedAt: string;
}

/**
 * Body of create and update. Leave a member out to store nothing for it. `isDefault` left out:
 * false on create, unchanged on update; true makes this the project's only default.
 */
export interface SaveViewRequest {
  name: string;
  filter?: FilterGroup;
  columns?: string[];
  sortBy?: string;
  sortDescending?: boolean;
  isDefault?: boolean;
}

const base = (projectId: string) => `/api/projects/${projectId}/filters`;

/** Default first, then by name (the server's order). */
export const listViews = (projectId: string) => apiFetch<SavedView[]>(base(projectId));

export const createView = (projectId: string, body: SaveViewRequest) =>
  apiFetch<SavedView>(base(projectId), { method: 'POST', body });

export const updateView = (projectId: string, viewId: string, body: SaveViewRequest) =>
  apiFetch<SavedView>(`${base(projectId)}/${viewId}`, { method: 'PUT', body });

export const deleteView = (projectId: string, viewId: string) =>
  apiFetch<void>(`${base(projectId)}/${viewId}`, { method: 'DELETE' });
