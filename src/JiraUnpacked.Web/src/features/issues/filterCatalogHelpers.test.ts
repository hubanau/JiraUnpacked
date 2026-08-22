import { describe, expect, it } from 'vitest';
import type { FilterCatalog } from '../../api/issues';
import {
  fieldNames,
  isDictionaryField,
  isOperatorAllowed,
  operatorsFor,
  resetOperatorIfDisallowed,
  typeFor,
} from './filterCatalogHelpers';

/**
 * A small fixture, not the full server catalog — these helpers are pure
 * functions of whatever `FilterCatalog` they are handed, so exercising them
 * against a hand-built fixture (rather than FilterBuilder.test.tsx's larger
 * CATALOG) is exactly what unit-tests a "pure helpers over a fetched
 * catalog" module the way the mock-mirror them file (filterCatalog.test.ts,
 * deleted with the client-side mirror it covered) used to.
 */
const CATALOG: FilterCatalog = {
  fields: [
    { name: 'status', type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] },
    { name: 'summary', type: 'Text', operators: ['Contains', 'StartsWith', 'IsNull'] },
    { name: 'createdAt', type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
  ],
  maxConditions: 20,
  maxInValues: 100,
  maxGroups: 10,
  maxDepth: 3,
};

describe('fieldNames', () => {
  it('lists every field name in catalog order', () => {
    expect(fieldNames(CATALOG)).toEqual(['status', 'summary', 'createdAt']);
  });
});

describe('operatorsFor', () => {
  it('returns the operators for a known field', () => {
    expect(operatorsFor(CATALOG, 'status')).toEqual(['Eq', 'Neq', 'In', 'IsNull']);
  });

  it('returns an empty array for an unknown field, rather than throwing', () => {
    expect(operatorsFor(CATALOG, 'notAField')).toEqual([]);
  });
});

describe('typeFor', () => {
  it('returns the type for a known field', () => {
    expect(typeFor(CATALOG, 'summary')).toBe('Text');
  });

  it('returns undefined for an unknown field, rather than throwing', () => {
    expect(typeFor(CATALOG, 'notAField')).toBeUndefined();
  });
});

describe('isOperatorAllowed', () => {
  // No production consumer uses this directly today — resetOperatorIfDisallowed
  // is the only caller, and it is exercised separately below — but it is
  // exported as part of this module's public surface (the natural
  // is-it-allowed counterpart to operatorsFor, the same relationship
  // isDictionaryField has to typeFor), so it is tested directly rather than
  // deleted: it is small, already correct, and covering it here is cheaper
  // and lower-risk than removing an intentionally-exported pure predicate.
  it('is true for an operator the field allows', () => {
    expect(isOperatorAllowed(CATALOG, 'status', 'In')).toBe(true);
  });

  it('is false for an operator the field does not allow', () => {
    expect(isOperatorAllowed(CATALOG, 'status', 'Contains')).toBe(false);
  });

  it('is false for an unknown field, rather than throwing', () => {
    expect(isOperatorAllowed(CATALOG, 'notAField', 'Eq')).toBe(false);
  });
});

describe('isDictionaryField', () => {
  it('is true for a DictName field', () => {
    expect(isDictionaryField(CATALOG, 'status')).toBe(true);
  });

  it('is false for a non-DictName field', () => {
    expect(isDictionaryField(CATALOG, 'summary')).toBe(false);
  });

  it('is false for an unknown field, rather than throwing', () => {
    expect(isDictionaryField(CATALOG, 'notAField')).toBe(false);
  });
});

describe('resetOperatorIfDisallowed', () => {
  it('keeps the current operator when the new field still allows it', () => {
    // Both status and summary allow IsNull.
    expect(resetOperatorIfDisallowed(CATALOG, 'summary', 'IsNull')).toBe('IsNull');
  });

  it("falls back to the new field's first operator when the current one is disallowed", () => {
    // createdAt does not allow Eq; its first operator is Lt.
    expect(resetOperatorIfDisallowed(CATALOG, 'createdAt', 'Eq')).toBe('Lt');
  });

  it("falls back to 'Eq' for an unknown field with no operators of its own", () => {
    expect(resetOperatorIfDisallowed(CATALOG, 'notAField', 'Contains')).toBe('Eq');
  });
});
