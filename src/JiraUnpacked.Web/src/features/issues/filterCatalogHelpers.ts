import type { FieldType, FilterCatalog, FilterOperator } from '../../api/issues';

/**
 * Pure helpers over a fetched FilterCatalog. This is deliberately NOT a data
 * mirror — every value comes from the `catalog` argument, sourced at runtime
 * from GET /api/filter-catalog (see FilterBuilder), rather than from a
 * hard-coded copy of the server's allow-list. Only the logic that used to
 * live in filterCatalog.ts survives; its hard-coded data does not.
 */

const fieldEntry = (catalog: FilterCatalog, field: string) =>
  catalog.fields.find((f) => f.name === field);

/** Canonical field names, in the order the field picker should list them. */
export const fieldNames = (catalog: FilterCatalog): string[] => catalog.fields.map((f) => f.name);

export const operatorsFor = (catalog: FilterCatalog, field: string): readonly FilterOperator[] =>
  fieldEntry(catalog, field)?.operators ?? [];

export const typeFor = (catalog: FilterCatalog, field: string): FieldType | undefined =>
  fieldEntry(catalog, field)?.type;

export const isOperatorAllowed = (catalog: FilterCatalog, field: string, op: FilterOperator): boolean =>
  operatorsFor(catalog, field).includes(op);

/** Fields whose values come from this project's value list: dictionaries, and status category. */
export const isDictionaryField = (catalog: FilterCatalog, field: string): boolean => {
  const type = typeFor(catalog, field);
  return type === 'DictName' || type === 'StatusCategory';
};

/**
 * When the user changes a row's field, the previously chosen operator may not
 * be valid for the new field. Returning the new field's first operator keeps
 * the row in a submittable state instead of silently sending an invalid pair.
 */
export const resetOperatorIfDisallowed = (
  catalog: FilterCatalog, field: string, op: FilterOperator,
): FilterOperator =>
  isOperatorAllowed(catalog, field, op) ? op : (operatorsFor(catalog, field)[0] ?? 'Eq');

/** What the editor calls each operator. The wire keeps the enum names. */
export const OPERATOR_LABELS: Record<FilterOperator, string> = {
  Eq: 'is',
  Neq: 'is not',
  In: 'is any of',
  NotIn: 'is none of',
  IsNull: 'is empty',
  IsNotNull: 'is not empty',
  Lt: '<',
  Lte: '≤',
  Gt: '>',
  Gte: '≥',
  Contains: 'contains',
  NotContains: 'does not contain',
  StartsWith: 'starts with',
  Between: 'between',
  InLast: 'in the last',
  NotInLast: 'more than … ago',
  InPeriod: 'in period',
  Was: 'was',
  Changed: 'changed',
  ChangeCount: 'number of changes',
};

export const operatorLabel = (op: FilterOperator): string => OPERATOR_LABELS[op] ?? op;
