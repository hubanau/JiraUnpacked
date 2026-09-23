import { describe, expect, it } from 'vitest';
import {
  blankGroup, cloneGroup, countEnabled, emptyRoot, insertAfter, isCompleteCondition,
  parseFilterParam, pruneTree, toUrlJson, toggleDisabled, withIdentityOf,
  type EditorCondition, type EditorGroup,
} from './filterTree';
import { OPERATOR_LABELS } from './filterCatalogHelpers';

const cond = (id: string, rest: Omit<EditorCondition, 'id'>): EditorCondition => ({ id, ...rest });
const group = (id: string, rest: Partial<EditorGroup> = {}): EditorGroup =>
  ({ id, logic: 'And', conditions: [], groups: [], ...rest });

describe('pruneTree', () => {
  it('sends only enabled, finished conditions, without ids', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'status', op: 'Eq', value: 'Done' }),
        cond('b', { field: 'status', op: 'Eq', value: '' }),
        cond('c', { field: 'assignee', op: 'In', values: [] }),
        cond('d', { field: 'priority', op: 'Eq', value: 'High', disabled: true }),
      ],
    });

    expect(pruneTree(tree)).toEqual({ logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] });
  });

  it('drops disabled groups and groups that pruning empties, at any depth', () => {
    const tree = group('r', {
      logic: 'Or',
      conditions: [cond('a', { field: 'summary', op: 'Contains', value: 'x' })],
      groups: [
        group('g1', { disabled: true, conditions: [cond('b', { field: 'status', op: 'IsNull' })] }),
        group('g2', { conditions: [cond('c', { field: 'status', op: 'Eq', value: '' })] }),
        group('g3', { groups: [group('g4', { conditions: [cond('d', { field: 'status', op: 'IsNotNull' })] })] }),
      ],
    });

    expect(pruneTree(tree)).toEqual({
      logic: 'Or',
      conditions: [{ field: 'summary', op: 'Contains', value: 'x' }],
      groups: [{ logic: 'And', groups: [{ logic: 'And', conditions: [{ field: 'status', op: 'IsNotNull' }] }] }],
    });
  });

  it('is undefined when nothing is left, so no filter is sent at all', () => {
    expect(pruneTree(emptyRoot())).toBeUndefined();
    expect(pruneTree(group('r', { disabled: true, conditions: [cond('a', { field: 'status', op: 'IsNull' })] })))
      .toBeUndefined();
    expect(pruneTree(group('r', { conditions: [cond('a', { field: 'status', op: 'IsNull', disabled: true })] })))
      .toBeUndefined();
  });

  it('turns (Empty) on is / is not into is empty / is not empty', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'sprint', op: 'Eq', value: null }),
        cond('b', { field: 'team', op: 'Neq', value: null }),
      ],
    });

    expect(pruneTree(tree)?.conditions).toEqual([
      { field: 'sprint', op: 'IsNull' },
      { field: 'team', op: 'IsNotNull' },
    ]);
  });

  it('keeps (Empty) inside an is-any-of list', () => {
    const tree = group('r', { conditions: [cond('a', { field: 'sprint', op: 'In', values: ['S1', null] })] });

    expect(pruneTree(tree)?.conditions).toEqual([{ field: 'sprint', op: 'In', values: ['S1', null] }]);
  });

  it('sends only the key its operator takes', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'status', op: 'IsNull', value: 'stale' }),
        cond('b', { field: 'status', op: 'In', value: 'stale', values: ['Done'] }),
      ],
    });

    expect(pruneTree(tree)?.conditions).toEqual([
      { field: 'status', op: 'IsNull' },
      { field: 'status', op: 'In', values: ['Done'] },
    ]);
  });

  it('leaves out a field the catalog no longer has', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'retired', op: 'Eq', value: 'x' }),
        cond('b', { field: 'status', op: 'Eq', value: 'Done' }),
      ],
    });

    expect(pruneTree(tree, new Set(['status']))?.conditions)
      .toEqual([{ field: 'status', op: 'Eq', value: 'Done' }]);
    // Before the catalog has loaded there is nothing to check against; the server is the backstop.
    expect(pruneTree(tree)?.conditions).toHaveLength(2);
  });
});

describe('countEnabled', () => {
  it('counts like the server: whole tree, root included, disabled subtrees not at all', () => {
    const tree = group('r', {
      conditions: [cond('a', { field: 'status', op: 'IsNull' }), cond('b', { field: 'status', op: 'IsNull', disabled: true })],
      groups: [
        group('g1', { conditions: [cond('c', { field: 'status', op: 'IsNull' })] }),
        group('g2', { disabled: true, conditions: [cond('d', { field: 'status', op: 'IsNull' })], groups: [group('g3')] }),
      ],
    });

    expect(countEnabled(tree)).toEqual({ conditions: 2, groups: 2 });
  });
});

describe('copying and identity', () => {
  it('cloneGroup deep-copies with fresh ids and no shared arrays', () => {
    const original = group('g', {
      conditions: [cond('a', { field: 'status', op: 'In', values: ['Done'] })],
      groups: [blankGroup()],
    });

    const copy = cloneGroup(original);

    expect(copy.id).not.toBe(original.id);
    expect(copy.conditions[0].id).not.toBe('a');
    expect(copy.groups[0].id).not.toBe(original.groups[0].id);
    expect(copy.conditions[0].values).not.toBe(original.conditions[0].values);
    expect(copy.conditions[0].values).toEqual(['Done']);
  });

  it('toggleDisabled switches a node off and back on without leaving a stray key', () => {
    const off = toggleDisabled(cond('a', { field: 'status', op: 'IsNull' }));
    expect(off.disabled).toBe(true);
    expect('disabled' in toggleDisabled(off)).toBe(false);
  });

  it('withIdentityOf puts a rebuilt row back with its id and on/off state', () => {
    const row = cond('a', { field: 'status', op: 'Eq', value: 'Done', disabled: true });
    expect(withIdentityOf(row, { field: 'status', op: 'Neq', value: 'Done' }))
      .toEqual({ id: 'a', field: 'status', op: 'Neq', value: 'Done', disabled: true });
  });

  it('insertAfter places the copy directly after the original', () => {
    expect(insertAfter([{ id: 'a' }, { id: 'b' }], 'a', { id: 'c' }).map((x) => x.id)).toEqual(['a', 'c', 'b']);
  });
});

describe('toUrlJson / parseFilterParam', () => {
  it('keeps disabled parts in the URL and strips ids', () => {
    const tree = group('r', {
      conditions: [cond('a', { field: 'status', op: 'Eq', value: 'Done', disabled: true })],
      groups: [group('g', { logic: 'Or', conditions: [cond('b', { field: 'sprint', op: 'In', values: [null] })] })],
    });

    const json = toUrlJson(tree)!;

    expect(json).not.toContain('"id"');
    expect(JSON.parse(json)).toEqual({
      logic: 'And',
      conditions: [{ field: 'status', op: 'Eq', value: 'Done', disabled: true }],
      groups: [{ logic: 'Or', conditions: [{ field: 'sprint', op: 'In', values: [null] }] }],
    });
  });

  it('writes nothing for an empty tree', () => {
    expect(toUrlJson(emptyRoot())).toBeNull();
  });

  it('round-trips a tree through the URL form', () => {
    const json = '{"logic":"Or","conditions":[{"field":"status","op":"NotIn","values":["Done",null]}],'
      + '"groups":[{"logic":"And","conditions":[{"field":"storyPoints","op":"Gte","value":3}],"disabled":true}]}';

    expect(toUrlJson(parseFilterParam(json, null))).toBe(json);
  });

  it('reads a legacy flat list with its old logic param', () => {
    const tree = parseFilterParam('[{"field":"status","op":"Eq","value":"Backlog"}]', 'Or');

    expect(tree.logic).toBe('Or');
    expect(tree.conditions).toEqual([expect.objectContaining({ field: 'status', op: 'Eq', value: 'Backlog' })]);
    expect(tree.groups).toEqual([]);
  });

  it('treats malformed or truncated JSON as no filter', () => {
    for (const raw of ['{not-json', '"a string"', '42', 'null']) {
      const tree = parseFilterParam(raw, null);
      expect(tree.conditions).toEqual([]);
      expect(tree.groups).toEqual([]);
    }
  });

  it('drops parts that are not conditions rather than failing the whole link', () => {
    const tree = parseFilterParam(
      '{"logic":"And","conditions":[{"field":"status"},{"op":"Eq"},{"field":"status","op":"Bogus"},'
      + '{"field":"status","op":"IsNull"}],"groups":[5]}', null);

    expect(tree.conditions.map((c) => c.op)).toEqual(['IsNull']);
    expect(tree.groups).toEqual([]);
  });

  it('gives every parsed node its own id', () => {
    const tree = parseFilterParam(
      '{"logic":"And","conditions":[{"field":"status","op":"IsNull"},{"field":"status","op":"IsNull"}]}', null);

    expect(new Set([tree.id, ...tree.conditions.map((c) => c.id)]).size).toBe(3);
  });

  it('accepts every operator the editor has a label for, including one added after this test was written', () => {
    // The accepted-operator check is a live lookup against OPERATOR_LABELS (a Record<FilterOperator,
    // string>, exhaustive by construction) rather than a list captured once at module load, so a
    // label the catalog gains later is honoured without a matching edit to filterTree.ts. Proven
    // here by adding one at runtime, past what FilterOperator itself lists at compile time.
    const labels = OPERATOR_LABELS as Record<string, string>;
    labels.Bogus = 'bogus';
    try {
      const tree = parseFilterParam('{"logic":"And","conditions":[{"field":"status","op":"Bogus"}]}', null);
      expect(tree.conditions.map((c) => c.op)).toEqual(['Bogus']);
    } finally {
      delete labels.Bogus;
    }
  });

  describe('null-value normalisation', () => {
    it('turns a hand-edited Eq null into IsNull', () => {
      const tree = parseFilterParam('{"logic":"And","conditions":[{"field":"sprint","op":"Eq","value":null}]}', null);

      expect(tree.conditions[0]).toMatchObject({ field: 'sprint', op: 'IsNull' });
      expect('value' in tree.conditions[0]).toBe(false);
    });

    it('turns a hand-edited Neq null into IsNotNull', () => {
      const tree = parseFilterParam('{"logic":"And","conditions":[{"field":"team","op":"Neq","value":null}]}', null);

      expect(tree.conditions[0]).toMatchObject({ field: 'team', op: 'IsNotNull' });
      expect('value' in tree.conditions[0]).toBe(false);
    });

    it('drops a null value on any other operator, leaving the condition incomplete rather than sent', () => {
      const tree = parseFilterParam(
        '{"logic":"And","conditions":[{"field":"summary","op":"Contains","value":null}]}', null);

      expect(tree.conditions[0].op).toBe('Contains');
      expect('value' in tree.conditions[0]).toBe(false);
      expect(isCompleteCondition(tree.conditions[0])).toBe(false);
    });

    it('never turns a jiraKey null into IsNull, since jiraKey has no empty ops', () => {
      const tree = parseFilterParam('{"logic":"And","conditions":[{"field":"jiraKey","op":"Eq","value":null}]}', null);

      expect(tree.conditions[0].op).toBe('Eq');
      expect('value' in tree.conditions[0]).toBe(false);
      expect(isCompleteCondition(tree.conditions[0])).toBe(false);
    });

    it('never turns a jiraKey null into IsNotNull either', () => {
      const tree = parseFilterParam('{"logic":"And","conditions":[{"field":"jiraKey","op":"Neq","value":null}]}', null);

      expect(tree.conditions[0].op).toBe('Neq');
      expect('value' in tree.conditions[0]).toBe(false);
      expect(isCompleteCondition(tree.conditions[0])).toBe(false);
    });
  });
});
