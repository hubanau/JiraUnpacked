import type { FilterOperator, HistoryCompare, HistoryValue, HistoryWindow, RelativeAmount } from '../../api/issues';
import { MAX_RELATIVE_AMOUNT, readPeriod, readRelativeAmount } from './valueShapes';

/**
 * The `history` pseudo-field's values — what "was", "changed" and "number of changes" take —
 * their limits and defaults, and the reader that turns untrusted JSON (a link, a stored view)
 * into exactly that shape: a fresh object in one key order per operator, or nothing at all.
 */

/** The catalog name of the pseudo-field. */
export const HISTORY_FIELD = 'history';

type HistoryOperator = 'Was' | 'Changed' | 'ChangeCount';

export const takesHistory = (op: FilterOperator): op is HistoryOperator =>
  op === 'Was' || op === 'Changed' || op === 'ChangeCount';

/** The server's limits (IssueFilterTranslator.History). */
export const MAX_HISTORY_FIELD_LENGTH = 100;
export const MAX_HISTORY_VALUE_LENGTH = 255;
export const MAX_CHANGE_COUNT = 10000;

/** In the order the compare select offers them. */
export const HISTORY_COMPARES: readonly { key: HistoryCompare; label: string }[] = [
  { key: 'Eq', label: '=' },
  { key: 'Neq', label: '≠' },
  { key: 'Gt', label: '>' },
  { key: 'Gte', label: '≥' },
  { key: 'Lt', label: '<' },
  { key: 'Lte', label: '≤' },
];

/** "number of changes" starts at "≥ 2", so choosing it already finds the churners. */
export const DEFAULT_CHANGE_COUNT: { compare: HistoryCompare; count: number } = { compare: 'Gte', count: 2 };

/** "during: in the last" starts at 30 days. */
export const DEFAULT_WINDOW_AMOUNT: RelativeAmount = { amount: 30, unit: 'days' };

/** The keys each operator takes; any other key with a value makes a link's value unreadable. */
const HISTORY_KEYS: Record<HistoryOperator, readonly string[]> = {
  Was: ['historyField', 'values'],
  Changed: ['historyField', 'from', 'to', 'by', 'during'],
  ChangeCount: ['historyField', 'compare', 'count', 'during'],
};

const isRecord = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

const BAD = Symbol('unreadable');

/**
 * A value list from untrusted JSON. Absent or null → undefined. Names, and (Empty) as null — ""
 * too, as the server reads it — where allowed; de-duplicated. Anything else → BAD.
 */
function readList(x: unknown, allowEmpty: boolean): (string | null)[] | undefined | typeof BAD {
  if (x === undefined || x === null) return undefined;
  if (!Array.isArray(x)) return BAD;
  const out: (string | null)[] = [];
  for (const raw of x) {
    const v: unknown = raw === '' ? null : raw;
    if (v === null) {
      if (!allowEmpty) return BAD;
    } else if (typeof v !== 'string') {
      return BAD;
    }
    const item = v as string | null;
    if (!out.includes(item)) out.push(item);
  }
  return out;
}

/** A `during` from untrusted JSON, in canonical key order — or undefined when it is none of the three shapes. */
export function readWindow(v: unknown): HistoryWindow | undefined {
  if (!isRecord(v)) return undefined;
  if (v.kind === 'inLast') {
    const rel = readRelativeAmount(v);
    return rel ? { kind: 'inLast', amount: rel.amount, unit: rel.unit } : undefined;
  }
  if (v.kind === 'inPeriod') {
    const period = readPeriod(v);
    return period ? { kind: 'inPeriod', ...period } : undefined;
  }
  if (v.kind === 'between') {
    const bound = (x: unknown): string | null | typeof BAD =>
      x === undefined || x === null || x === '' ? null : typeof x === 'string' ? x : BAD;
    const from = bound(v.from);
    const to = bound(v.to);
    return from === BAD || to === BAD ? undefined : { kind: 'between', from, to };
  }
  return undefined;
}

/** In the last: a whole amount 1–3650. Between: a bound, and not reversed. In period: always (the reader admits only known periods). */
export function isCompleteWindow(w: HistoryWindow): boolean {
  if (w.kind === 'inLast') return Number.isInteger(w.amount) && w.amount >= 1 && w.amount <= MAX_RELATIVE_AMOUNT;
  if (w.kind === 'between') {
    if (w.from === null && w.to === null) return false;
    return w.from === null || w.to === null || w.from <= w.to;
  }
  return true;
}

/**
 * The value as the server takes it and a saved view stores it: only the operator's keys, in one
 * order, optional lists left out when empty, every array and the window copied.
 */
export function canonicalHistoryValue(op: FilterOperator, v: HistoryValue): HistoryValue {
  const out: HistoryValue = { historyField: v.historyField };
  if (op === 'Was') {
    out.values = [...(v.values ?? [])];
    return out;
  }
  if (op === 'Changed') {
    if (v.from?.length) out.from = [...v.from];
    if (v.to?.length) out.to = [...v.to];
    if (v.by?.length) out.by = [...v.by];
  } else {
    if (v.compare !== undefined) out.compare = v.compare;
    if (v.count !== undefined) out.count = v.count;
  }
  if (v.during) out.during = { ...v.during };
  return out;
}

/** What each operator starts with: "was" no values yet, "changed" nothing, "number of changes" ≥ 2. */
export function freshHistoryValue(op: FilterOperator, historyField: string): HistoryValue {
  if (op === 'Was') return { historyField, values: [] };
  if (op === 'ChangeCount') return { historyField, ...DEFAULT_CHANGE_COUNT };
  return { historyField };
}

/**
 * A history value from untrusted JSON, read whole or not at all: a part it cannot read (a key
 * this operator does not take, a list that is not one, a window of no known kind, an unknown
 * compare, a count that is not a number) leaves it unreadable — the row stays on screen,
 * unfinished, rather than quietly meaning more than the link said.
 */
export function readHistoryValue(op: FilterOperator, raw: unknown): HistoryValue | undefined {
  if (!takesHistory(op) || !isRecord(raw) || typeof raw.historyField !== 'string') return undefined;
  const keys = HISTORY_KEYS[op];
  if (Object.entries(raw).some(([k, x]) => !keys.includes(k) && x !== null && x !== undefined)) return undefined;

  const v: HistoryValue = { historyField: raw.historyField };
  if (op === 'Was') {
    const values = readList(raw.values, true);
    if (values === BAD) return undefined;
    v.values = values ?? [];
    return canonicalHistoryValue(op, v);
  }

  if (raw.during !== undefined && raw.during !== null) {
    const during = readWindow(raw.during);
    if (!during) return undefined;
    v.during = during;
  }

  if (op === 'Changed') {
    const from = readList(raw.from, true);
    const to = readList(raw.to, true);
    const by = readList(raw.by, false);
    if (from === BAD || to === BAD || by === BAD) return undefined;
    if (from) v.from = from;
    if (to) v.to = to;
    if (by) v.by = by.filter((x): x is string => x !== null);
    return canonicalHistoryValue(op, v);
  }

  if (raw.compare !== undefined && raw.compare !== null) {
    const compare = HISTORY_COMPARES.find((c) => c.key === raw.compare);
    if (!compare) return undefined;
    v.compare = compare.key;
  }
  if (raw.count !== undefined && raw.count !== null) {
    if (typeof raw.count !== 'number' || !Number.isFinite(raw.count)) return undefined;
    v.count = raw.count;
  }
  return canonicalHistoryValue(op, v);
}

const fits = (list: readonly (string | null)[] | undefined): boolean =>
  (list ?? []).every((x) => x === null || x.length <= MAX_HISTORY_VALUE_LENGTH);

/**
 * Finished enough to send: a field name (non-blank, ≤ 100); "was" at least one value; "number of
 * changes" a compare and a whole count 0–10000; any window finished; no value over 255 characters.
 */
export function isCompleteHistoryValue(op: FilterOperator, v: HistoryValue | undefined): boolean {
  if (!v || v.historyField.trim() === '' || v.historyField.length > MAX_HISTORY_FIELD_LENGTH) return false;
  if (v.during && !isCompleteWindow(v.during)) return false;
  if (op === 'Was') return (v.values?.length ?? 0) > 0 && fits(v.values);
  if (op === 'Changed') return fits(v.from) && fits(v.to) && fits(v.by);
  if (op === 'ChangeCount') {
    return v.compare !== undefined && v.count !== undefined && Number.isInteger(v.count)
      && v.count >= 0 && v.count <= MAX_CHANGE_COUNT;
  }
  return false;
}
