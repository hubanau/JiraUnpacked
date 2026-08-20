import { barPercent } from './format';

/**
 * No charting library — a CSS bar plus its label and value. The track is
 * `aria-hidden` because the number beside it carries the information; a
 * screen reader hears the label and the value, never a decorative div, and
 * nothing depends on reading a pixel width (the number is the real value).
 */
export function BarRow({ label, value, max, suffix }: {
  label: string; value: number; max: number; suffix?: string;
}) {
  const pct = barPercent(value, max);
  return (
    <div className="flex items-center gap-3">
      <span className="w-40 shrink-0 truncate text-sm">{label}</span>
      <div className="h-4 flex-1 rounded bg-[var(--ju-surface)]" aria-hidden="true">
        <div className="h-4 rounded bg-[var(--ju-primary)]" style={{ width: `${pct}%` }} />
      </div>
      <span className="w-24 shrink-0 text-right text-sm tabular-nums">
        {value}{suffix ? ` ${suffix}` : ''}
      </span>
    </div>
  );
}
