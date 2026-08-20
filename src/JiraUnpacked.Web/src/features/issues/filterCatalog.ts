import type { FilterOperator } from '../../api/issues';

/**
 * A mirror of FilterFields.All in src/JiraUnpacked.Infrastructure/Reporting/
 * Filtering/FilterField.cs. No endpoint exposes that catalog, so the UI keeps
 * its own copy to render valid operators and the right value input.
 *
 * This mirror is a CONVENIENCE, never an authority. The server validates every
 * request and its 400 must always reach the user verbatim. If a field is added
 * server-side and not here it is merely missing from the UI; if one is removed
 * or renamed, the UI offers a field the server rejects — visible and harmless.
 * filterCatalog.test.ts asserts the matrix so drift fails there first.
 */
export type FieldType = 'DictName' | 'Decimal' | 'DateTime' | 'Text' | 'ParentRef';

export interface CatalogEntry {
  type: FieldType;
  operators: readonly FilterOperator[];
}

const dict: CatalogEntry = { type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] };

export const FIELD_CATALOG: Record<string, CatalogEntry> = {
  status: dict,
  issueType: dict,
  priority: dict,
  assignee: dict,
  sprint: dict,
  team: dict,
  storyPoints: { type: 'Decimal', operators: ['Eq', 'Neq', 'Lt', 'Lte', 'Gt', 'Gte', 'IsNull'] },
  createdAt: { type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
  updatedAt: { type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
  summary: { type: 'Text', operators: ['Contains', 'StartsWith', 'IsNull'] },
  jiraKey: { type: 'Text', operators: ['Eq', 'Contains', 'StartsWith'] },
  parent: { type: 'ParentRef', operators: ['Eq', 'IsNull'] },
};

/** Canonical field names, in the order the field picker lists them. */
export const FILTER_FIELDS: readonly string[] = Object.keys(FIELD_CATALOG);

/** FR-E4's guards, mirrored so the UI can block before the server has to. */
export const MAX_CONDITIONS = 20;
export const MAX_IN_VALUES = 100;

export const operatorsFor = (field: string): readonly FilterOperator[] =>
  FIELD_CATALOG[field]?.operators ?? [];

export const typeFor = (field: string): FieldType | undefined => FIELD_CATALOG[field]?.type;

export const isOperatorAllowed = (field: string, op: FilterOperator): boolean =>
  operatorsFor(field).includes(op);

/**
 * When the user changes a row's field, the previously chosen operator may not
 * be valid for the new field. Returning the new field's first operator keeps
 * the row in a submittable state instead of silently sending an invalid pair.
 */
export const resetOperatorIfDisallowed = (field: string, op: FilterOperator): FilterOperator =>
  isOperatorAllowed(field, op) ? op : (operatorsFor(field)[0] ?? 'Eq');
