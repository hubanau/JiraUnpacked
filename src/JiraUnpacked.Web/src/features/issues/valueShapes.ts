import type {
  ConditionValue, ListValue, PeriodKey, PeriodValue, RelativeAmount, RelativeUnit, SmartSprintKey, SmartValue,
} from '../../api/issues';

/**
 * The object-shaped filter values: a relative amount ("in the last" / "more than … ago"), a
 * calendar period ("in period") and a smart sprint value. Their vocabularies, labels and
 * defaults, and the readers that turn untrusted JSON — a link, a stored view — into exactly
 * these shapes, as fresh objects in one key order, or into nothing.
 */

export const RELATIVE_UNITS: readonly RelativeUnit[] = ['hours', 'days', 'weeks', 'months'];

/** Reading order: the order the period select offers them in. */
export const PERIODS: readonly { key: PeriodKey; label: string }[] = [
  { key: 'today', label: 'Today' },
  { key: 'yesterday', label: 'Yesterday' },
  { key: 'thisWeek', label: 'This week' },
  { key: 'lastWeek', label: 'Last week' },
  { key: 'thisMonth', label: 'This month' },
  { key: 'lastMonth', label: 'Last month' },
  { key: 'thisQuarter', label: 'This quarter' },
  { key: 'lastQuarter', label: 'Last quarter' },
  { key: 'thisYear', label: 'This year' },
  { key: 'lastYear', label: 'Last year' },
];

/** In the order the pickers offer them. The server sends the same labels; these read right before it answers. */
export const SMART_SPRINT_LABELS: Record<SmartSprintKey, string> = {
  active: 'Active sprint',
  future: 'Future sprints',
  open: 'Open sprints (active + future)',
  closed: 'Closed sprints',
};

/** The largest "in the last" / "more than … ago" amount the server takes (RelativeDates.MaxAmount). */
export const MAX_RELATIVE_AMOUNT = 3650;

/** "in the last" starts at 14 days, so choosing the operator already filters. */
export const DEFAULT_RELATIVE: RelativeAmount = { amount: 14, unit: 'days' };

/** "in period" likewise starts somewhere useful. */
export const DEFAULT_PERIOD: PeriodKey = 'thisWeek';

const isRecord = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

const PERIOD_KEYS: ReadonlySet<string> = new Set(PERIODS.map((p) => p.key));

export const isSmartKey = (k: string): k is SmartSprintKey =>
  Object.prototype.hasOwnProperty.call(SMART_SPRINT_LABELS, k);

export function readSmart(v: unknown): SmartValue | undefined {
  if (!isRecord(v)) return undefined;
  const key = v.smart;
  return typeof key === 'string' && isSmartKey(key) ? { smart: key } : undefined;
}

export function readRelativeAmount(v: unknown): RelativeAmount | undefined {
  if (!isRecord(v)) return undefined;
  const { amount } = v;
  const unit = RELATIVE_UNITS.find((u) => u === v.unit);
  return typeof amount === 'number' && Number.isFinite(amount) && unit ? { amount, unit } : undefined;
}

/** A period without a zone stays zoneless: the server reads that as UTC, so it keeps its meaning. */
export function readPeriod(v: unknown): PeriodValue | undefined {
  if (!isRecord(v)) return undefined;
  const { period, timeZone } = v;
  if (typeof period !== 'string' || !PERIOD_KEYS.has(period)) return undefined;
  const out: PeriodValue = { period: period as PeriodKey };
  if (typeof timeZone === 'string' && timeZone !== '') out.timeZone = timeZone;
  return out;
}

export const isSmartValue = (v: unknown): v is SmartValue => readSmart(v) !== undefined;
export const isRelativeAmount = (v: unknown): v is RelativeAmount => readRelativeAmount(v) !== undefined;
export const isPeriodValue = (v: unknown): v is PeriodValue => readPeriod(v) !== undefined;

/** A plain name, number or date — what the text and number inputs edit. */
export const isScalar = (v: ConditionValue | ListValue | undefined): v is string | number =>
  typeof v === 'string' || typeof v === 'number';

export const smartLabel = (v: SmartValue): string => SMART_SPRINT_LABELS[v.smart];

/** The zone "in period" is evaluated in: the browser's own, captured when the operator is chosen. */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}
