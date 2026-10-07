import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  MAX_RELATIVE_AMOUNT, PERIODS, SMART_SPRINT_LABELS, browserTimeZone, readPeriod, readRelativeAmount, readSmart,
} from './valueShapes';

afterEach(() => vi.restoreAllMocks());

describe('readers', () => {
  it('read each object shape into a fresh object in canonical key order', () => {
    const period = readPeriod({ timeZone: 'Europe/Warsaw', extra: 1, period: 'thisWeek' });
    expect(period).toEqual({ period: 'thisWeek', timeZone: 'Europe/Warsaw' });
    expect(Object.keys(period!)).toEqual(['period', 'timeZone']);

    expect(readRelativeAmount({ unit: 'weeks', amount: 2 })).toEqual({ amount: 2, unit: 'weeks' });
    expect(readSmart({ smart: 'open', label: 'x' })).toEqual({ smart: 'open' });
  });

  it('leave a period without a zone zoneless — it means UTC and keeps meaning it', () => {
    expect(readPeriod({ period: 'today' })).toEqual({ period: 'today' });
    expect(readPeriod({ period: 'today', timeZone: null })).toEqual({ period: 'today' });
  });

  it('refuse anything that is not exactly the shape', () => {
    expect(readSmart({ smart: 'current' })).toBeUndefined();
    expect(readSmart({ smart: 'toString' })).toBeUndefined();
    expect(readSmart('active')).toBeUndefined();
    expect(readRelativeAmount({ amount: '14', unit: 'days' })).toBeUndefined();
    expect(readRelativeAmount({ amount: 14, unit: 'years' })).toBeUndefined();
    expect(readPeriod({ period: 'fortnight' })).toBeUndefined();
    expect(readPeriod(['thisWeek'])).toBeUndefined();
    expect(readPeriod(null)).toBeUndefined();
  });
});

describe('vocabularies', () => {
  it('lists the periods in reading order, Today to Last year', () => {
    expect(PERIODS.map((p) => p.label)).toEqual([
      'Today', 'Yesterday', 'This week', 'Last week', 'This month', 'Last month',
      'This quarter', 'Last quarter', 'This year', 'Last year',
    ]);
  });

  it('caps a relative amount where the server does', () => {
    expect(MAX_RELATIVE_AMOUNT).toBe(3650);
  });

  it('names the smart values as the spec does', () => {
    expect(SMART_SPRINT_LABELS).toEqual({
      active: 'Active sprint', future: 'Future sprints', open: 'Open sprints (active + future)', closed: 'Closed sprints',
    });
  });
});

describe('browserTimeZone', () => {
  it('is the browser’s own IANA zone', () => {
    const real = Intl.DateTimeFormat().resolvedOptions();
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ ...real, timeZone: 'Europe/Warsaw' });

    expect(browserTimeZone()).toBe('Europe/Warsaw');
  });

  it('falls back to UTC when the browser cannot say', () => {
    const real = Intl.DateTimeFormat().resolvedOptions();
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ ...real, timeZone: '' });

    expect(browserTimeZone()).toBe('UTC');
  });
});
