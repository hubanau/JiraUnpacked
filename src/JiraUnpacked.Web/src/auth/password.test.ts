import { describe, expect, it } from 'vitest';
import { MIN_CLASSES, MIN_LENGTH, assessPassword } from './password';

describe('assessPassword', () => {
  it('mirrors the server thresholds', () => {
    expect(MIN_LENGTH).toBe(12);
    expect(MIN_CLASSES).toBe(3);
  });

  it('rejects 11 characters and accepts 12', () => {
    expect(assessPassword('Abcdefg1!aa').valid).toBe(false);  // 11
    expect(assessPassword('Abcdefg1!aaa').valid).toBe(true);  // 12
  });

  it('rejects 2 character classes and accepts 3', () => {
    expect(assessPassword('abcdefghijkl').classes).toBe(1);
    expect(assessPassword('abcdefghijkL').classes).toBe(2);
    expect(assessPassword('abcdefghijkL').valid).toBe(false);
    expect(assessPassword('abcdefghijL1').classes).toBe(3);
    expect(assessPassword('abcdefghijL1').valid).toBe(true);
  });

  it('counts symbols as the fourth class', () => {
    expect(assessPassword('abcdefghijk!').classes).toBe(2);
    expect(assessPassword('ABCDEFGHIJK!').classes).toBe(2);
    expect(assessPassword('abcDEF123!@#').classes).toBe(4);
  });

  it('treats an empty password as invalid without throwing', () => {
    expect(assessPassword('').valid).toBe(false);
    expect(assessPassword('').classes).toBe(0);
  });
});
