/**
 * The display rules that must not lie. Pure on purpose: each of these is a
 * rule the API went out of its way to make unambiguous, and the UI's job is
 * not to undo that.
 */

/** The API returns null (not 0) when no issue qualifies. Zero would claim instant cycle time. */
export const NOT_ENOUGH_DATA = 'Not enough data';

/** A null status is a real group of issues with no status FK — not a status named "null". */
export const NO_STATUS_LABEL = '(no status)';

export function formatAverageDays(averageDays: number | null): string {
  if (averageDays === null) return NOT_ENOUGH_DATA;
  const rounded = Math.round(averageDays * 10) / 10;
  return `${rounded} ${rounded === 1 ? 'day' : 'days'}`;
}

export function formatStatusLabel(status: string | null): string {
  return status ?? NO_STATUS_LABEL;
}

/**
 * Only 'Ok' means the check actually ran. 'NotConfigured' — and anything
 * unrecognised — must fall through to the warning path, because a check that
 * did not run returns zero violations exactly like a clean one.
 */
export function isCheckRunnable(status: string): boolean {
  return status === 'Ok';
}

/** Bar width as a percentage of the largest value in the series. */
export function barPercent(value: number, max: number): number {
  if (max <= 0) return 0;
  return Math.round((value / max) * 100);
}
