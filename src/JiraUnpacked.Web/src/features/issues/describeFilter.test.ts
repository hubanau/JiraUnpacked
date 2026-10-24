import { describe, expect, it } from 'vitest';
import type { FilterCondition, FilterGroup } from '../../api/issues';
import { describeFilter, formatDate, listText } from './describeFilter';
import { emptyRoot, newId, pruneTree, type EditorGroup } from './filterTree';

const all = (...conditions: FilterCondition[]): FilterGroup => ({ logic: 'And', conditions });
const one = (c: FilterCondition) => describeFilter(all(c));

describe('describeFilter', () => {
  it('reads no filter as All issues', () => {
    expect(describeFilter(undefined)).toBe('All issues');
    expect(describeFilter(pruneTree(emptyRoot()))).toBe('All issues');
  });

  it('names fields by their column headers and operators by the editor’s words', () => {
    expect(one({ field: 'status', op: 'Eq', value: 'Done' })).toBe('Status is Done');
    expect(one({ field: 'statusCategory', op: 'Neq', value: 'Done' })).toBe('Category is not Done');
    expect(one({ field: 'issueType', op: 'In', values: ['Bug', 'Story'] })).toBe('Type is any of Bug, Story');
    expect(one({ field: 'priority', op: 'NotIn', values: ['Low'] })).toBe('Priority is none of Low');
    expect(one({ field: 'storyPoints', op: 'Gte', value: 3 })).toBe('Points ≥ 3');
    expect(one({ field: 'assignee', op: 'IsNull' })).toBe('Assignee is empty');
    expect(one({ field: 'team', op: 'IsNotNull' })).toBe('Team is not empty');
    expect(one({ field: 'summary', op: 'Contains', value: 'login' })).toBe('Summary contains "login"');
    expect(one({ field: 'summary', op: 'NotContains', value: 'flaky' })).toBe('Summary does not contain "flaky"');
    expect(one({ field: 'jiraKey', op: 'StartsWith', value: 'PLAT-1' })).toBe('Key starts with "PLAT-1"');
  });

  it('reads (Empty) in a list as empty and smart sprints by their label', () => {
    expect(one({ field: 'sprint', op: 'In', values: [{ smart: 'active' }, null, 'Sprint 7'] }))
      .toBe('Sprint is any of Active sprint, empty, Sprint 7');
    expect(one({ field: 'sprint', op: 'Eq', value: { smart: 'open' } })).toBe('Sprint is Open sprints (active + future)');
  });

  it('lists up to five values and truncates more', () => {
    expect(listText(['a', 'b', 'c', 'd', 'e'])).toBe('a, b, c, d, e');
    expect(one({ field: 'status', op: 'In', values: ['Backlog', 'To Do', 'A', 'B', 'C', 'D'] }))
      .toBe('Status is any of Backlog, To Do, … and 4 more');
  });

  it('reads dates, ranges and relative dates', () => {
    expect(formatDate('2026-09-01')).toBe('1 Sep 2026');
    expect(formatDate('2026-09-01T10:30:00Z')).toBe('1 Sep 2026 10:30');
    expect(formatDate('not a date')).toBe('not a date');
    expect(one({ field: 'createdAt', op: 'Lt', value: '2026-09-01' })).toBe('Created < 1 Sep 2026');
    expect(one({ field: 'createdAt', op: 'Between', values: ['2026-09-01', '2026-09-30'] }))
      .toBe('Created between 1 Sep 2026 and 30 Sep 2026');
    expect(one({ field: 'createdAt', op: 'Between', values: ['2026-09-01', null] })).toBe('Created ≥ 1 Sep 2026');
    expect(one({ field: 'storyPoints', op: 'Between', values: [null, 5] })).toBe('Points ≤ 5');
    expect(one({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } })).toBe('Updated in the last 14 days');
    expect(one({ field: 'updatedAt', op: 'InLast', value: { amount: 1, unit: 'weeks' } })).toBe('Updated in the last 1 week');
    expect(one({ field: 'updatedAt', op: 'NotInLast', value: { amount: 3, unit: 'months' } })).toBe('Updated more than 3 months ago');
    expect(one({ field: 'createdAt', op: 'InPeriod', value: { period: 'thisWeek', timeZone: 'Europe/Warsaw' } }))
      .toBe('Created in period this week (Europe/Warsaw)');
    expect(one({ field: 'createdAt', op: 'InPeriod', value: { period: 'lastMonth' } })).toBe('Created in period last month');
  });

  it('reads history conditions', () => {
    expect(one({ field: 'history', op: 'Was', value: { historyField: 'status', values: ['Blocked'] } }))
      .toBe('status was Blocked');
    expect(one({ field: 'history', op: 'Was', value: { historyField: 'status', values: ['Blocked', null] } }))
      .toBe('status was any of Blocked, empty');
    expect(one({
      field: 'history', op: 'Changed',
      value: {
        historyField: 'Sprint', from: ['A'], to: ['B'], by: ['Alice'],
        during: { kind: 'between', from: '2026-09-01', to: '2026-09-30' },
      },
    })).toBe('Sprint changed from A to B by Alice between 1 Sep 2026 and 30 Sep 2026');
    expect(one({ field: 'history', op: 'Changed', value: { historyField: 'status' } })).toBe('status changed');
    expect(one({
      field: 'history', op: 'Changed',
      value: { historyField: 'status', during: { kind: 'inPeriod', period: 'lastMonth', timeZone: 'UTC' } },
    })).toBe('status changed in period last month (UTC)');
    expect(one({
      field: 'history', op: 'Changed',
      value: { historyField: 'status', during: { kind: 'between', from: '2026-09-01', to: null } },
    })).toBe('status changed since 1 Sep 2026');
    expect(one({
      field: 'history', op: 'ChangeCount',
      value: { historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 30, unit: 'days' } },
    })).toBe('Sprint changed ≥ 3 times in the last 30 days');
    expect(one({ field: 'history', op: 'ChangeCount', value: { historyField: 'Sprint', compare: 'Eq', count: 1 } }))
      .toBe('Sprint changed = 1 time');
  });

  it('joins with the group’s logic and parenthesises nested groups of more than one part', () => {
    const filter: FilterGroup = {
      logic: 'And',
      conditions: [
        { field: 'status', op: 'In', values: ['Backlog', 'To Do'] },
        {
          field: 'history', op: 'ChangeCount',
          value: { historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 30, unit: 'days' } },
        },
      ],
      groups: [{
        logic: 'Or',
        conditions: [{ field: 'assignee', op: 'Eq', value: 'Alice' }, { field: 'priority', op: 'NotIn', values: ['Low'] }],
      }],
    };
    expect(describeFilter(filter)).toBe(
      'Status is any of Backlog, To Do and Sprint changed ≥ 3 times in the last 30 days'
      + ' and (Assignee is Alice or Priority is none of Low)');
    expect(describeFilter({ logic: 'Or', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }, { field: 'status', op: 'IsNull' }] }))
      .toBe('Status is Done or Status is empty');
    expect(describeFilter({ logic: 'And', groups: [{ logic: 'Or', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] }] }))
      .toBe('Status is Done');
  });

  it('reads what is sent: disabled and unfinished parts are not in it', () => {
    const tree: EditorGroup = {
      id: newId(),
      logic: 'And',
      conditions: [
        { id: newId(), field: 'status', op: 'Eq', value: 'Done' },
        { id: newId(), field: 'priority', op: 'Eq', value: 'Low', disabled: true },
        { id: newId(), field: 'assignee', op: 'Eq', value: '' },
      ],
      groups: [{ id: newId(), logic: 'Or', conditions: [{ id: newId(), field: 'team', op: 'IsNull' }], groups: [], disabled: true }],
    };
    expect(describeFilter(pruneTree(tree))).toBe('Status is Done');
  });
});
