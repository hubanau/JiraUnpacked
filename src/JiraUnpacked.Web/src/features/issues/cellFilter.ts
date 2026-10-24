import type { FilterCatalog, FilterCondition, IssueListItem } from '../../api/issues';
import { countEnabled, newId, toWireCondition, type EditorCondition, type EditorGroup } from './filterTree';

/** A cell's value as a filter takes it: a name, a number (Points), the parent's Jira id — or null for an empty cell. */
export type CellValue = string | number | null;

export type CellFilterMode = 'include' | 'exclude';

/**
 * The columns whose cells offer "Filter to" / "Exclude", and each cell's value. Column ids are the
 * filter's own field names (see columns.ts), so the column IS the field.
 */
export const CELL_FILTER_VALUES: Readonly<Record<string, (item: IssueListItem) => CellValue>> = {
  status: (i) => i.status,
  statusCategory: (i) => i.statusCategory,
  issueType: (i) => i.issueType,
  priority: (i) => i.priority,
  assignee: (i) => i.assignee,
  sprint: (i) => i.sprint,
  team: (i) => i.team,
  storyPoints: (i) => i.storyPoints,
  parent: (i) => i.parentJiraIssueId,
};

/** "Filter to" a value is `is`, "Exclude" it `is not`; on an empty cell, is empty / is not empty. */
export function cellCondition(field: string, value: CellValue, mode: CellFilterMode): FilterCondition {
  if (value === null) return { field, op: mode === 'include' ? 'IsNull' : 'IsNotNull' };
  return { field, op: mode === 'include' ? 'Eq' : 'Neq', value };
}

/** Nesting depth, the root at 1, counting switched-off groups too: switched back on, they must still fit. */
export function treeDepth(g: EditorGroup): number {
  return 1 + g.groups.reduce((deepest, child) => Math.max(deepest, treeDepth(child)), 0);
}

/**
 * Adding straight to the root would WIDEN an Any root that already holds something, and change
 * nothing under a switched-off root — so then the root is wrapped first: a new All holding the old
 * root as a group, plus the new condition. A click on a cell always narrows the list.
 */
export function needsWrap(root: EditorGroup): boolean {
  if (root.disabled) return true;
  return root.logic === 'Or'
    && (root.conditions.some((c) => !c.disabled) || root.groups.some((g) => !g.disabled));
}

/** Why a cell filter cannot be added now — the builder's own limit hints — or undefined when it fits. */
export function cellFilterHint(root: EditorGroup, catalog: FilterCatalog): string | undefined {
  const counts = countEnabled(root);
  if (counts.conditions + 1 > catalog.maxConditions) return `Limit: ${catalog.maxConditions} conditions`;
  if (needsWrap(root)) {
    if (counts.groups + 1 > catalog.maxGroups) return `Limit: ${catalog.maxGroups} groups`;
    if (treeDepth(root) + 1 > catalog.maxDepth) return `Limit: ${catalog.maxDepth} levels of groups`;
  }
  return undefined;
}

const sameCondition = (a: FilterCondition, b: FilterCondition): boolean =>
  JSON.stringify(toWireCondition(a)) === JSON.stringify(toWireCondition(b));

/**
 * The tree with `condition` added to its root, wrapping first where needsWrap says so. The SAME
 * tree comes back when an identical condition is already switched on in the root, so a second
 * click adds nothing. Callers check cellFilterHint first.
 */
export function addCellCondition(root: EditorGroup, condition: FilterCondition): EditorGroup {
  const row: EditorCondition = { ...condition, id: newId() };
  if (needsWrap(root)) return { id: newId(), logic: 'And', conditions: [row], groups: [root] };
  if (root.conditions.some((c) => !c.disabled && sameCondition(c, condition))) return root;
  return { ...root, conditions: [...root.conditions, row] };
}
