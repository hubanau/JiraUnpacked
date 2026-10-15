import type {
  ConditionValue, FilterCondition, FilterGroup, FilterLogic, FilterOperator, ListValue,
} from '../../api/issues';
import { OPERATOR_LABELS } from './filterCatalogHelpers';
import {
  HISTORY_FIELD, freshHistoryValue, isCompleteHistoryValue, readHistoryValue, takesHistory,
} from './historyShapes';
import {
  DEFAULT_PERIOD, DEFAULT_RELATIVE, MAX_RELATIVE_AMOUNT, isRelativeAmount, isScalar, isSmartValue, readPeriod, readRelativeAmount,
  readSmart,
} from './valueShapes';

/**
 * The editor's working copy of a filter: the wire FilterGroup plus a client-only `id` on every
 * node. Rows are keyed by it, so a row keeps its identity — focus, a half-typed chip — when a
 * sibling above it is removed or duplicated. It never leaves the browser: toUrlJson and
 * pruneTree both strip it.
 */
export interface EditorCondition extends FilterCondition {
  id: string;
}

export interface EditorGroup {
  id: string;
  logic: FilterLogic;
  conditions: EditorCondition[];
  groups: EditorGroup[];
  disabled?: boolean;
}

export interface TreeCounts {
  conditions: number;
  groups: number;
  /** Enabled conditions on the history pseudo-field: capped on their own (catalog.maxHistoryConditions). */
  history: number;
}

/**
 * A field's operator is valid iff it has a label — OPERATOR_LABELS is `Record<FilterOperator,
 * string>`, so it is exhaustive by construction and the compiler refuses to build if a member is
 * ever added to FilterOperator without one. A live lookup rather than a list captured once at
 * module load, so this stays true even if OPERATOR_LABELS is ever extended at runtime.
 */
const isKnownOperator = (op: string): op is FilterOperator =>
  Object.prototype.hasOwnProperty.call(OPERATOR_LABELS, op);

let lastId = 0;
/** Unique for the page's lifetime, which is all a React key needs. */
export const newId = (): string => `f${(lastId += 1)}`;

export const emptyRoot = (): EditorGroup => ({ id: newId(), logic: 'And', conditions: [], groups: [] });

/** What "Add condition" creates. Status is the field people filter on first. */
export const blankCondition = (): EditorCondition => ({ id: newId(), field: 'status', op: 'Eq', value: '' });

/** "Add group" seeds one blank row, so a new group is never an (invalid) empty one. */
export const blankGroup = (): EditorGroup => ({
  id: newId(), logic: 'And', conditions: [blankCondition()], groups: [],
});

export const isEmptyTree = (g: EditorGroup): boolean => g.conditions.length === 0 && g.groups.length === 0;

export const takesNoValue = (op: FilterOperator): boolean => op === 'IsNull' || op === 'IsNotNull';
export const takesList = (op: FilterOperator): boolean => op === 'In' || op === 'NotIn';
/** "between": `values: [from, to]`, either bound null (open). */
export const takesRange = (op: FilterOperator): boolean => op === 'Between';
/** "in the last" / "more than … ago": `value: { amount, unit }`. */
export const takesAmount = (op: FilterOperator): boolean => op === 'InLast' || op === 'NotInLast';
/** "in period": `value: { period, timeZone }`. */
export const takesPeriod = (op: FilterOperator): boolean => op === 'InPeriod';

const isBound = (v: ListValue | undefined): boolean => v !== undefined && v !== null && v !== '';
/** An empty bound is an open one: the server reads null as "no limit on this side". */
const asBound = (v: ListValue | undefined): ListValue => (isBound(v) ? (v as ListValue) : null);

/**
 * Object values are copied, never shared: two rows — or the editor and the wire — must not alias
 * one object, nor the arrays inside a history value. Values are plain JSON, so a JSON round trip
 * is a faithful deep copy.
 */
function copyValue<T>(v: T): T {
  return (typeof v === 'object' && v !== null ? JSON.parse(JSON.stringify(v)) : v) as T;
}

// ---- sending ----

/**
 * A condition the user has started but not finished is unfinished, not invalid: it is left out
 * of the request rather than sent to earn a 400 mid-edit. A null `value` is (Empty) and complete.
 * Between needs at least one bound; in the last / more than … ago a whole amount from 1 to 3650;
 * in period a period.
 */
export function isCompleteCondition(c: FilterCondition): boolean {
  if (takesHistory(c.op)) return isCompleteHistoryValue(c.op, readHistoryValue(c.op, c.value));
  if (takesNoValue(c.op)) return true;
  if (takesList(c.op)) return (c.values?.length ?? 0) > 0;
  if (takesRange(c.op)) return (c.values ?? []).some(isBound);
  if (takesAmount(c.op)) {
    const rel = readRelativeAmount(c.value);
    return rel !== undefined && Number.isInteger(rel.amount) && rel.amount >= 1 && rel.amount <= MAX_RELATIVE_AMOUNT;
  }
  if (takesPeriod(c.op)) return readPeriod(c.value) !== undefined;
  return c.value !== undefined && c.value !== '';
}

/** One condition as the server takes it: only its operator's key, (Empty) on is/is not as is empty/is not empty. */
export function toWireCondition(c: FilterCondition): FilterCondition {
  if ((c.op === 'Eq' || c.op === 'Neq') && c.value === null) {
    return { field: c.field, op: c.op === 'Eq' ? 'IsNull' : 'IsNotNull' };
  }
  if (takesNoValue(c.op)) return { field: c.field, op: c.op };
  if (takesList(c.op)) return { field: c.field, op: c.op, values: (c.values ?? []).map(copyValue) };
  if (takesRange(c.op)) {
    const [from, to] = c.values ?? [];
    return { field: c.field, op: c.op, values: [asBound(from), asBound(to)] };
  }
  if (takesHistory(c.op)) {
    // Canonical and freshly built: the key order a saved view compares on, no arrays shared.
    const value = readHistoryValue(c.op, c.value);
    return value ? { field: c.field, op: c.op, value } : { field: c.field, op: c.op };
  }
  return { field: c.field, op: c.op, value: copyValue(c.value) };
}

/**
 * What the server is sent: disabled parts, unfinished rows and rows on a field the catalog no
 * longer has are dropped, groups left empty by that go too, and ids go. Undefined when nothing
 * is left — the server refuses an empty group, and "no filter" is what the user has.
 *
 * `knownFields` is optional because the catalog may still be loading; until it arrives the
 * field check is skipped and the server's own validation is the backstop.
 */
export function pruneTree(root: EditorGroup, knownFields?: ReadonlySet<string>): FilterGroup | undefined {
  if (root.disabled) return undefined;
  const conditions = root.conditions
    .filter((c) => !c.disabled && isCompleteCondition(c) && (!knownFields || knownFields.has(c.field)))
    .map(toWireCondition);
  const groups = root.groups
    .map((g) => pruneTree(g, knownFields))
    .filter((g): g is FilterGroup => g !== undefined);
  if (conditions.length === 0 && groups.length === 0) return undefined;
  return {
    logic: root.logic,
    ...(conditions.length > 0 ? { conditions } : {}),
    ...(groups.length > 0 ? { groups } : {}),
  };
}

/**
 * What a saved view stores: the tree as the user built it — disabled parts KEPT, so switching one
 * back on after opening the view works — minus what is not a filter yet: unfinished conditions,
 * and groups left empty by removing them. Ids stripped; (Empty) on is / is not in its wire form
 * (the server refuses a null `value`). Undefined when nothing is left: an empty tree saves no filter.
 *
 * Deterministic key order, so two trees that mean the same thing stringify the same — which is
 * how "modified" is decided (see viewState.isModified).
 */
export function toSavable(root: EditorGroup): FilterGroup | undefined {
  const conditions = root.conditions
    .filter(isCompleteCondition)
    .map((c) => (c.disabled ? { ...toWireCondition(c), disabled: true } : toWireCondition(c)));
  const groups = root.groups
    .map((g) => toSavable(g))
    .filter((g): g is FilterGroup => g !== undefined);
  if (conditions.length === 0 && groups.length === 0) return undefined;
  return {
    logic: root.logic,
    ...(conditions.length > 0 ? { conditions } : {}),
    ...(groups.length > 0 ? { groups } : {}),
    ...(root.disabled ? { disabled: true } : {}),
  };
}

// ---- counting (matches the server's budget) ----

/**
 * Enabled conditions and groups, counted as the server counts them: over the whole tree, the
 * root included as a group, and a disabled node — with everything under it — not at all.
 */
export function countEnabled(g: EditorGroup): TreeCounts {
  if (g.disabled) return { conditions: 0, groups: 0, history: 0 };
  const on = g.conditions.filter((c) => !c.disabled);
  let conditions = on.length;
  let history = on.filter((c) => c.field === HISTORY_FIELD).length;
  let groups = 1;
  for (const child of g.groups) {
    const sub = countEnabled(child);
    conditions += sub.conditions;
    groups += sub.groups;
    history += sub.history;
  }
  return { conditions, groups, history };
}

// ---- editing ----

export const cloneCondition = (c: EditorCondition): EditorCondition => ({
  ...c,
  id: newId(),
  ...(c.values ? { values: c.values.map(copyValue) } : {}),
  ...(c.value !== undefined ? { value: copyValue(c.value) } : {}),
});

export const cloneGroup = (g: EditorGroup): EditorGroup => ({
  ...g, id: newId(), conditions: g.conditions.map(cloneCondition), groups: g.groups.map(cloneGroup),
});

export function toggleDisabled<T extends { disabled?: boolean }>(node: T): T {
  if (!node.disabled) return { ...node, disabled: true };
  const next = { ...node };
  delete (next as { disabled?: boolean }).disabled;
  return next;
}

/** A row ConditionRow rebuilt from scratch, put back in its place: same id, same on/off state. */
export function withIdentityOf(row: EditorCondition, next: FilterCondition): EditorCondition {
  const out: EditorCondition = { ...next, id: row.id };
  if (row.disabled) out.disabled = true;
  else delete out.disabled;
  return out;
}

export function insertAfter<T extends { id: string }>(list: T[], id: string, item: T): T[] {
  const at = list.findIndex((x) => x.id === id);
  return at < 0 ? [...list, item] : [...list.slice(0, at + 1), item, ...list.slice(at + 1)];
}

/**
 * A row for `op` with nothing chosen yet — except the relative-date operators, which start at a
 * useful default so choosing one already filters: "in the last 14 days", "in period: this week"
 * (in `timeZone`, the browser's, captured now and saved with the condition).
 */
export function freshCondition(field: string, op: FilterOperator, timeZone: string): FilterCondition {
  if (takesHistory(op)) return { field, op, value: freshHistoryValue(op, '') };
  if (takesList(op)) return { field, op, values: [] };
  if (takesRange(op)) return { field, op, values: [null, null] };
  if (takesAmount(op)) return { field, op, value: { ...DEFAULT_RELATIVE } };
  if (takesPeriod(op)) return { field, op, value: { period: DEFAULT_PERIOD, timeZone } };
  return { field, op };
}

/**
 * The row after its operator changes, rebuilt with only the key the new operator takes. What
 * still means the same thing carries over: the list between is any of / is none of, the amount
 * between in the last / more than … ago, a typed value between the scalar operators, and a smart
 * sprint value between is / is not. Everything else starts over (freshCondition).
 */
export function switchOperator(c: FilterCondition, nextOp: FilterOperator, timeZone: string): FilterCondition {
  const { field, op } = c;
  if (takesHistory(nextOp)) {
    // The field carries over between history operators; a window means the same under
    // "changed" and "number of changes".
    const prev = takesHistory(op) ? readHistoryValue(op, c.value) : undefined;
    const next = freshHistoryValue(nextOp, prev?.historyField ?? '');
    if (prev?.during && nextOp !== 'Was') next.during = { ...prev.during };
    return { field, op: nextOp, value: next };
  }
  if (takesList(nextOp)) return { field, op: nextOp, values: takesList(op) ? [...(c.values ?? [])] : [] };
  if (takesAmount(nextOp) && isRelativeAmount(c.value)) return { field, op: nextOp, value: { ...c.value } };
  if (takesNoValue(nextOp) || takesRange(nextOp) || takesAmount(nextOp) || takesPeriod(nextOp)) {
    return freshCondition(field, nextOp, timeZone);
  }
  const keep = isScalar(c.value) || (isSmartValue(c.value) && (nextOp === 'Eq' || nextOp === 'Neq'));
  return { field, op: nextOp, value: keep ? c.value : '' };
}

// ---- the URL form ----

function stripCondition(c: EditorCondition): FilterCondition {
  const out: FilterCondition = { field: c.field, op: c.op };
  if (c.value !== undefined) out.value = c.value;
  if (c.values !== undefined) out.values = [...c.values];
  if (c.disabled) out.disabled = true;
  return out;
}

function stripGroup(g: EditorGroup): FilterGroup {
  const out: FilterGroup = { logic: g.logic, conditions: g.conditions.map(stripCondition) };
  if (g.groups.length > 0) out.groups = g.groups.map(stripGroup);
  if (g.disabled) out.disabled = true;
  return out;
}

/**
 * The tree as the URL keeps it: every part, disabled and unfinished ones included, so a reload or
 * a shared link restores exactly what was on screen. Ids stripped; null when there is nothing.
 */
export function toUrlJson(root: EditorGroup): string | null {
  return isEmptyTree(root) ? null : JSON.stringify(stripGroup(root));
}

const isRecord = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

const notNull = <T>(x: T | null): x is T => x !== null;

/** Fields with no empty operators at all — see the operators table in the design spec. */
const NO_EMPTY_OPS: ReadonlySet<string> = new Set(['jiraKey']);

/**
 * A `value` in the shape its operator takes, as a fresh object in canonical key order; anything
 * else is dropped, leaving the row unfinished — kept on screen, never sent.
 */
function readValue(op: FilterOperator, raw: unknown): ConditionValue | undefined {
  if (takesHistory(op)) return readHistoryValue(op, raw);
  if (takesAmount(op)) return readRelativeAmount(raw);
  if (takesPeriod(op)) return readPeriod(raw);
  if (typeof raw === 'string' || typeof raw === 'number' || raw === null) return raw;
  // Only is / is not take an object as `value`: a smart sprint value.
  return op === 'Eq' || op === 'Neq' ? readSmart(raw) : undefined;
}

/** `values` as its operator takes them: exactly [from, to] for Between; else names, numbers, (Empty) and smart values. */
function readValues(op: FilterOperator, raw: unknown): ListValue[] | undefined {
  // A history condition keeps its lists inside `value`; a stray `values` is not part of it.
  if (takesHistory(op) || !Array.isArray(raw)) return undefined;
  if (takesRange(op)) {
    return raw.length === 2
      ? raw.map((x): ListValue => (typeof x === 'string' || typeof x === 'number' ? x : null))
      : undefined;
  }
  const out: ListValue[] = [];
  for (const x of raw) {
    if (typeof x === 'string' || typeof x === 'number' || x === null) out.push(x);
    else {
      const smart = readSmart(x);
      if (smart) out.push(smart);
    }
  }
  return out;
}

function readCondition(v: unknown): EditorCondition | null {
  if (!isRecord(v) || typeof v.field !== 'string' || typeof v.op !== 'string') return null;
  if (!isKnownOperator(v.op)) return null;
  const c: EditorCondition = { id: newId(), field: v.field, op: v.op as FilterOperator };
  const value = readValue(c.op, v.value);
  if (value !== undefined) c.value = value;
  const values = readValues(c.op, v.values);
  if (values !== undefined) c.values = values;
  if (v.disabled === true) c.disabled = true;

  // A hand-edited (or hand-crafted) link can carry `value: null` on an operator that does not
  // mean (Empty) on the wire — Eq/Neq are the only ones where it does. Normalise at parse time
  // rather than trusting it through: Eq/Neq become the is-empty/is-not-empty operator they mean;
  // any other operator just has the null dropped, leaving the condition incomplete (pruned, never
  // sent) rather than silently kept with a value its operator cannot use. jiraKey has no empty
  // operators at all (see the operators table), so it is never turned into IsNull — its null is
  // always just dropped, incomplete.
  if (c.value === null) {
    if (!NO_EMPTY_OPS.has(c.field) && c.op === 'Eq') c.op = 'IsNull';
    else if (!NO_EMPTY_OPS.has(c.field) && c.op === 'Neq') c.op = 'IsNotNull';
    delete c.value;
  }
  return c;
}

/** Far past the server's MaxDepth: a hostile link cannot recurse the tab to death. */
const MAX_PARSE_DEPTH = 10;

function readGroup(v: unknown, depth: number): EditorGroup | null {
  if (!isRecord(v) || depth > MAX_PARSE_DEPTH) return null;
  return {
    id: newId(),
    logic: v.logic === 'Or' ? 'Or' : 'And',
    conditions: Array.isArray(v.conditions) ? v.conditions.map(readCondition).filter(notNull) : [],
    groups: Array.isArray(v.groups) ? v.groups.map((g) => readGroup(g, depth + 1)).filter(notNull) : [],
    ...(v.disabled === true ? { disabled: true } : {}),
  };
}

/**
 * Reads `filter=` (and, for old links, `logic=`). An object is the tree. An array is the flat
 * list links carried before groups existed, joined by the old `logic` param. Anything else —
 * including JSON a chat client truncated — is no filter: a broken link must never blank the page.
 */
export function parseFilterParam(raw: string | null, legacyLogic: string | null): EditorGroup {
  if (!raw) return emptyRoot();
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return emptyRoot();
  }
  if (Array.isArray(parsed)) {
    return {
      ...emptyRoot(),
      logic: legacyLogic === 'Or' ? 'Or' : 'And',
      conditions: parsed.map(readCondition).filter(notNull),
    };
  }
  return readGroup(parsed, 1) ?? emptyRoot();
}
