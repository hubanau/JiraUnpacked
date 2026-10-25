import { useState } from 'react';
import type { ChartGroup } from '../../../api/issues';
import { Table, TBody, TD, TH, THead, TR } from '../../../ui';
import { formatValue, groupLabel, measureOf, sortGroups, totalOf, type ChartViewProps } from './chartData';

type Column = 'value' | 'issues' | 'points' | 'share';

const HEADERS: { id: Column; label: string; sortLabel: string }[] = [
  { id: 'value', label: 'Value', sortLabel: 'Value' },
  { id: 'issues', label: 'Issues', sortLabel: 'Issues' },
  { id: 'points', label: 'Story points', sortLabel: 'Story points' },
  { id: 'share', label: '%', sortLabel: 'share' },
];

/**
 * Every group the server returned, with both measures — the chart as numbers, and the only view
 * not cut to a top N. Sorting is local and starts from the chart's own order.
 */
export function ChartTable({ chart, measure, onPick, pickHint }: ChartViewProps) {
  const [sort, setSort] = useState<{ col: Column; desc: boolean } | null>(null);
  const total = totalOf(chart, measure);
  const share = (g: { issues: number; storyPoints: number }) =>
    total > 0 ? `${Math.round((measureOf(g, measure) / total) * 100)}%` : '—';
  const label = (g: ChartGroup) => groupLabel(chart.groupBy, g.key);

  let rows = sortGroups(chart.groups, chart.groupBy, measure);
  if (sort) {
    const by: Record<Column, (a: ChartGroup, b: ChartGroup) => number> = {
      value: (a, b) => label(a).localeCompare(label(b)),
      issues: (a, b) => a.issues - b.issues,
      points: (a, b) => a.storyPoints - b.storyPoints,
      share: (a, b) => measureOf(a, measure) - measureOf(b, measure),
    };
    rows = [...rows].sort((a, b) => (sort.desc ? -1 : 1) * by[sort.col](a, b));
  }

  // A new column starts with what people look for first: names A→Z, numbers largest first.
  const toggle = (col: Column) => setSort((s) =>
    s?.col === col ? { col, desc: !s.desc } : { col, desc: col !== 'value' });

  return (
    <Table>
      <THead>
        <TR>
          {HEADERS.map((h) => (
            <TH
              key={h.id}
              className={h.id === 'value' ? '' : 'text-right'}
              aria-sort={sort?.col === h.id ? (sort.desc ? 'descending' : 'ascending') : 'none'}
            >
              <button type="button" aria-label={`Sort by ${h.sortLabel}`} onClick={() => toggle(h.id)}>
                {h.label}
                {sort?.col === h.id && <span aria-hidden="true">{sort.desc ? ' ↓' : ' ↑'}</span>}
              </button>
            </TH>
          ))}
        </TR>
      </THead>
      <TBody>
        {rows.map((g) => (
          <TR key={`g:${g.key}`}>
            <TD>
              <button
                type="button"
                aria-label={`Filter to ${label(g)}`}
                title={pickHint}
                disabled={pickHint !== undefined}
                onClick={() => onPick(g.key)}
                className="text-left text-[var(--ju-primary)] hover:underline disabled:cursor-not-allowed disabled:text-[var(--ju-text)] disabled:no-underline"
              >
                {label(g)}
              </button>
            </TD>
            <TD className="text-right tabular-nums">{formatValue(g.issues)}</TD>
            <TD className="text-right tabular-nums">{formatValue(g.storyPoints)}</TD>
            <TD className="text-right tabular-nums">{share(g)}</TD>
          </TR>
        ))}
        {chart.other && (
          <TR>
            <TD className="text-[var(--ju-muted)]">{`Other (${chart.other.groups} more values)`}</TD>
            <TD className="text-right tabular-nums">{formatValue(chart.other.issues)}</TD>
            <TD className="text-right tabular-nums">{formatValue(chart.other.storyPoints)}</TD>
            <TD className="text-right tabular-nums">{share(chart.other)}</TD>
          </TR>
        )}
      </TBody>
    </Table>
  );
}
