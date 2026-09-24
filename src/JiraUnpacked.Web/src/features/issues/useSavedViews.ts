import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { listViews, type SavedView } from '../../api/views';
import { queryKeys } from '../../api/queryKeys';
import type { ListParams } from './useListState';
import { isModified, viewParams, type ListSnapshot } from './viewState';

/** The parts of useListState that saved views drive. */
export interface ViewListControls {
  viewId: string | null;
  hasListState: boolean;
  applyParams: (next: ListParams, options: { replace: boolean }) => void;
  setViewId: (id: string | null) => void;
}

export interface SavedViews {
  /** Undefined until loaded (or when the request failed). */
  views: SavedView[] | undefined;
  /** No views could be loaded at all: the menu says so and the list carries on without views. */
  unavailable: boolean;
  /** The view named by `view=`, when it is in the list. */
  loaded: SavedView | undefined;
  /** The list differs from the loaded view in something Save would write. */
  modified: boolean;
  /** Opens a view: its whole URL, pushed, so Back returns to what the list showed. */
  apply: (view: SavedView) => void;
  /**
   * True while a list opened on a bare URL waits to learn whether a default view opens instead.
   * The issue query waits on it, so the first request is the default view's rather than a flash of
   * the unfiltered list. Never true for a URL that already says what to show.
   */
  holdQuery: boolean;
}

/** The project's saved views, which one the list shows, and the default view on a bare URL. */
export function useSavedViews(projectId: string, list: ViewListControls, snapshot: ListSnapshot): SavedViews {
  const { viewId, hasListState, applyParams, setViewId } = list;
  const query = useQuery({ queryKey: queryKeys.views(projectId), queryFn: () => listViews(projectId) });
  const views = query.data;

  const loaded = viewId ? views?.find((v) => v.id === viewId) : undefined;
  const modified = useMemo(() => (loaded ? isModified(loaded, snapshot) : false), [loaded, snapshot]);

  const apply = useCallback(
    (view: SavedView) => applyParams(viewParams(view), { replace: false }),
    [applyParams],
  );

  // Decided once, from the URL the list opened with: a bare URL is the only case in which a
  // default view may step in. Clear all, or coming back to a bare URL later, never re-applies it.
  const [bareAtMount] = useState(() => !hasListState);
  const [defaultSettled, setDefaultSettled] = useState(!bareAtMount);
  const defaultRequested = useRef<string | null>(null);

  useEffect(() => {
    if (defaultSettled) return;
    if (defaultRequested.current !== null) {
      // Applied; the query may go once the URL carries it (the tree is adopted in the same render).
      if (viewId === defaultRequested.current) setDefaultSettled(true);
      return;
    }
    // List state that arrived before the views (a link followed while they loaded) wins; and a
    // failed views request means no default — the list proceeds unfiltered.
    if (hasListState || query.isError) {
      setDefaultSettled(true);
      return;
    }
    if (!views) return;
    const fallback = views.find((v) => v.isDefault);
    if (!fallback) {
      setDefaultSettled(true);
      return;
    }
    defaultRequested.current = fallback.id;
    // Replace: the bare URL it stands in for is not somewhere Back should return to.
    applyParams(viewParams(fallback), { replace: true });
  }, [defaultSettled, viewId, hasListState, query.isError, views, applyParams]);

  // A view id that names no view — deleted elsewhere, or from someone else's link — is dropped
  // silently; the rest of the URL still applies. Not while a refetch may still bring it.
  useEffect(() => {
    if (viewId !== null && views && !query.isFetching && !views.some((v) => v.id === viewId)) {
      setViewId(null);
    }
  }, [viewId, views, query.isFetching, setViewId]);

  // Only when there is nothing to show: a failed background refetch keeps the list it already has.
  return { views, unavailable: query.isError && !views, loaded, modified, apply, holdQuery: !defaultSettled };
}
