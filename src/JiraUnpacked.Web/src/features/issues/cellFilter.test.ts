import { describe, expect, it } from 'vitest';
import type { ConditionValue, FilterLogic, FilterOperator } from '../../api/issues';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { addCellCondition, cellCondition, cellFilterHint, treeDepth } from './cellFilter';
import { newId, pruneTree, type EditorCondition, type EditorGroup } from './filterTree';

const cond = (field: string, op: FilterOperator, value?: ConditionValue, disabled = false): EditorCondition => ({
  id: newId(), field, op, ...(value !== undefined ? { value } : {}), ...(disabled ? { disabled: true } : {}),
});

const group = (logic: FilterLogic, conditions: EditorCondition[], groups: EditorGroup[] = [], disabled = false): EditorGroup => ({
  id: newId(), logic, conditions, groups, ...(disabled ? { disabled: true } : {}),
});

const STATUS_DONE = { field: 'status', op: 'Eq' as const, value: 'Done' };

describe('cellCondition', () => {
  it('is / is not a value, and is empty / is not empty for an empty cell', () => {
    expect(cellCondition('status', 'Done', 'include')).toEqual({ field: 'status', op: 'Eq', value: 'Done' });
    expect(cellCondition('status', 'Done', 'exclude')).toEqual({ field: 'status', op: 'Neq', value: 'Done' });
    expect(cellCondition('storyPoints', 3, 'include')).toEqual({ field: 'storyPoints', op: 'Eq', value: 3 });
    expect(cellCondition('assignee', null, 'include')).toEqual({ field: 'assignee', op: 'IsNull' });
    expect(cellCondition('assignee', null, 'exclude')).toEqual({ field: 'assignee', op: 'IsNotNull' });
  });
});

describe('addCellCondition', () => {
  it('appends to an All root', () => {
    const next = addCellCondition(group('And', [cond('priority', 'Eq', 'High')]), STATUS_DONE);
    expect(pruneTree(next)).toEqual({
      logic: 'And', conditions: [{ field: 'priority', op: 'Eq', value: 'High' }, STATUS_DONE],
    });
  });

  it('wraps an Any root that holds something, so the click narrows', () => {
    const root = group('Or', [cond('priority', 'Eq', 'High'), cond('priority', 'Eq', 'Low')]);
    const next = addCellCondition(root, STATUS_DONE);
    expect(next.groups[0]).toBe(root);
    expect(pruneTree(next)).toEqual({
      logic: 'And',
      conditions: [STATUS_DONE],
      groups: [{ logic: 'Or', conditions: [{ field: 'priority', op: 'Eq', value: 'High' }, { field: 'priority', op: 'Eq', value: 'Low' }] }],
    });
  });

  it('adds straight to an Any root whose parts are all switched off', () => {
    const next = addCellCondition(group('Or', [cond('priority', 'Eq', 'High', true)]), STATUS_DONE);
    expect(next.logic).toBe('Or');
    expect(next.conditions).toHaveLength(2);
  });

  it('wraps a switched-off root, which stays off inside', () => {
    const root = group('And', [cond('priority', 'Eq', 'High')], [], true);
    const next = addCellCondition(root, STATUS_DONE);
    expect(next.disabled).toBeUndefined();
    expect(next.groups[0]).toBe(root);
    expect(pruneTree(next)).toEqual({ logic: 'And', conditions: [STATUS_DONE] });
  });

  it('returns the same tree for a condition already switched on in the root, not for one switched off', () => {
    const on = group('And', [cond('status', 'Eq', 'Done')]);
    expect(addCellCondition(on, STATUS_DONE)).toBe(on);
    const off = group('And', [cond('status', 'Eq', 'Done', true)]);
    expect(addCellCondition(off, STATUS_DONE).conditions).toHaveLength(2);
  });
});

describe('cellFilterHint', () => {
  it('is undefined while the condition fits', () => {
    expect(cellFilterHint(group('And', [cond('status', 'Eq', 'Done')]), FILTER_CATALOG)).toBeUndefined();
  });

  it('names the condition limit', () => {
    const full = group('And', Array.from({ length: 20 }, (_, i) => cond('summary', 'Contains', `w${i}`)));
    expect(cellFilterHint(full, FILTER_CATALOG)).toBe('Limit: 20 conditions');
  });

  it('names the depth and group limits only when the root must be wrapped', () => {
    const deep = group('Or', [cond('status', 'Eq', 'Done')], [
      group('And', [cond('priority', 'Eq', 'High')], [group('And', [cond('team', 'IsNull')])]),
    ]);
    expect(treeDepth(deep)).toBe(3);
    expect(cellFilterHint(deep, FILTER_CATALOG)).toBe('Limit: 3 levels of groups');
    expect(cellFilterHint({ ...deep, logic: 'And' }, FILTER_CATALOG)).toBeUndefined();

    const wide = group('Or', [], Array.from({ length: 9 }, () => group('And', [cond('status', 'Eq', 'Done')])));
    expect(cellFilterHint(wide, FILTER_CATALOG)).toBe('Limit: 10 groups');
  });
});
