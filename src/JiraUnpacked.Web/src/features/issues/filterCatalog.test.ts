import { describe, expect, it } from 'vitest';
import {
  FIELD_CATALOG, FILTER_FIELDS, MAX_CONDITIONS, MAX_IN_VALUES,
  isOperatorAllowed, operatorsFor, resetOperatorIfDisallowed, typeFor,
} from './filterCatalog';

// This matrix is a mirror of FilterFields.All in the .NET Infrastructure
// project. Asserting it field by field means server-side drift fails here
// loudly instead of surfacing as a 400 in production.
const EXPECTED: Record<string, { type: string; ops: string[] }> = {
  status:      { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  issueType:   { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  priority:    { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  assignee:    { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  sprint:      { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  team:        { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  storyPoints: { type: 'Decimal',   ops: ['Eq', 'Neq', 'Lt', 'Lte', 'Gt', 'Gte', 'IsNull'] },
  createdAt:   { type: 'DateTime',  ops: ['Lt', 'Lte', 'Gt', 'Gte'] },
  updatedAt:   { type: 'DateTime',  ops: ['Lt', 'Lte', 'Gt', 'Gte'] },
  summary:     { type: 'Text',      ops: ['Contains', 'StartsWith', 'IsNull'] },
  jiraKey:     { type: 'Text',      ops: ['Eq', 'Contains', 'StartsWith'] },
  parent:      { type: 'ParentRef', ops: ['Eq', 'IsNull'] },
};

describe('FIELD_CATALOG mirrors the server allow-list', () => {
  it('offers exactly the twelve catalog fields and no others', () => {
    expect([...FILTER_FIELDS].sort()).toEqual(Object.keys(EXPECTED).sort());
  });

  it.each(Object.keys(EXPECTED))('matches the server type and operators for %s', (field) => {
    expect(typeFor(field)).toBe(EXPECTED[field].type);
    expect([...operatorsFor(field)]).toEqual(EXPECTED[field].ops);
  });

  it('exposes the FR-E4 guard limits', () => {
    expect(MAX_CONDITIONS).toBe(20);
    expect(MAX_IN_VALUES).toBe(100);
  });

  it('reports an unknown field as having no operators', () => {
    expect(operatorsFor('nope')).toEqual([]);
    expect(isOperatorAllowed('nope', 'Eq')).toBe(false);
  });
});

describe('resetOperatorIfDisallowed', () => {
  it('keeps an operator the new field still allows', () => {
    // Both status and issueType allow Eq.
    expect(resetOperatorIfDisallowed('issueType', 'Eq')).toBe('Eq');
  });

  it('falls back to the new field’s first operator when the old one is invalid', () => {
    // createdAt does not allow Contains; its first operator is Lt.
    expect(resetOperatorIfDisallowed('createdAt', 'Contains')).toBe('Lt');
  });

  it('falls back for an unknown field rather than throwing', () => {
    expect(resetOperatorIfDisallowed('nope', 'Eq')).toBe('Eq');
  });
});

describe('catalog entry shape', () => {
  it('gives every field a non-empty operator list', () => {
    for (const field of FILTER_FIELDS) {
      expect(operatorsFor(field).length).toBeGreaterThan(0);
      expect(FIELD_CATALOG[field]).toBeDefined();
    }
  });
});
