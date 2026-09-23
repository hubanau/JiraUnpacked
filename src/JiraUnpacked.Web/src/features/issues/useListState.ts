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
  // of "what the URL should now contain": refreshed from the committed params on every render,
  // and advanced by every patch, so the next patch in the same tick builds on top of the one
  // just made rather than on a params snapshot that has not caught up yet.
  const latestParams = useRef(params);
  latestParams.current = params;

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
    recentWrites.current = [...recentWrites.current.slice(-(REMEMBERED_WRITES - 1)), raw ?? ''];
    setHeld((h) => ({ ...h, tree: next }));
    patch({ filter: raw, logic: null });
  }, [patch]);

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

    setSort: (by: string, descending: boolean) =>
      patch({
        sortBy: by === DEFAULT_SORT ? null : by,
        sortDesc: descending ? null : 'false',
      }),

    setColumns: (next: string[]) =>
      patch({ cols: next.join(',') === DEFAULT_COLUMNS.join(',') ? null : next.join(',') }),
  };
}
