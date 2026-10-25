import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ApiError } from '../../../api/errors';
import { getIssueChart } from '../../../api/issues';
import type { ChartGroupBy, FilterCondition, FilterGroup, IssueChartRequest } from '../../../api/issues';
import { queryKeys } from '../../../api/queryKeys';
import { Select, Spinner } from '../../../ui';
import type { ChartMeasure, ChartState, ChartType } from '../useListState';
import { ChartBars } from './ChartBars';
import { ChartDonut } from './ChartDonut';
import { ChartTable } from './ChartTable';
import { CHART_FIELDS, chartCondition, formatValue, type ChartViewProps } from './chartData';

interface ChartPanelProps {
  projectId: string;
  /** The list's pruned filter — the very one its query sends. Undefined = no filter. */
  filter?: FilterGroup;
  /** False until the list itself may query (catalog settled, no default view pending). */
  enabled: boolean;
  chart: ChartState;
  onChange: (next: ChartState) => void;
  onClose: () => void;
  /** A clicked group, as the condition that narrows the list to it. */
  onPick: (condition: FilterCondition) => void;
  /** Set when the filter is at a limit: clicks are off and this says why. */
  pickHint?: string;
}

const MEASURES: { id: ChartMeasure; label: string }[] = [
  { id: 'issues', label: 'Issues' },
  { id: 'points', label: 'Story points' },
];

const icon = (d: string) => (
  <svg viewBox="0 0 16 16" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth={1.5} aria-hidden="true">
    <path d={d} strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

const TYPES: { id: ChartType; label: string; icon: ReactNode }[] = [
  { id: 'bar', label: 'Bar chart', icon: icon('M2 3h9M2 8h12M2 13h6') },
  { id: 'donut', label: 'Donut chart', icon: icon('M8 2a6 6 0 1 0 6 6H8z M10 1.5A5 5 0 0 1 14.5 6H10z') },
  { id: 'table', label: 'Table', icon: icon('M2 3h12v10H2z M2 6.5h12 M6.5 6.5V13') },
];

const VIEWS: Record<ChartType, (props: ChartViewProps) => ReactNode> = {
  bar: ChartBars,
  donut: ChartDonut,
  table: ChartTable,
};

const pressedClass = (pressed: boolean) => (pressed
  ? 'border-[var(--ju-primary)] bg-[var(--ju-primary-soft)] text-[var(--ju-primary)]'
  : 'border-[var(--ju-border)] text-[var(--ju-text-secondary)] hover:bg-[var(--ju-surface-2)]');

/**
 * "View issues as a chart": the list's filtered issues grouped by one field. Only the field is
 * in the request — the response carries both measures, so the measure and the chart type are
 * pure display and switching them never asks the server again.
 */
export function ChartPanel({
  projectId, filter, enabled, chart, onChange, onClose, onPick, pickHint,
}: ChartPanelProps) {
  const request: IssueChartRequest = filter ? { filter, groupBy: chart.groupBy } : { groupBy: chart.groupBy };
  const { data, error, isPending } = useQuery({
    queryKey: queryKeys.issueChart(projectId, request),
    queryFn: () => getIssueChart(projectId, request),
    enabled,
    // While a new filter or field loads, the previous chart stays up rather than blanking the panel.
    placeholderData: (prev) => prev,
  });

  const View = VIEWS[chart.type];
  const set = (patch: Partial<ChartState>) => onChange({ ...chart, ...patch });

  let body: ReactNode;
  if (error) {
    body = (
      <p role="alert" className="text-sm text-[var(--ju-danger)]">
        {error instanceof ApiError ? error.title : 'Could not load the chart.'}
      </p>
    );
  } else if (isPending || !data) {
    body = <Spinner aria-label="Loading chart" />;
  } else if (data.totalIssues === 0) {
    body = <p className="text-sm text-[var(--ju-muted)]">No issues match this filter</p>;
  } else {
    body = (
      <>
        <View
          chart={data}
          measure={chart.measure}
          onPick={(key) => onPick(chartCondition(data.groupBy, key))}
          pickHint={pickHint}
        />
        <p className="text-xs text-[var(--ju-muted)]">
          {chart.measure === 'points'
            ? `Total: ${formatValue(data.totalStoryPoints)} points`
              + (data.issuesWithoutPoints > 0 ? ` · ${formatValue(data.issuesWithoutPoints)} issues without points` : '')
            : `Total: ${formatValue(data.totalIssues)} issues`}
        </p>
      </>
    );
  }

  return (
    <aside
      aria-label="Chart"
      className="flex flex-col gap-4 self-start rounded-[var(--ju-radius-lg)] border border-[var(--ju-border)] bg-[var(--ju-surface)] p-4 shadow-[var(--ju-shadow-sm)]"
    >
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold">Chart</h2>
        <button
          type="button"
          aria-label="Close chart"
          onClick={onClose}
          className="rounded p-1 text-[var(--ju-muted)] hover:bg-[var(--ju-surface-2)] hover:text-[var(--ju-text)]"
        >
          {icon('M4 4l8 8M12 4l-8 8')}
        </button>
      </div>

      <div className="flex flex-col gap-1">
        <label htmlFor="chart-group-by" className="text-xs font-medium text-[var(--ju-text-secondary)]">Group by</label>
        <Select
          id="chart-group-by"
          value={chart.groupBy}
          onChange={(e) => set({ groupBy: e.target.value as ChartGroupBy })}
        >
          {CHART_FIELDS.map((f) => <option key={f.id} value={f.id}>{f.label}</option>)}
        </Select>
      </div>

      <div className="flex flex-wrap items-center justify-between gap-2">
        <div role="group" aria-label="Measure" className="inline-flex">
          {MEASURES.map((m, i) => (
            <button
              key={m.id}
              type="button"
              aria-pressed={chart.measure === m.id}
              onClick={() => set({ measure: m.id })}
              className={`border px-2.5 py-1 text-xs ${i === 0 ? 'rounded-l-md' : '-ml-px rounded-r-md'} ${pressedClass(chart.measure === m.id)}`}
            >
              {m.label}
            </button>
          ))}
        </div>
        <div role="group" aria-label="Chart type" className="inline-flex gap-1">
          {TYPES.map((t) => (
            <button
              key={t.id}
              type="button"
              aria-label={t.label}
              title={t.label}
              aria-pressed={chart.type === t.id}
              onClick={() => set({ type: t.id })}
              className={`rounded-md border p-1.5 ${pressedClass(chart.type === t.id)}`}
            >
              {t.icon}
            </button>
          ))}
        </div>
      </div>

      {body}
    </aside>
  );
}
