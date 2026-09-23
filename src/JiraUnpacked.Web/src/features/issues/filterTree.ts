import type { FilterCondition, FilterGroup, FilterLogic, FilterOperator } from '../../api/issues';
import { OPERATOR_LABELS } from './filterCatalogHelpers';

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

// ---- sending ----

/**
 * A condition the user has started but not finished is unfinished, not invalid: it is left out
 * of the request rather than sent to earn a 400 mid-edit. A null `value` is (Empty) and complete.
 */
export function isCompleteCondition(c: FilterCondition): boolean {
  if (takesNoValue(c.op)) return true;
  if (takesList(c.op)) return (c.values?.length ?? 0) > 0;
  return c.value !== undefined && c.value !== '';
}

/** One condition as the server takes it: only its operator's key, (Empty) on is/is not as is empty/is not empty. */
export function toWireCondition(c: FilterCondition): FilterCondition {
  if ((c.op === 'Eq' || c.op === 'Neq') && c.value === null) {
    return { field: c.field, op: c.op === 'Eq' ? 'IsNull' : 'IsNotNull' };
  }
  if (takesNoValue(c.op)) return { field: c.field, op: c.op };
  if (takesList(c.op)) return { field: c.field, op: c.op, values: [...(c.values ?? [])] };
  return { field: c.field, op: c.op, value: c.value };
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

// ---- counting (matches the server's budget) ----

/**
 * Enabled conditions and groups, counted as the server counts them: over the whole tree, the
 * root included as a group, and a disabled node — with everything under it — not at all.
 */
export function countEnabled(g: EditorGroup): TreeCounts {
  if (g.disabled) return { conditions: 0, groups: 0 };
  let conditions = g.conditions.filter((c) => !c.disabled).length;
  let groups = 1;
  for (const child of g.groups) {
    const sub = countEnabled(child);
    conditions += sub.conditions;
    groups += sub.groups;
  }
  return { conditions, groups };
}

// ---- editing ----

export const cloneCondition = (c: EditorCondition): EditorCondition => ({
  ...c, id: newId(), ...(c.values ? { values: [...c.values] } : {}),
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

function readCondition(v: unknown): EditorCondition | null {
  if (!isRecord(v) || typeof v.field !== 'string' || typeof v.op !== 'string') return null;
  if (!isKnownOperator(v.op)) return null;
  const c: EditorCondition = { id: newId(), field: v.field, op: v.op as FilterOperator };
  if (typeof v.value === 'string' || typeof v.value === 'number' || v.value === null) c.value = v.value;
  if (Array.isArray(v.values)) {
    c.values = v.values.filter((x): x is string | number | null =>
      typeof x === 'string' || typeof x === 'number' || x === null);
  }
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
