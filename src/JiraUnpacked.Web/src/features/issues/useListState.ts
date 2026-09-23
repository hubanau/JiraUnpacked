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
    const echo = rawLogic === null && recentWrites.current.includes(rawFilter ?? '');
    if (!echo) tree = parseFilterParam(rawFilter, rawLogic);
    setHeld({ raw: rawFilter, logic: rawLogic, tree });
  }

  const patch = useCallback((next: Record<string, string | null>) => {
    setParams((prev) => {
      const out = new URLSearchParams(prev);
      for (const [key, value] of Object.entries(next)) {
        if (value === null) out.delete(key);
        else out.set(key, value);
      }
      return out;
    }, { replace: true });
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
