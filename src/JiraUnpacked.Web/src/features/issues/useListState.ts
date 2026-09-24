import { useCallback, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { parseFilterParam, toUrlJson, type EditorGroup } from './filterTree';

export const DEFAULT_SORT = 'updatedAt';

/** The columns the table has always shown. Ids match the filter and sort vocabulary. */
export const DEFAULT_COLUMNS = [
  'jiraKey', 'summary', 'status', 'issueType', 'assignee', 'sprint', 'storyPoints', 'updatedAt',
];

/** How many of this hook's own recent writes it recognises when the URL catches up with them. */
const REMEMBERED_WRITES = 20;

/** Every URL param that is part of the list's view. A URL with none of them is a bare list. */
export const LIST_PARAMS = ['filter', 'logic', 'sortBy', 'sortDesc', 'cols', 'view'] as const;

/**
 * A whole list view as URL params, spelled exactly as this hook writes them: null = absent, which
 * for sortBy/sortDesc/cols means the default. `view` names the saved view the list was opened from.
 */
export interface ListParams {
  filter: string | null;
  sortBy: string | null;
  sortDesc: string | null;
  cols: string | null;
  view: string | null;
}

/**
 * The list's whole view — filter tree, sort, columns — read from and written to the query string.
 *
 * The URL rather than localStorage: only ju.username and ju.theme may be persisted, and a URL is
 * shareable, survives reload and Back. A default is written as an ABSENT param, so an untouched
 * list has a clean URL. The filter is written unpruned (disabled parts survive a reload) and
 * always as a tree; the legacy `logic` param is read once and dropped on the first write.
 */
export function useListState() {
  const [params, setParams] = useSearchParams();
  const rawFilter = params.get('filter');
  const rawLogic = params.get('logic');

  // The strings this hook itself wrote to `filter=`. React Router applies URL updates in a
  // transition, so our own write can land a render or more after the edit. When it does, that
  // is an echo, not news: re-parsing it would mint fresh ids and remount every row, dropping
  // focus and any half-typed value mid-keystroke. Read in render, only ever written in setTree.
  const recentWrites = useRef<string[]>([]);

  const [held, setHeld] = useState(() => ({
    raw: rawFilter, logic: rawLogic, tree: parseFilterParam(rawFilter, rawLogic),
  }));

  let tree = held.tree;
  if (held.raw !== rawFilter || held.logic !== rawLogic) {
    // The URL moved. Our own write arriving keeps the tree already held (it may even be newer
    // than the URL); anything else — Back, a pasted link — is adopted from the URL.
    //
    // A match only counts as an echo of the OLDEST still-unconfirmed write it could be: found by
    // index, not just membership. Once found, that entry and every one before it are dropped —
    // they are now confirmed (or superseded) — so a later URL that happens to carry the same
    // JSON (e.g. Clear All's '', landing again after a fresh navigation) is never mistaken for a
    // write whose own echo already arrived and was consumed; it is adopted fresh instead.
    const matchIndex = rawLogic === null ? recentWrites.current.indexOf(rawFilter ?? '') : -1;
    if (matchIndex === -1) {
      tree = parseFilterParam(rawFilter, rawLogic);
    } else {
      recentWrites.current = recentWrites.current.slice(matchIndex + 1);
    }
    setHeld({ raw: rawFilter, logic: rawLogic, tree });
  }

  // React Router's setSearchParams updater is handed the searchParams of the last COMMITTED
  // render, not of a sibling call earlier in the same tick — so a filter write immediately
  // followed by a sort write, before either has committed, would each build on the same stale
  // base and the second would silently overwrite the first. This ref is this hook's own record
  // of "what the URL should now contain": advanced by every patch, so the next patch in the same
  // tick builds on top of the one just made rather than on a params snapshot that has not caught
  // up yet. It is refreshed from the committed params only when `params`'s IDENTITY has actually
  // changed — not on every render — because a render can happen (e.g. from unrelated state)
  // before the router's own transition has committed; refreshing unconditionally would stomp a
  // patch already recorded here with the stale params still in scope for that render.
  const latestParams = useRef(params);
  const seenParams = useRef(params);
  if (params !== seenParams.current) {
    seenParams.current = params;
    latestParams.current = params;
  }

  const patch = useCallback((next: Record<string, string | null>) => {
    const out = new URLSearchParams(latestParams.current);
    for (const [key, value] of Object.entries(next)) {
      if (value === null) out.delete(key);
      else out.set(key, value);
    }
    latestParams.current = out;
    setParams(out, { replace: true });
  }, [setParams]);

  const setTree = useCallback((next: EditorGroup) => {
    const raw = toUrlJson(next);
    // A no-op write (the new tree serialises to exactly what the URL already holds, e.g. typing
    // "2." into storyPoints where it parses the same as "2") must not be remembered: if it were,
    // it would sit in the window forever (nothing ever changes the URL to consume it), so a later
    // navigation that happens to land on this same URL again (Back included) would be misread as
    // this hook's own echo and ignored, leaving the tree stuck on whatever it held before instead
    // of following the URL. Update the held tree (fresh ids, e.g.) and stop — nothing to patch.
    if (raw === latestParams.current.get('filter') && !latestParams.current.has('logic')) {
      setHeld((h) => ({ ...h, tree: next }));
      return;
    }
    recentWrites.current = [...recentWrites.current.slice(-(REMEMBERED_WRITES - 1)), raw ?? ''];
    setHeld((h) => ({ ...h, tree: next }));
    patch({ filter: raw, logic: null });
  }, [patch]);

  /**
   * Replaces the whole view in one navigation — how a saved view is opened. Push keeps what the
   * list showed one Back away; replace leaves nothing behind (a default view standing in for a
   * bare URL).
   *
   * The applied filter is this hook's own write, exactly like setTree's: the tree is held at once
   * and the URL's arrival is recognised as its echo. Holding it here, rather than waiting to adopt
   * it from the URL, is what makes the view win over an edit queued in the same tick (the URL may
   * not even move), and keeps a render in which the URL has not caught up yet from showing the
   * old tree. The same no-op rule as setTree applies to what is remembered.
   */
  const applyParams = useCallback((next: ListParams, options: { replace: boolean }) => {
    const current = latestParams.current;
    const unchanged = next.filter === current.get('filter') && !current.has('logic');
    if (!unchanged) {
      recentWrites.current = [...recentWrites.current.slice(-(REMEMBERED_WRITES - 1)), next.filter ?? ''];
    }
    const out = new URLSearchParams(current);
    for (const key of LIST_PARAMS) out.delete(key);
    for (const [key, value] of Object.entries(next)) {
      if (value !== null) out.set(key, value);
    }
    const tree = parseFilterParam(next.filter, null);
    setHeld((h) => ({ ...h, tree }));
    latestParams.current = out;
    setParams(out, { replace: options.replace });
  }, [setParams]);

  const setViewId = useCallback((id: string | null) => patch({ view: id }), [patch]);

  const columnsParam = params.get('cols');
  const columns = useMemo(
    () => columnsParam?.split(',').filter(Boolean) ?? DEFAULT_COLUMNS,
    [columnsParam],
  );

  return {
    tree,
    setTree,
    sortBy: params.get('sortBy') ?? DEFAULT_SORT,
    sortDescending: params.get('sortDesc') !== 'false',
    columns,

    /** The saved view the list was opened from, if any; kept through edits so "modified" can show. */
    viewId: params.get('view'),
    /** False for a bare URL — the only case in which a default view may step in. */
    hasListState: LIST_PARAMS.some((key) => params.has(key)),
    setViewId,
    applyParams,

    setSort: (by: string, descending: boolean) =>
      patch({
        sortBy: by === DEFAULT_SORT ? null : by,
        sortDesc: descending ? null : 'false',
      }),

    setColumns: (next: string[]) =>
      patch({ cols: next.join(',') === DEFAULT_COLUMNS.join(',') ? null : next.join(',') }),
  };
}
