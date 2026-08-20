import { describe, expect, it } from 'vitest';
import {
  NO_STATUS_LABEL, NOT_ENOUGH_DATA, formatAverageDays, formatStatusLabel,
  isCheckRunnable, barPercent,
} from './format';

describe('formatAverageDays — a null average must never read as zero', () => {
  it('renders null as "not enough data", not 0', () => {
    expect(formatAverageDays(null)).toBe(NOT_ENOUGH_DATA);
    expect(formatAverageDays(null)).not.toContain('0');
  });

  // The distinguishing case: a genuine zero is data, and must still be a number.
  it('renders a real zero as a number', () => {
    expect(formatAverageDays(0)).toBe('0 days');
  });

  it('rounds to one decimal', () => {
    expect(formatAverageDays(3.14159)).toBe('3.1 days');
    expect(formatAverageDays(1)).toBe('1 day');
  });
});

describe('formatStatusLabel — a null status is a real group', () => {
  it('renders null as an explicit label, never "null" or blank', () => {
    expect(formatStatusLabel(null)).toBe(NO_STATUS_LABEL);
    expect(formatStatusLabel(null)).not.toBe('null');
    expect(formatStatusLabel(null).trim()).not.toBe('');
  });

  it('passes a real status through unchanged', () => {
    expect(formatStatusLabel('In Progress')).toBe('In Progress');
  });
});

describe('isCheckRunnable — NotConfigured is not success', () => {
  it('treats Ok as runnable', () => {
    expect(isCheckRunnable('Ok')).toBe(true);
  });

  it('treats NotConfigured as NOT runnable, so zero violations cannot read as clean', () => {
    expect(isCheckRunnable('NotConfigured')).toBe(false);
  });

  it('treats an unknown status as not runnable rather than assuming success', () => {
    expect(isCheckRunnable('Whatever')).toBe(false);
  });
});

describe('barPercent', () => {
  it('scales against the maximum', () => {
    expect(barPercent(5, 10)).toBe(50);
    expect(barPercent(10, 10)).toBe(100);
  });

  it('returns 0 when the maximum is 0 rather than dividing by zero', () => {
    expect(barPercent(0, 0)).toBe(0);
  });
});
