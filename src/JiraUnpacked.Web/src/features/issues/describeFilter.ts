import type {
  ConditionValue, FilterCondition, FilterGroup, HistoryWindow, ListValue,
} from '../../api/issues';
import { ISSUE_COLUMNS } from './columns';
import { OPERATOR_LABELS } from './filterCatalogHelpers';
import { HISTORY_COMPARES, readHistoryValue, takesHistory } from './historyShapes';
import { PERIODS, readPeriod, readRelativeAmount, readSmart, smartLabel } from './valueShapes';

/**
 * The filter as one sentence — "Status is any of Backlog, To Do and (Assignee is Alice or …)" —
 * for the summary line above the builder. It reads the PRUNED tree (pruneTree), the very object
 * the list is queried with, so the sentence says what is applied: switched-off and unfinished
 * parts are not in it.
 *
 * Field names are the table's column headers ("Category", "Type", "Points"), operators the
 * editor's own words (OPERATOR_LABELS), and dates a fixed English form rather than the locale's,
 * so a shared link reads the same for whoever opens it.
 */

const MAX_LISTED = 5;
const SHOWN_WHEN_TRUNCATED = 2;
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const DATE_FIELDS: ReadonlySet<string> = new Set(['createdAt', 'updatedAt']);

export const fieldLabel = (field: string): string =>
  ISSUE_COLUMNS.find((c) => c.id === field)?.header ?? field;

/** "2026-09-01" → "1 Sep 2026"; with a time → "1 Sep 2026 10:30"; anything else as written. */
export function formatDate(raw: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2}))?/.exec(raw);
  if (!m) return raw;
  const month = MONTHS[Number(m[2]) - 1];
  if (!month) return raw;
  const day = `${Number(m[3])} ${month} ${m[1]}`;
  return m[4] && !(m[4] === '00' && m[5] === '00') ? `${day} ${m[4]}:${m[5]}` : day;
}

/** "A, B, C"; more than five → "A, B, … and 4 more". */
export function listText(items: readonly string[]): string {
  if (items.length <= MAX_LISTED) return items.join(', ');
  return `${items.slice(0, SHOWN_WHEN_TRUNCATED).join(', ')}, … and ${items.length - SHOWN_WHEN_TRUNCATED} more`;
}

function valueText(field: string, v: ListValue | ConditionValue | undefined): string {
  if (v === null || v === undefined || v === '') return 'empty';
  if (typeof v === 'number') return String(v);
  if (typeof v === 'string') return DATE_FIELDS.has(field) ? formatDate(v) : v;
  const smart = readSmart(v);
  return smart ? smartLabel(smart) : JSON.stringify(v);
}

/** "14 days", "1 week". */
function amountText(v: unknown): string {
  const rel = readRelativeAmount(v);
  if (!rel) return '…';
  return `${rel.amount} ${rel.amount === 1 ? rel.unit.slice(0, -1) : rel.unit}`;
}

/** "this week (Europe/Warsaw)"; no zone, no parentheses. */
function periodText(v: unknown): string {
  const p = readPeriod(v);
  if (!p) return '…';
  const label = (PERIODS.find((x) => x.key === p.period)?.label ?? p.period).toLowerCase();
  return p.timeZone ? `${label} (${p.timeZone})` : label;
}

const isBound = (x: ListValue | undefined): boolean => x !== null && x !== undefined && x !== '';

/** Between: "between A and B"; one bound open: "≥ A" / "≤ B". */
function rangeText(field: string, values: ListValue[] | undefined): string {
  const [from, to] = values ?? [];
  if (isBound(from) && isBound(to)) return `between ${valueText(field, from)} and ${valueText(field, to)}`;
  return isBound(from) ? `≥ ${valueText(field, from)}` : `≤ ${valueText(field, to)}`;
}

function windowText(w: HistoryWindow): string {
  if (w.kind === 'inLast') return `in the last ${amountText(w)}`;
  if (w.kind === 'inPeriod') return `in period ${periodText(w)}`;
  if (w.from && w.to) return `between ${formatDate(w.from)} and ${formatDate(w.to)}`;
  return w.from ? `since ${formatDate(w.from)}` : `until ${formatDate(w.to ?? '')}`;
}

/** One value as itself; several as "any of A, B". */
function oneOrAny(values: readonly (string | null)[]): string {
  const shown = values.map((v) => (v === null ? 'empty' : v));
  return shown.length === 1 ? shown[0] : `any of ${listText(shown)}`;
}

/** "status was Blocked", "Sprint changed from A to B by Alice …", "Sprint changed ≥ 3 times …". */
function describeHistory(c: FilterCondition): string {
  const v = readHistoryValue(c.op, c.value);
  // Unreachable for a pruned tree (an unreadable value is unfinished, so never sent).
  if (!v) return `${fieldLabel(c.field)} ${OPERATOR_LABELS[c.op]}`;
  const during = v.during ? ` ${windowText(v.during)}` : '';
  if (c.op === 'Was') return `${v.historyField} was ${oneOrAny(v.values ?? [])}`;
  if (c.op === 'Changed') {
    const parts = [`${v.historyField} changed`];
    if (v.from?.length) parts.push(`from ${oneOrAny(v.from)}`);
    if (v.to?.length) parts.push(`to ${oneOrAny(v.to)}`);
    if (v.by?.length) parts.push(`by ${oneOrAny(v.by)}`);
    return parts.join(' ') + during;
  }
  const compare = HISTORY_COMPARES.find((x) => x.key === v.compare)?.label ?? '';
  const count = v.count ?? 0;
  return `${v.historyField} changed ${compare} ${count} ${count === 1 ? 'time' : 'times'}${during}`;
}

function describeCondition(c: FilterCondition): string {
  if (takesHistory(c.op)) return describeHistory(c);
  const field = fieldLabel(c.field);
  switch (c.op) {
    case 'IsNull':
      return `${field} is empty`;
    case 'IsNotNull':
      return `${field} is not empty`;
    case 'In':
    case 'NotIn':
      return `${field} ${OPERATOR_LABELS[c.op]} ${listText((c.values ?? []).map((v) => valueText(c.field, v)))}`;
    case 'Between':
      return `${field} ${rangeText(c.field, c.values)}`;
    case 'InLast':
      return `${field} in the last ${amountText(c.value)}`;
    case 'NotInLast':
      return `${field} more than ${amountText(c.value)} ago`;
    case 'InPeriod':
      return `${field} in period ${periodText(c.value)}`;
    case 'Contains':
    case 'NotContains':
    case 'StartsWith':
      return `${field} ${OPERATOR_LABELS[c.op]} "${valueText(c.field, c.value)}"`;
    default:
      return `${field} ${OPERATOR_LABELS[c.op]} ${valueText(c.field, c.value)}`;
  }
}

function describeGroup(g: FilterGroup, nested: boolean): string {
  const parts = [
    ...(g.conditions ?? []).filter((c) => !c.disabled).map(describeCondition),
    ...(g.groups ?? []).filter((x) => !x.disabled).map((x) => describeGroup(x, true)),
  ].filter((s) => s !== '');
  const text = parts.join(g.logic === 'Or' ? ' or ' : ' and ');
  return nested && parts.length > 1 ? `(${text})` : text;
}

/** The pruned filter as one sentence; no filter at all reads "All issues". */
export function describeFilter(filter: FilterGroup | undefined): string {
  if (!filter || filter.disabled) return 'All issues';
  const text = describeGroup(filter, false);
  return text === '' ? 'All issues' : text;
}
