import { describe, expect, it } from 'vitest';
import type { HistoryValue } from '../../api/issues';
import {
  blankGroup, cloneCondition, cloneGroup, countEnabled, emptyRoot, freshCondition, insertAfter,
  isCompleteCondition, parseFilterParam, pruneTree, switchOperator, toSavable, toUrlJson,
  toWireCondition, toggleDisabled, withIdentityOf,
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

    expect(countEnabled(tree)).toEqual({ conditions: 2, groups: 2, history: 0 });
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

describe('toSavable', () => {
  it('keeps disabled parts, drops unfinished rows and the groups they empty, and strips ids', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'status', op: 'Eq', value: 'Done' }),
        cond('b', { field: 'status', op: 'Eq', value: '' }),
        cond('c', { field: 'priority', op: 'Eq', value: 'High', disabled: true }),
      ],
      groups: [
        group('g1', { logic: 'Or', disabled: true, conditions: [cond('d', { field: 'assignee', op: 'Eq', value: 'Bob' })] }),
        group('g2', { conditions: [cond('e', { field: 'sprint', op: 'In', values: [] })] }),
      ],
    });

    expect(toSavable(tree)).toEqual({
      logic: 'And',
      conditions: [
        { field: 'status', op: 'Eq', value: 'Done' },
        { field: 'priority', op: 'Eq', value: 'High', disabled: true },
      ],
      groups: [{ logic: 'Or', disabled: true, conditions: [{ field: 'assignee', op: 'Eq', value: 'Bob' }] }],
    });
  });

  it('writes (Empty) on is / is not as is empty / is not empty — the only form the server accepts', () => {
    const tree = group('r', { conditions: [cond('a', { field: 'sprint', op: 'Neq', value: null, disabled: true })] });

    expect(toSavable(tree)).toEqual({
      logic: 'And', conditions: [{ field: 'sprint', op: 'IsNotNull', disabled: true }],
    });
  });

  it('saves no filter for an empty tree, or one holding only unfinished rows', () => {
    expect(toSavable(emptyRoot())).toBeUndefined();
    expect(toSavable(group('r', { conditions: [cond('a', { field: 'status', op: 'In', values: [] })] }))).toBeUndefined();
  });

  it('keeps a switched-off root with its contents', () => {
    const tree = group('r', { disabled: true, conditions: [cond('a', { field: 'summary', op: 'Contains', value: 'x' })] });

    expect(toSavable(tree)).toEqual({
      logic: 'And', disabled: true, conditions: [{ field: 'summary', op: 'Contains', value: 'x' }],
    });
  });
});

describe('object values', () => {
  it('reads each object value from a link in canonical form, and drops shapes its operator cannot use', () => {
    const tree = parseFilterParam(JSON.stringify({
      logic: 'And',
      conditions: [
        { field: 'updatedAt', op: 'InPeriod', value: { timeZone: 'Europe/Warsaw', period: 'thisWeek', extra: 1 } },
        { field: 'updatedAt', op: 'InLast', value: { unit: 'days', amount: 14 } },
        { field: 'sprint', op: 'Eq', value: { smart: 'active' } },
        { field: 'sprint', op: 'In', values: [{ smart: 'current' }, 'Sprint 7', { smart: 'open' }, null] },
        { field: 'createdAt', op: 'Between', values: ['2026-09-01', null] },
        { field: 'updatedAt', op: 'InLast', value: { amount: '14', unit: 'days' } },
        { field: 'updatedAt', op: 'InPeriod', value: { period: 'fortnight' } },
        { field: 'createdAt', op: 'Lt', value: { smart: 'active' } },
        { field: 'createdAt', op: 'Between', values: ['2026-09-01'] },
      ],
    }), null);

    const [period, last, smartEq, smartIn, between, badAmount, badPeriod, badLt, badBetween] = tree.conditions;
    expect(period.value).toEqual({ period: 'thisWeek', timeZone: 'Europe/Warsaw' });
    expect(Object.keys(period.value as object)).toEqual(['period', 'timeZone']);
    expect(last.value).toEqual({ amount: 14, unit: 'days' });
    expect(smartEq.value).toEqual({ smart: 'active' });
    expect(smartIn.values).toEqual(['Sprint 7', { smart: 'open' }, null]);
    expect(between.values).toEqual(['2026-09-01', null]);
    for (const c of [badAmount, badPeriod, badLt, badBetween]) {
      expect(isCompleteCondition(c)).toBe(false);   // kept on screen, never sent
    }
  });

  it('decides completeness per operator', () => {
    const c = (rest: Omit<EditorCondition, 'id'>) => cond('x', rest);
    expect(isCompleteCondition(c({ field: 'createdAt', op: 'Between', values: [null, null] }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'createdAt', op: 'Between', values: ['', null] }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'createdAt', op: 'Between', values: [null, '2026-09-30'] }))).toBe(true);
    expect(isCompleteCondition(c({ field: 'storyPoints', op: 'Between', values: [0, null] }))).toBe(true);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InLast', value: { amount: 0, unit: 'days' } }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InLast', value: { amount: 1.5, unit: 'days' } }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'NotInLast', value: { amount: 1, unit: 'hours' } }))).toBe(true);
    // The server refuses more than 3650: past it the row is unfinished, never a 400.
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InLast', value: { amount: 3650, unit: 'days' } }))).toBe(true);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InLast', value: { amount: 3651, unit: 'days' } }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InPeriod', value: { period: 'today' } }))).toBe(true);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InPeriod' }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'sprint', op: 'Eq', value: { smart: 'active' } }))).toBe(true);
  });

  it('sends an empty Between bound as an open one, and copies object values rather than sharing them', () => {
    expect(toWireCondition({ field: 'createdAt', op: 'Between', values: ['2026-09-01', ''] }))
      .toEqual({ field: 'createdAt', op: 'Between', values: ['2026-09-01', null] });

    const value = { amount: 14, unit: 'days' as const };
    const wire = toWireCondition({ field: 'updatedAt', op: 'InLast', value });
    expect(wire).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } });
    expect(wire.value).not.toBe(value);
  });

  it('round-trips every object value through the URL form', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'updatedAt', op: 'InLast', value: { amount: 3, unit: 'weeks' } }),
        cond('b', { field: 'updatedAt', op: 'InPeriod', value: { period: 'lastMonth', timeZone: 'Europe/Warsaw' } }),
        cond('c', { field: 'storyPoints', op: 'Between', values: [1.5, null] }),
        cond('d', { field: 'sprint', op: 'In', values: [{ smart: 'open' }, null, 'Sprint 7'] }),
        cond('e', { field: 'sprint', op: 'Neq', value: { smart: 'active' } }),
      ],
    });

    expect(pruneTree(parseFilterParam(toUrlJson(tree), null))).toEqual(pruneTree(tree));
  });

  it('copies object values when a condition is duplicated', () => {
    const original = cond('a', { field: 'updatedAt', op: 'InPeriod', value: { period: 'today', timeZone: 'UTC' } });
    const copy = cloneCondition(original);

    expect(copy.value).toEqual(original.value);
    expect(copy.value).not.toBe(original.value);
  });
});

describe('freshCondition / switchOperator', () => {
  const TZ = 'Europe/Warsaw';

  it('starts the relative operators at a default that already filters, and the rest empty', () => {
    expect(freshCondition('updatedAt', 'InLast', TZ)).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } });
    expect(freshCondition('updatedAt', 'InPeriod', TZ)).toEqual({ field: 'updatedAt', op: 'InPeriod', value: { period: 'thisWeek', timeZone: TZ } });
    expect(freshCondition('createdAt', 'Between', TZ)).toEqual({ field: 'createdAt', op: 'Between', values: [null, null] });
    expect(freshCondition('status', 'In', TZ)).toEqual({ field: 'status', op: 'In', values: [] });
    expect(freshCondition('status', 'Eq', TZ)).toEqual({ field: 'status', op: 'Eq' });
  });

  it('keeps what still means the same thing, and starts over otherwise', () => {
    expect(switchOperator({ field: 'updatedAt', op: 'InLast', value: { amount: 30, unit: 'weeks' } }, 'NotInLast', TZ))
      .toEqual({ field: 'updatedAt', op: 'NotInLast', value: { amount: 30, unit: 'weeks' } });
    expect(switchOperator({ field: 'updatedAt', op: 'Lt', value: '2026-01-01' }, 'InLast', TZ))
      .toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } });
    expect(switchOperator({ field: 'updatedAt', op: 'Lt', value: '2026-01-01' }, 'InPeriod', TZ))
      .toEqual({ field: 'updatedAt', op: 'InPeriod', value: { period: 'thisWeek', timeZone: TZ } });
    expect(switchOperator({ field: 'updatedAt', op: 'InPeriod', value: { period: 'today' } }, 'Lt', TZ))
      .toEqual({ field: 'updatedAt', op: 'Lt', value: '' });
    expect(switchOperator({ field: 'updatedAt', op: 'Lt', value: '2026-01-01' }, 'Gte', TZ))
      .toEqual({ field: 'updatedAt', op: 'Gte', value: '2026-01-01' });
    expect(switchOperator({ field: 'sprint', op: 'Eq', value: { smart: 'active' } }, 'Neq', TZ))
      .toEqual({ field: 'sprint', op: 'Neq', value: { smart: 'active' } });
    expect(switchOperator({ field: 'sprint', op: 'Eq', value: { smart: 'active' } }, 'In', TZ))
      .toEqual({ field: 'sprint', op: 'In', values: [] });
    expect(switchOperator({ field: 'status', op: 'In', values: ['Done'] }, 'NotIn', TZ))
      .toEqual({ field: 'status', op: 'NotIn', values: ['Done'] });
    expect(switchOperator({ field: 'status', op: 'Eq', value: 'Done' }, 'IsNull', TZ))
      .toEqual({ field: 'status', op: 'IsNull' });
  });
});

describe('history conditions', () => {
  it('starts each history operator fresh, and keeps the field (and the window) when switching between them', () => {
    expect(freshCondition('history', 'Was', 'UTC')).toEqual({ field: 'history', op: 'Was', value: { historyField: '', values: [] } });
    expect(freshCondition('history', 'Changed', 'UTC')).toEqual({ field: 'history', op: 'Changed', value: { historyField: '' } });
    expect(freshCondition('history', 'ChangeCount', 'UTC'))
      .toEqual({ field: 'history', op: 'ChangeCount', value: { historyField: '', compare: 'Gte', count: 2 } });

    const counted = {
      field: 'history', op: 'ChangeCount' as const,
      value: { historyField: 'Sprint', compare: 'Gt' as const, count: 4, during: { kind: 'inLast' as const, amount: 7, unit: 'days' as const } },
    };
    expect(switchOperator(counted, 'Changed', 'UTC'))
      .toEqual({ field: 'history', op: 'Changed', value: { historyField: 'Sprint', during: { kind: 'inLast', amount: 7, unit: 'days' } } });
    expect(switchOperator(counted, 'Was', 'UTC')).toEqual({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: [] } });
  });

  it('sends only finished history conditions, in canonical form', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'history', op: 'Was', value: { historyField: 'Sprint', values: [] } }),            // no value yet
        cond('b', { field: 'history', op: 'Changed', value: { historyField: '' } }),                         // no field yet
        cond('c', { field: 'history', op: 'Changed', value: { to: ['Done'], from: [], historyField: 'status' } }),
        cond('d', { field: 'history', op: 'ChangeCount', value: { historyField: 'Sprint', compare: 'Gte' } }), // no count
      ],
    });

    const sent = pruneTree(tree);
    expect(sent).toEqual({ logic: 'And', conditions: [{ field: 'history', op: 'Changed', value: { historyField: 'status', to: ['Done'] } }] });
    expect(Object.keys(sent!.conditions![0].value as object)).toEqual(['historyField', 'to']);
  });

  it('round-trips history values through the URL form, and a malformed one stays unfinished', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101', null] } }),
        cond('b', {
          field: 'history', op: 'Changed',
          value: { historyField: 'status', from: ['To Do'], by: ['Alice'], during: { kind: 'between', from: '2026-09-01', to: null } },
        }),
        cond('c', {
          field: 'history', op: 'ChangeCount',
          value: { historyField: 'Sprint', compare: 'Eq', count: 0, during: { kind: 'inPeriod', period: 'lastMonth', timeZone: 'Europe/Warsaw' } },
        }),
      ],
    });
    expect(pruneTree(parseFilterParam(toUrlJson(tree), null))).toEqual(pruneTree(tree));

    const bad = parseFilterParam(JSON.stringify({
      logic: 'And',
      conditions: [{ field: 'history', op: 'Changed', value: { historyField: 'status', during: { kind: 'fortnight' } } }],
    }), null);
    expect(bad.conditions[0].value).toBeUndefined();
    expect(isCompleteCondition(bad.conditions[0])).toBe(false);   // kept on screen, never broadened to "any time"
  });

  it('copies history values deeply when a condition is duplicated', () => {
    const original = cond('a', { field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101'] } });
    const copy = cloneCondition(original);
    (copy.value as HistoryValue).values!.push('PLAT 102');
    expect((original.value as HistoryValue).values).toEqual(['PLAT 101']);
  });

  it('counts enabled history conditions on their own, over the whole tree', () => {
    const h = (id: string, disabled = false): EditorCondition => ({
      id, field: 'history', op: 'Changed', value: { historyField: 'Sprint' }, ...(disabled ? { disabled: true } : {}),
    });
    expect(countEnabled(group('r', { conditions: [h('a'), h('b', true)], groups: [group('g', { conditions: [h('c')] })] })))
      .toEqual({ conditions: 2, groups: 2, history: 2 });
  });
});
