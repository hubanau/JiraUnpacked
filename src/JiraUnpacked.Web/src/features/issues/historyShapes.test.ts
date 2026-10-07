import { describe, expect, it } from 'vitest';
import type { FilterOperator, HistoryValue } from '../../api/issues';
import { freshHistoryValue, isCompleteHistoryValue, readHistoryValue, readWindow } from './historyShapes';

describe('readHistoryValue', () => {
  it('reads each operator’s keys in canonical order, dropping explicit nulls and empty optional lists', () => {
    const counted = readHistoryValue('ChangeCount', {
      during: { unit: 'days', amount: 30, kind: 'inLast' }, count: 3, compare: 'Gte', historyField: 'Sprint',
    });
    expect(counted).toEqual({ historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 30, unit: 'days' } });
    expect(Object.keys(counted!)).toEqual(['historyField', 'compare', 'count', 'during']);
    expect(Object.keys(counted!.during!)).toEqual(['kind', 'amount', 'unit']);

    expect(readHistoryValue('Changed', { historyField: 'status', by: null, from: [], to: ['Done', 'Done', ''], during: null }))
      .toEqual({ historyField: 'status', to: ['Done', null] });
    expect(readHistoryValue('Was', { historyField: 'Sprint' })).toEqual({ historyField: 'Sprint', values: [] });
    expect(readHistoryValue('Changed', {
      historyField: 'Sprint', during: { kind: 'inPeriod', timeZone: 'Europe/Warsaw', period: 'lastMonth' },
    })?.during).toEqual({ kind: 'inPeriod', period: 'lastMonth', timeZone: 'Europe/Warsaw' });
  });

  it('refuses a value it cannot read whole, rather than reading less than it says', () => {
    const unreadable: [FilterOperator, unknown][] = [
      ['Was', { historyField: 'status', values: 'Done' }],
      ['Was', { historyField: 'status', values: ['Done'], during: { kind: 'inLast', amount: 1, unit: 'days' } }],
      ['Was', { values: ['x'] }],
      ['Changed', { historyField: 'status', by: [null] }],
      ['Changed', { historyField: 'status', to: [7] }],
      ['Changed', { historyField: 'status', during: { kind: 'fortnight' } }],
      ['Changed', { historyField: 'status', during: { kind: 'between', from: 5 } }],
      ['Changed', { historyField: 'status', colour: 'red' }],
      ['ChangeCount', { historyField: 'Sprint', compare: 'About', count: 3 }],
      ['ChangeCount', { historyField: 'Sprint', compare: 'Gte', count: '3' }],
      ['Eq', { historyField: 'status', values: ['x'] }],
      ['Was', 'status'],
    ];
    for (const [op, raw] of unreadable) expect(readHistoryValue(op, raw)).toBeUndefined();
  });

  it('reads the three window shapes and nothing else', () => {
    expect(readWindow({ kind: 'between', to: '2026-09-30' })).toEqual({ kind: 'between', from: null, to: '2026-09-30' });
    expect(readWindow({ kind: 'inLast', amount: '30', unit: 'days' })).toBeUndefined();
    expect(readWindow({ kind: 'inPeriod', period: 'fortnight' })).toBeUndefined();
    expect(readWindow(null)).toBeUndefined();
  });
});

describe('isCompleteHistoryValue', () => {
  it('needs a field; "was" a value; "number of changes" a compare and a whole count from 0 to 10000; and a finished window', () => {
    const done = (op: FilterOperator, v: HistoryValue) => isCompleteHistoryValue(op, v);
    expect(done('Changed', { historyField: '' })).toBe(false);
    expect(done('Changed', { historyField: '  ' })).toBe(false);
    expect(done('Changed', { historyField: 'x'.repeat(101) })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint' })).toBe(true);
    expect(done('Was', { historyField: 'Sprint', values: [] })).toBe(false);
    expect(done('Was', { historyField: 'Sprint', values: [null] })).toBe(true);
    expect(done('Was', { historyField: 'Sprint', values: ['x'.repeat(256)] })).toBe(false);
    expect(done('ChangeCount', { historyField: 'Sprint', compare: 'Eq', count: 0 })).toBe(true);
    expect(done('ChangeCount', { historyField: 'Sprint', compare: 'Eq' })).toBe(false);
    expect(done('ChangeCount', { historyField: 'Sprint', count: 2 })).toBe(false);
    expect(done('ChangeCount', { historyField: 'Sprint', compare: 'Eq', count: 2.5 })).toBe(false);
    expect(done('ChangeCount', { historyField: 'Sprint', compare: 'Eq', count: 10001 })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint', during: { kind: 'inLast', amount: 0, unit: 'days' } })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint', during: { kind: 'between', from: null, to: null } })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint', during: { kind: 'between', from: '2026-09-30', to: '2026-09-01' } })).toBe(false);
    expect(done('Changed', { historyField: 'Sprint', during: { kind: 'between', from: null, to: '2026-09-30' } })).toBe(true);
    expect(isCompleteHistoryValue('Changed', undefined)).toBe(false);
  });

  it('starts each operator where the spec says', () => {
    expect(freshHistoryValue('Was', 'Sprint')).toEqual({ historyField: 'Sprint', values: [] });
    expect(freshHistoryValue('Changed', '')).toEqual({ historyField: '' });
    expect(freshHistoryValue('ChangeCount', '')).toEqual({ historyField: '', compare: 'Gte', count: 2 });
  });
});
