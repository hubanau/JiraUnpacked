import { describe, expect, it } from 'vitest';
import type { ChartGroup, IssueChart } from '../../../api/issues';
import { chartFixture } from '../../../test/chartFixture';
import {
  CHART_FIELDS, chartCondition, formatCompact, groupLabel, isKeyOrdered, measureOf, sortGroups, topN,
} from './chartData';

const g = (key: string | null, issues: number, storyPoints: number): ChartGroup => ({ key, issues, storyPoints });

const chartOf = (groups: ChartGroup[], extra: Partial<IssueChart> = {}): IssueChart => ({
  ...chartFixture, groupBy: 'team', groups, ...extra,
});

describe('chart fields', () => {
  it('offers every chartable field with the list’s own words', () => {
    expect(CHART_FIELDS.map((f) => f.label)).toEqual([
      'Status', 'Category', 'Type', 'Priority', 'Assignee', 'Sprint', 'Team', 'Points',
      'Created (month)', 'Updated (month)',
    ]);
    expect(isKeyOrdered('createdAt')).toBe(true);
    expect(isKeyOrdered('storyPoints')).toBe(true);
    expect(isKeyOrdered('team')).toBe(false);
  });
});

describe('sortGroups', () => {
  it('ranks by the chosen measure, ties by name, (Empty) last', () => {
    const groups = [g('b', 1, 5), g(null, 1, 9), g('a', 3, 5), g('c', 1, 1)];

    expect(sortGroups(groups, 'team', 'issues').map((x) => x.key)).toEqual(['a', 'b', 'c', null]);
    expect(sortGroups(groups, 'team', 'points').map((x) => x.key)).toEqual([null, 'a', 'b', 'c']);
  });

  it('keeps a key-ordered field in the server’s order', () => {
    const months = [g('2026-01', 1, 9), g('2026-02', 5, 0)];

    expect(sortGroups(months, 'createdAt', 'points')).toEqual(months);
  });

  it('reads either measure off a group', () => {
    expect(measureOf(g('a', 3, 7.5), 'issues')).toBe(3);
    expect(measureOf(g('a', 3, 7.5), 'points')).toBe(7.5);
  });
});

describe('topN', () => {
  it('folds everything past the top N, and the server’s own Other, into one Other slice', () => {
    const groups = Array.from({ length: 12 }, (_, i) => g(`t${i}`, 12 - i, 0));
    const chart = chartOf(groups, { truncated: true, other: { groups: 40, issues: 7, storyPoints: 0 } });

    const slices = topN(chart, 'issues', 10);

    expect(slices).toHaveLength(11);
    expect(slices[0]).toMatchObject({ key: 't0', label: 't0', value: 12 });
    expect(slices[10]).toEqual({ key: null, label: 'Other', value: 2 + 1 + 7, group: null });
  });

  it('has no Other slice when nothing is left over', () => {
    expect(topN(chartFixture, 'issues', 10).map((s) => s.label))
      .toEqual(['To Do', 'In Progress', 'Done', '(Empty)']);
  });

  it('re-ranks when the measure changes', () => {
    expect(topN(chartFixture, 'points', 10).map((s) => s.label))
      .toEqual(['In Progress', 'To Do', 'Done', '(Empty)']);
  });

  it('keeps a timeline in order, folding only its oldest part when it is very long', () => {
    const months = Array.from({ length: 32 }, (_, i) => g(`20${String(24 + Math.floor(i / 12))}-${String((i % 12) + 1).padStart(2, '0')}`, 1, 0));
    const slices = topN(chartOf(months, { groupBy: 'createdAt' }), 'issues', 10);

    expect(slices).toHaveLength(31);
    expect(slices[0]).toMatchObject({ label: 'Earlier', value: 2, group: null });
    expect(slices[1].key).toBe('2024-03');
    expect(slices[30].key).toBe('2026-08');
  });
});

describe('topN — a short timeline', () => {
  it('keeps only the newest months when told to, folding the rest into Earlier', () => {
    const months = Array.from({ length: 12 }, (_, i) => g(`2026-${String(i + 1).padStart(2, '0')}`, i + 1, 0));
    const slices = topN(chartOf(months, { groupBy: 'createdAt' }), 'issues', 8, 8);

    expect(slices.map((s) => s.label)).toEqual([
      'Earlier', 'May 2026', 'Jun 2026', 'Jul 2026', 'Aug 2026', 'Sep 2026', 'Oct 2026', 'Nov 2026', 'Dec 2026',
    ]);
    expect(slices[0].value).toBe(1 + 2 + 3 + 4);
  });

  it('calls the folded part of a Points scale Other, not Earlier', () => {
    const points = Array.from({ length: 4 }, (_, i) => g(String(i + 1), 1, i + 1));

    expect(topN(chartOf(points, { groupBy: 'storyPoints' }), 'issues', 2, 2)[0].label).toBe('Other');
  });
});

describe('groupLabel', () => {
  it('names the empty group the way the field reads', () => {
    expect(groupLabel('assignee', null)).toBe('Unassigned');
    expect(groupLabel('team', null)).toBe('(Empty)');
  });

  it('writes a month the way a person reads it', () => {
    expect(groupLabel('createdAt', '2026-09')).toBe('Sep 2026');
    expect(groupLabel('updatedAt', '2024-01')).toBe('Jan 2024');
  });

  it('leaves names and points as they are', () => {
    expect(groupLabel('storyPoints', '0.5')).toBe('0.5');
    expect(groupLabel('status', 'To Do')).toBe('To Do');
  });
});

describe('chartCondition', () => {
  it('filters to a name, a number or empty', () => {
    expect(chartCondition('team', 'mobile')).toEqual({ field: 'team', op: 'Eq', value: 'mobile' });
    expect(chartCondition('storyPoints', '5')).toEqual({ field: 'storyPoints', op: 'Eq', value: 5 });
    expect(chartCondition('assignee', null)).toEqual({ field: 'assignee', op: 'IsNull' });
  });

  it('filters a month to its first through last day, inclusive', () => {
    expect(chartCondition('createdAt', '2024-02'))
      .toEqual({ field: 'createdAt', op: 'Between', values: ['2024-02-01', '2024-02-29'] });
    expect(chartCondition('updatedAt', '2026-09'))
      .toEqual({ field: 'updatedAt', op: 'Between', values: ['2026-09-01', '2026-09-30'] });
  });
});

describe('formatCompact', () => {
  it.each([
    [98012, '98K'], [10596, '11K'], [5284, '5.3K'], [41230.5, '41K'], [950, '950'], [12.5, '12.5'],
    [0, '0'], [1000, '1K'], [2_450_000, '2.5M'],
  ])('%s → %s', (n, expected) => {
    expect(formatCompact(n)).toBe(expected);
  });
});
