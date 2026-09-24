import { useCallback, useMemo } from 'react';
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
  /** The request failed: the menu says so and the list carries on without views. */
  unavailable: boolean;
  /** The view named by `view=`, when it is in the list. */
  loaded: SavedView | undefined;
  /** The list differs from the loaded view in something Save would write. */
  modified: boolean;
  /** Opens a view: its whole URL, pushed, so Back returns to what the list showed. */
  apply: (view: SavedView) => void;
}

/** The project's saved views, and which of them — if any — the list is showing. */
export function useSavedViews(projectId: string, list: ViewListControls, snapshot: ListSnapshot): SavedViews {
  const { viewId, applyParams } = list;
  const query = useQuery({ queryKey: queryKeys.views(projectId), queryFn: () => listViews(projectId) });
  const views = query.data;

  const loaded = viewId ? views?.find((v) => v.id === viewId) : undefined;
  const modified = useMemo(() => (loaded ? isModified(loaded, snapshot) : false), [loaded, snapshot]);

  const apply = useCallback(
    (view: SavedView) => applyParams(viewParams(view), { replace: false }),
    [applyParams],
  );

  return { views, unavailable: query.isError, loaded, modified, apply };
}
