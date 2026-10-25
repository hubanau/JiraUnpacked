import type { ChartGroup, ChartGroupBy, FilterCondition, IssueChart } from '../../../api/issues';
import type { ChartMeasure } from '../useListState';

/** What the chart can group by, in the list's own words (the column headers), in the order offered. */
export const CHART_FIELDS: { id: ChartGroupBy; label: string }[] = [
  { id: 'status', label: 'Status' },
  { id: 'statusCategory', label: 'Category' },
  { id: 'issueType', label: 'Type' },
  { id: 'priority', label: 'Priority' },
  { id: 'assignee', label: 'Assignee' },
  { id: 'sprint', label: 'Sprint' },
  { id: 'team', label: 'Team' },
  { id: 'storyPoints', label: 'Points' },
  { id: 'createdAt', label: 'Created (month)' },
  { id: 'updatedAt', label: 'Updated (month)' },
];

/** A scale rather than a ranking: Points and months read in key order, never re-ranked by size. */
export const isKeyOrdered = (groupBy: ChartGroupBy): boolean =>
  groupBy === 'storyPoints' || groupBy === 'createdAt' || groupBy === 'updatedAt';

export const measureOf = (g: { issues: number; storyPoints: number }, measure: ChartMeasure): number =>
  measure === 'points' ? g.storyPoints : g.issues;

/**
 * The groups in the order to show them. The server ranks by issues; switching to points re-ranks
 * here, which is why the measure never refetches. Ties by name; (Empty) last.
 */
export function sortGroups(groups: ChartGroup[], groupBy: ChartGroupBy, measure: ChartMeasure): ChartGroup[] {
  if (isKeyOrdered(groupBy)) return groups;
  return [...groups].sort((a, b) =>
    measureOf(b, measure) - measureOf(a, measure)
    || Number(a.key === null) - Number(b.key === null)
    || (a.key ?? '').localeCompare(b.key ?? ''));
}

/** One bar, slice or legend entry. `group` is null for Other, which is not a value one can filter to. */
export interface ChartSlice {
  key: string | null;
  label: string;
  value: number;
  group: ChartGroup | null;
}

export const OTHER_LABEL = 'Other';

const otherSlice = (value: number): ChartSlice => ({ key: null, label: OTHER_LABEL, value, group: null });

/**
 * At most `n` ranked groups plus one Other holding the rest and anything the server already left
 * out (so the slices always add up to the total). A timeline is not ranked: it keeps its order
 * and every month up to 3n, folding only its oldest part into a leading Other beyond that.
 */
export function topN(chart: IssueChart, measure: ChartMeasure, n: number): ChartSlice[] {
  const toSlice = (group: ChartGroup): ChartSlice => ({
    key: group.key, label: groupLabel(chart.groupBy, group.key), value: measureOf(group, measure), group,
  });
  const serverOther = chart.other ? measureOf(chart.other, measure) : 0;
  const sum = (groups: ChartGroup[]) => groups.reduce((total, g) => total + measureOf(g, measure), 0);

  if (isKeyOrdered(chart.groupBy)) {
    const cap = n * 3;
    const cut = Math.max(0, chart.groups.length - cap);
    const rest = sum(chart.groups.slice(0, cut)) + serverOther;
    const shown = chart.groups.slice(cut).map(toSlice);
    return rest > 0 ? [otherSlice(rest), ...shown] : shown;
  }

  const sorted = sortGroups(chart.groups, chart.groupBy, measure);
  const rest = sum(sorted.slice(n)) + serverOther;
  const shown = sorted.slice(0, n).map(toSlice);
  return rest > 0 ? [...shown, otherSlice(rest)] : shown;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** How a group's key reads: (Empty) says what is missing; a "yyyy-MM" month reads "Sep 2026". */
export function groupLabel(groupBy: ChartGroupBy, key: string | null): string {
  if (key === null) return groupBy === 'assignee' ? 'Unassigned' : '(Empty)';
  if (groupBy === 'createdAt' || groupBy === 'updatedAt') {
    const [year, month] = key.split('-');
    return `${MONTHS[Number(month) - 1]} ${year}`;
  }
  return key;
}

/**
 * The condition that narrows the list to one group. A month is its first through last day as
 * date-only strings, which the server reads as UTC with both ends inclusive — the same UTC month
 * the server bucketed by, so the drilled-down list matches the bar it came from.
 */
export function chartCondition(groupBy: ChartGroupBy, key: string | null): FilterCondition {
  if (key === null) return { field: groupBy, op: 'IsNull' };
  if (groupBy === 'storyPoints') return { field: groupBy, op: 'Eq', value: Number(key) };
  if (groupBy === 'createdAt' || groupBy === 'updatedAt') {
    const [year, month] = key.split('-').map(Number);
    const lastDay = new Date(Date.UTC(year, month, 0)).getUTCDate();
    return { field: groupBy, op: 'Between', values: [`${key}-01`, `${key}-${String(lastDay).padStart(2, '0')}`] };
  }
  return { field: groupBy, op: 'Eq', value: key };
}

/** 98012 → "98K", 5284 → "5.3K", 950 → "950": two significant-ish digits, for the donut's centre. */
export function formatCompact(n: number): string {
  const trim = (x: number) => String(Math.round(x * 10) / 10);
  const scaled = (x: number, suffix: string) => (x < 10 ? trim(x) : String(Math.round(x))) + suffix;
  if (Math.abs(n) >= 1_000_000) return scaled(n / 1_000_000, 'M');
  if (Math.abs(n) >= 1_000) return scaled(n / 1_000, 'K');
  return trim(n);
}
