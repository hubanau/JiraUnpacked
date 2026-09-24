import { describe, expect, it } from 'vitest';
import type { SavedView } from '../../api/views';
import { parseFilterParam, type EditorCondition } from './filterTree';
import { DEFAULT_COLUMNS } from './useListState';
import {
  isModified, resaveRequest, saveRequest, shownColumns, sortViews, viewParams, withView, withoutView,
  type ListSnapshot,
} from './viewState';

const view = (overrides: Partial<SavedView> = {}): SavedView => ({
  id: 'v1', name: 'My backlog', conditions: null, filter: null, columns: null, sortBy: null,
  sortDescending: null, isDefault: false, createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-01T00:00:00Z',
  ...overrides,
});

/** A group exactly as the API serialises it: every absent member an explicit null. */
const SERVER_FILTER = {
  logic: 'And',
  conditions: [
    { field: 'summary', op: 'Contains', value: 'login', values: null, disabled: null },
    { field: 'sprint', op: 'IsNull', value: null, values: null, disabled: true },
  ],
  groups: null,
  disabled: null,
} as unknown as SavedView['filter'];

const BACKLOG = view({
  filter: SERVER_FILTER, columns: ['jiraKey', 'summary', 'priority'], sortBy: 'priority', sortDescending: false,
});

/** The list right after opening `v`: what useListState reads back from viewParams(v). */
const opened = (v: SavedView): ListSnapshot => {
  const p = viewParams(v);
  return {
    tree: parseFilterParam(p.filter, null),
    sortBy: p.sortBy ?? 'updatedAt',
    sortDescending: p.sortDesc !== 'false',
    columns: p.cols?.split(',') ?? DEFAULT_COLUMNS,
  };
};

const withConditions = (s: ListSnapshot, conditions: EditorCondition[]): ListSnapshot =>
  ({ ...s, tree: { ...s.tree, conditions } });

describe('viewParams', () => {
  it('opens a view the server returned with explicit nulls as the clean URL useListState writes', () => {
    const p = viewParams(BACKLOG);

    expect(JSON.parse(p.filter!)).toEqual({
      logic: 'And',
      conditions: [
        { field: 'summary', op: 'Contains', value: 'login' },
        { field: 'sprint', op: 'IsNull', disabled: true },
      ],
    });
    expect(p).toMatchObject({ sortBy: 'priority', sortDesc: 'false', cols: 'jiraKey,summary,priority', view: 'v1' });
  });

  it('writes what a view leaves out — or spells out as the default — as absent', () => {
    const blank = { filter: null, sortBy: null, sortDesc: null, cols: null, view: 'v1' };
    expect(viewParams(view())).toEqual(blank);
    expect(viewParams(view({ sortBy: 'updatedAt', sortDescending: true, columns: [...DEFAULT_COLUMNS] }))).toEqual(blank);
    // An empty column list would show a table with no columns: it is treated as "not stored".
    expect(viewParams(view({ columns: [] }))).toEqual(blank);
  });

  it('opens a flat conditions list, as older clients stored it, as an All group', () => {
    const p = viewParams(view({ conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] }));

    expect(JSON.parse(p.filter!)).toEqual({ logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] });
  });

  it('writes columns in table order, whatever order they were stored in', () => {
    expect(viewParams(view({ columns: ['priority', 'jiraKey'] })).cols).toBe('jiraKey,priority');
  });
});

describe('isModified', () => {
  it('is false right after opening a view, true after an edit, false again once it is undone', () => {
    const now = opened(BACKLOG);
    const [summary, sprint] = now.tree.conditions;

    expect(isModified(BACKLOG, now)).toBe(false);
    expect(isModified(BACKLOG, withConditions(now, [{ ...summary, value: 'logout' }, sprint]))).toBe(true);
    expect(isModified(BACKLOG, withConditions(now, [{ ...summary, value: 'login' }, sprint]))).toBe(false);
  });

  it('counts sort and columns, but not the order the columns were listed in', () => {
    const now = opened(BACKLOG);

    expect(isModified(BACKLOG, { ...now, sortDescending: true })).toBe(true);
    expect(isModified(BACKLOG, { ...now, sortBy: 'summary' })).toBe(true);
    expect(isModified(BACKLOG, { ...now, columns: ['jiraKey', 'summary'] })).toBe(true);
    expect(isModified(BACKLOG, { ...now, columns: ['priority', 'summary', 'jiraKey'] })).toBe(false);
  });

  it('ignores a row that is still being filled in — Save would not write it', () => {
    const now = opened(BACKLOG);
    const blank: EditorCondition = { id: 'x', field: 'status', op: 'Eq', value: '' };

    expect(isModified(BACKLOG, withConditions(now, [...now.tree.conditions, blank]))).toBe(false);
  });

  it('counts switching a row off, since a view stores disabled parts', () => {
    const now = opened(BACKLOG);
    const [summary, sprint] = now.tree.conditions;

    expect(isModified(BACKLOG, withConditions(now, [{ ...summary, disabled: true }, sprint]))).toBe(true);
  });

  it('treats a view that stores nothing as the default list', () => {
    const plain: ListSnapshot = {
      tree: parseFilterParam(null, null), sortBy: 'updatedAt', sortDescending: true, columns: DEFAULT_COLUMNS,
    };

    expect(isModified(view(), plain)).toBe(false);
    expect(isModified(view(), { ...plain, sortBy: 'summary' })).toBe(true);
  });
});

describe('saveRequest', () => {
  it('stores the list as shown: savable filter, columns in table order, sort always', () => {
    const now = opened(BACKLOG);

    expect(saveRequest('Mine', { ...now, columns: ['priority', 'jiraKey', 'nope'] })).toEqual({
      name: 'Mine',
      filter: {
        logic: 'And',
        conditions: [
          { field: 'summary', op: 'Contains', value: 'login' },
          { field: 'sprint', op: 'IsNull', disabled: true },
        ],
      },
      columns: ['jiraKey', 'priority'],
      sortBy: 'priority',
      sortDescending: false,
    });
  });

  it('leaves columns out when none of the listed ids is a column the table knows', () => {
    const now = { ...opened(BACKLOG), columns: ['bogus'] };

    expect(saveRequest('Mine', now)).not.toHaveProperty('columns');
    // An empty list is the default list, on both sides of the comparison.
    expect(isModified(view(), { ...opened(view()), columns: ['bogus'] })).toBe(false);
  });

  it('sends no filter for an empty tree', () => {
    expect(saveRequest('All', opened(view()))).toEqual({
      name: 'All', columns: DEFAULT_COLUMNS, sortBy: 'updatedAt', sortDescending: true,
    });
  });
});

describe('resaveRequest', () => {
  it('keeps exactly what the view stores, cleaned of the server’s nulls, under a new name', () => {
    expect(resaveRequest(BACKLOG, { name: 'Login bugs' })).toEqual({
      name: 'Login bugs',
      filter: {
        logic: 'And',
        conditions: [
          { field: 'summary', op: 'Contains', value: 'login' },
          { field: 'sprint', op: 'IsNull', disabled: true },
        ],
      },
      columns: ['jiraKey', 'summary', 'priority'],
      sortBy: 'priority',
      sortDescending: false,
    });
  });

  it('does not send back an empty column list', () => {
    expect(resaveRequest(view({ columns: [] }), { name: 'Renamed' })).toEqual({ name: 'Renamed' });
  });

  it('moves only the default flag, storing nothing the view did not store', () => {
    expect(resaveRequest(view(), { isDefault: true })).toEqual({ name: 'My backlog', isDefault: true });
  });
});

describe('the cached list', () => {
  const a = view({ id: 'a', name: 'apple', isDefault: true });
  const b = view({ id: 'b', name: 'Banana' });

  it('orders default first, then by name ignoring case', () => {
    expect(sortViews([b, view({ id: 'c', name: 'cherry' }), a]).map((v) => v.id)).toEqual(['a', 'b', 'c']);
  });

  it('replaces a saved view in place and leaves it the only default', () => {
    const next = withView([a, b], { ...b, name: 'Blueberry', isDefault: true });

    expect(next.map((v) => [v.name, v.isDefault])).toEqual([['Blueberry', true], ['apple', false]]);
  });

  it('adds a new view and drops a deleted one', () => {
    expect(withView([a], b).map((v) => v.id)).toEqual(['a', 'b']);
    expect(withoutView([a, b], 'a').map((v) => v.id)).toEqual(['b']);
  });
});

describe('shownColumns', () => {
  it('is table order, known ids only, each once', () => {
    expect(shownColumns(['priority', 'jiraKey', 'nope', 'jiraKey'])).toEqual(['jiraKey', 'priority']);
  });
});
