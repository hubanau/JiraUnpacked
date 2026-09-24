import type { SavedView, SaveViewRequest } from '../../api/views';
import { ISSUE_COLUMNS } from './columns';
import { emptyRoot, parseFilterParam, toSavable, toUrlJson, type EditorGroup } from './filterTree';
import { DEFAULT_COLUMNS, DEFAULT_SORT, type ListParams } from './useListState';

/** What the list shows right now — the part of it a view can store. */
export interface ListSnapshot {
  tree: EditorGroup;
  sortBy: string;
  sortDescending: boolean;
  columns: string[];
}

const COLUMN_ORDER = ISSUE_COLUMNS.map((c) => c.id);

/** Columns as the table shows them: table order, ids the table knows, each once. */
export const shownColumns = (columns: readonly string[]): string[] =>
  COLUMN_ORDER.filter((id) => columns.includes(id));

// The URL's own normalisation (see useListState): a default is an absent param.
const colsParam = (columns: readonly string[]): string | null => {
  const joined = shownColumns(columns).join(',');
  return joined === DEFAULT_COLUMNS.join(',') ? null : joined;
};
const sortByParam = (sortBy: string): string | null => (sortBy === DEFAULT_SORT ? null : sortBy);
const sortDescParam = (descending: boolean): string | null => (descending ? null : 'false');

// What a view leaves out is the list's default. An empty column list would show a table with no
// columns at all, so it counts as "not stored" too.
const viewSortBy = (v: SavedView): string => v.sortBy ?? DEFAULT_SORT;
const viewSortDescending = (v: SavedView): boolean => v.sortDescending ?? true;
const viewColumns = (v: SavedView): readonly string[] =>
  v.columns && v.columns.length > 0 ? v.columns : DEFAULT_COLUMNS;

/**
 * A view's filter as the editor holds it. Read through the same parser as a link, so the API's
 * explicit nulls (`"value": null`, `"groups": null`) and a flat `conditions` list written by an
 * older client come out as the one tree shape everything else here compares.
 */
export function viewTree(view: SavedView): EditorGroup {
  if (view.filter) return parseFilterParam(JSON.stringify(view.filter), null);
  if (view.conditions && view.conditions.length > 0) {
    return parseFilterParam(JSON.stringify(view.conditions), 'And');
  }
  return emptyRoot();
}

/** The URL a view opens at, spelled exactly as useListState writes it, plus view=<id>. */
export function viewParams(view: SavedView): ListParams {
  return {
    filter: toUrlJson(viewTree(view)),
    sortBy: sortByParam(viewSortBy(view)),
    sortDesc: sortDescParam(viewSortDescending(view)),
    cols: colsParam(viewColumns(view)),
    view: view.id,
  };
}

function fingerprint(tree: EditorGroup, sortBy: string, descending: boolean, columns: readonly string[]): string {
  return JSON.stringify([toSavable(tree) ?? null, sortByParam(sortBy), sortDescParam(descending), colsParam(columns)]);
}

/**
 * Whether the list differs from the view in anything Save would write. Both sides go through
 * toSavable and the URL's normalisation (defaults ≡ absent, columns in table order), so a row that
 * is still being filled in, or columns listed in another order, is not a change — and saving always
 * clears the marker.
 */
export function isModified(view: SavedView, current: ListSnapshot): boolean {
  return fingerprint(viewTree(view), viewSortBy(view), viewSortDescending(view), viewColumns(view))
    !== fingerprint(current.tree, current.sortBy, current.sortDescending, current.columns);
}

/** The body that stores the list as it is now under `name` (isDefault left out: see SaveViewRequest). */
export function saveRequest(name: string, current: ListSnapshot): SaveViewRequest {
  const filter = toSavable(current.tree);
  return {
    name,
    ...(filter ? { filter } : {}),
    columns: shownColumns(current.columns),
    sortBy: current.sortBy,
    sortDescending: current.sortDescending,
  };
}

/**
 * The body that renames a view or moves the default flag while keeping exactly what it stores.
 * A PUT replaces the whole definition, so the stored one is sent back — never the list's edits.
 */
export function resaveRequest(view: SavedView, changes: { name?: string; isDefault?: boolean }): SaveViewRequest {
  const filter = toSavable(viewTree(view));
  return {
    name: changes.name ?? view.name,
    ...(filter ? { filter } : {}),
    ...(view.columns ? { columns: view.columns } : {}),
    ...(view.sortBy !== null ? { sortBy: view.sortBy } : {}),
    ...(view.sortDescending !== null ? { sortDescending: view.sortDescending } : {}),
    ...(changes.isDefault !== undefined ? { isDefault: changes.isDefault } : {}),
  };
}

/** The dropdown's order, the server's: the default first, then by name ignoring case. */
export function sortViews(views: SavedView[]): SavedView[] {
  return [...views].sort((a, b) =>
    Number(b.isDefault) - Number(a.isDefault) || a.name.toLowerCase().localeCompare(b.name.toLowerCase()));
}

/** The cached list after `saved` was written: replaced or added, and the only default if it is one. */
export function withView(views: SavedView[], saved: SavedView): SavedView[] {
  const others = views
    .filter((v) => v.id !== saved.id)
    .map((v) => (saved.isDefault && v.isDefault ? { ...v, isDefault: false } : v));
  return sortViews([...others, saved]);
}

export const withoutView = (views: SavedView[], id: string): SavedView[] => views.filter((v) => v.id !== id);
