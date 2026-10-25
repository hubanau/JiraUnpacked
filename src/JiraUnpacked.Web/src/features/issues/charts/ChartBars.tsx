import { barPercent } from '../../insight/format';
import { formatValue, topN, type ChartSlice, type ChartViewProps } from './chartData';

/** Bars shown before the rest fold into Other. */
const BARS = 10;

/**
 * Horizontal bars, largest first (a timeline in its own order). One series, so one colour; Other
 * is grey and, not being a value, not clickable. The number beside each bar carries the value —
 * the bar itself is decorative, as in BarRow.
 */
export function ChartBars({ chart, measure, onPick, pickHint }: ChartViewProps) {
  const slices = topN(chart, measure, BARS);
  const max = Math.max(0, ...slices.map((s) => s.value));

  const row = (s: ChartSlice) => (
    <>
      <span className="w-32 shrink-0 truncate text-left text-sm">{s.label}</span>
      <span className="h-3 flex-1" aria-hidden="true">
        <span
          className="block h-3 rounded-r"
          style={{
            width: `${barPercent(s.value, max)}%`,
            background: s.group ? 'var(--ju-chart-1)' : 'var(--ju-chart-other)',
          }}
        />
      </span>
      <span className="w-16 shrink-0 text-right text-sm tabular-nums">{formatValue(s.value)}</span>
    </>
  );

  return (
    <ul className="flex flex-col gap-1">
      {slices.map((s) => (
        <li key={s.group ? `g:${s.key}` : 'other'}>
          {s.group ? (
            <button
              type="button"
              aria-label={`Filter to ${s.label}: ${formatValue(s.value)}`}
              title={pickHint ?? `${s.label}: ${formatValue(s.value)}`}
              disabled={pickHint !== undefined}
              onClick={() => onPick(s.key)}
              className="flex w-full items-center gap-2 rounded px-1 py-0.5 hover:bg-[var(--ju-surface-2)] disabled:cursor-not-allowed disabled:hover:bg-transparent"
            >
              {row(s)}
            </button>
          ) : (
            <div className="flex items-center gap-2 px-1 py-0.5 text-[var(--ju-muted)]">{row(s)}</div>
          )}
        </li>
      ))}
    </ul>
  );
}
