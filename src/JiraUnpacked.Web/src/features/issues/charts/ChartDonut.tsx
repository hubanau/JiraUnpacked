import { formatCompact, formatValue, topN, totalOf, type ChartSlice, type ChartViewProps } from './chartData';

/** Slices shown before the rest fold into Other: one per categorical colour. */
const SLICES = 8;
/** Below this share a slice gets no number in the ring — it would not fit. The legend still has it. */
const LABEL_MIN_SHARE = 0.03;

const C = 100;          // centre of the 200×200 viewBox
const OUTER = 92;
const INNER = 60;

const point = (r: number, angle: number) => [C + r * Math.cos(angle), C + r * Math.sin(angle)];

/** One ring segment from angle a0 to a1 (radians, clockwise from 3 o'clock). */
function segment(a0: number, a1: number): string {
  // A full ring cannot be one arc (start = end draws nothing): split it in two.
  if (a1 - a0 >= 2 * Math.PI - 1e-9) {
    const mid = a0 + Math.PI;
    return `${segment(a0, mid)} ${segment(mid, a1)}`;
  }
  const large = a1 - a0 > Math.PI ? 1 : 0;
  const [x0, y0] = point(OUTER, a0);
  const [x1, y1] = point(OUTER, a1);
  const [x2, y2] = point(INNER, a1);
  const [x3, y3] = point(INNER, a0);
  return `M${x0} ${y0} A${OUTER} ${OUTER} 0 ${large} 1 ${x1} ${y1} L${x2} ${y2} A${INNER} ${INNER} 0 ${large} 0 ${x3} ${y3} Z`;
}

const isValue = (s: ChartSlice) => s.group !== null && s.key !== null;

/**
 * Categorical slots in fixed order, counted among the VALUES only: Other, Earlier and (Empty) are
 * grey and use up no slot, so at most SLICES values ever ask for one of the eight colours.
 */
function colorsOf(slices: ChartSlice[]): string[] {
  let slot = 0;
  return slices.map((s) => (isValue(s) ? `var(--ju-chart-${++slot})` : 'var(--ju-chart-other)'));
}

const keyOf = (s: ChartSlice) => (s.group ? `g:${s.key}` : 'other');

/**
 * A donut with the total in the middle and a legend below. The legend is the accessible chart:
 * it names every slice with its value and share, and its entries are the click targets — the
 * SVG is a picture of the same numbers.
 */
export function ChartDonut({ chart, measure, onPick, pickHint }: ChartViewProps) {
  // A timeline keeps its newest SLICES months: past eight there is no colour left to give.
  const slices = topN(chart, measure, SLICES, SLICES);
  const colors = colorsOf(slices);
  const total = totalOf(chart, measure);
  const word = measure === 'points' ? 'Points' : 'Issues';

  if (total <= 0) {
    return <p className="text-sm text-[var(--ju-muted)]">No story points in these issues.</p>;
  }

  // Starts at 12 o'clock and runs clockwise, like Jira's.
  let angle = -Math.PI / 2;
  const arcs = slices.map((s, i) => {
    const share = s.value / total;
    const a0 = angle;
    angle += share * 2 * Math.PI;
    return { s, i, share, a0, a1: angle };
  });
  const pct = (share: number) => `${Math.round(share * 100)}%`;

  return (
    <div className="flex flex-col items-center gap-3">
      <svg
        viewBox="0 0 200 200"
        className="w-full max-w-[260px]"
        role="img"
        aria-label={`Donut chart, total ${formatValue(total)} ${word.toLowerCase()}`}
      >
        {arcs.filter((a) => a.share > 0).map(({ s, i, share, a0, a1 }) => {
          const [lx, ly] = point((OUTER + INNER) / 2, (a0 + a1) / 2);
          return (
            <g key={keyOf(s)} aria-hidden="true">
              <path d={segment(a0, a1)} fill={colors[i]} stroke="var(--ju-surface)" strokeWidth={2}>
                <title>{`${s.label}: ${formatValue(s.value)} (${pct(share)})`}</title>
              </path>
              {share >= LABEL_MIN_SHARE && (
                <text
                  x={lx} y={ly} textAnchor="middle" dominantBaseline="central" fontSize={10}
                  fill="var(--ju-text)" stroke="var(--ju-surface)" strokeWidth={3} paintOrder="stroke"
                  pointerEvents="none"
                >
                  {formatCompact(s.value)}
                </text>
              )}
            </g>
          );
        })}
        <g data-testid="donut-total">
          <text x={C} y={C - 4} textAnchor="middle" fontSize={26} fontWeight={600} fill="var(--ju-text)">
            {formatCompact(total)}
          </text>
          <text x={C} y={C + 18} textAnchor="middle" fontSize={11} fill="var(--ju-muted)">{word}</text>
        </g>
      </svg>

      <ul aria-label="Legend" className="flex w-full flex-col gap-0.5 text-sm">
        {arcs.map(({ s, i, share }) => {
          const content = (
            <>
              <span className="h-3 w-3 shrink-0 rounded-sm" style={{ background: colors[i] }} aria-hidden="true" />
              <span className="flex-1 truncate text-left">{s.label}</span>
              <span className="tabular-nums">{formatValue(s.value)}</span>
              <span className="w-10 text-right tabular-nums text-[var(--ju-muted)]">{pct(share)}</span>
            </>
          );
          return (
            <li key={keyOf(s)}>
              {s.group ? (
                <button
                  type="button"
                  aria-label={`Filter to ${s.label}: ${formatValue(s.value)}`}
                  title={pickHint}
                  disabled={pickHint !== undefined}
                  onClick={() => onPick(s.key)}
                  className="flex w-full items-center gap-2 rounded px-1 py-0.5 hover:bg-[var(--ju-surface-2)] disabled:cursor-not-allowed disabled:hover:bg-transparent"
                >
                  {content}
                </button>
              ) : (
                <div className="flex items-center gap-2 px-1 py-0.5 text-[var(--ju-muted)]">{content}</div>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
