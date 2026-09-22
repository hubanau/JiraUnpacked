import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { FilterCondition, FilterLogic } from '../../api/issues';

export const DEFAULT_SORT = 'updatedAt';

/** The columns the table has always shown. Ids match the filter and sort vocabulary. */
export const DEFAULT_COLUMNS = [
  'jiraKey', 'summary', 'status', 'issueType', 'assignee', 'sprint', 'storyPoints', 'updatedAt',
];

/**
 * The list's whole view — filter, logic, sort, columns — read from and written to the query
 * string.
 *
 * The URL rather than localStorage for two reasons, in order. Only ju.username and ju.theme may
 * be persisted, and this is not a reason to widen that. And a URL is shareable: "PLAT bugs this
 * sprint by points" becomes a link, which is most of the value of building a filter at all. It
 * also survives reload and Back, which useState does not.
 *
 * A default is written as an ABSENT param, never an explicit one, so an untouched list has a
 * clean URL and a shared link carries only what was deliberately chosen.
 */
export function useListState() {
  const [params, setParams] = useSearchParams();

  // Keyed on the raw string, not the params object. IssueList resets paging in an effect keyed
  // on conditions, so a fresh array identity each render would make that effect fire forever.
  const rawFilter = params.get('filter');
  const conditions = useMemo<FilterCondition[]>(() => {
    if (!rawFilter) return [];
    try {
      const parsed: unknown = JSON.parse(rawFilter);
      return Array.isArray(parsed) ? (parsed as FilterCondition[]) : [];
    } catch {
      // A URL is user-editable and chat clients truncate them. A broken filter degrades to
      // "no filter"; it must never blank the page.
      return [];
    }
  }, [rawFilter]);

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

  const columnsParam = params.get('cols');
  const columns = useMemo(
    () => columnsParam?.split(',').filter(Boolean) ?? DEFAULT_COLUMNS,
    [columnsParam],
  );

  return {
    conditions,
    logic: (params.get('logic') === 'Or' ? 'Or' : 'And') as FilterLogic,
    sortBy: params.get('sortBy') ?? DEFAULT_SORT,
    sortDescending: params.get('sortDesc') !== 'false',
    columns,

    setConditions: (next: FilterCondition[]) =>
      patch({ filter: next.length === 0 ? null : JSON.stringify(next) }),

    setLogic: (next: FilterLogic) =>
      patch({ logic: next === 'Or' ? 'Or' : null }),

    setSort: (by: string, descending: boolean) =>
      patch({
        sortBy: by === DEFAULT_SORT ? null : by,
        sortDesc: descending ? null : 'false',
      }),

    setColumns: (next: string[]) =>
      patch({ cols: next.join(',') === DEFAULT_COLUMNS.join(',') ? null : next.join(',') }),
  };
}
