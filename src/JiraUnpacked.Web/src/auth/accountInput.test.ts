import { describe, expect, it } from 'vitest';
import {
  EMAIL_MAX_LENGTH, USERNAME_MAX_LENGTH, USERNAME_MIN_LENGTH, emailProblem, usernameProblem,
} from './accountInput';

describe('account input rules', () => {
  it('mirrors the server limits', () => {
    expect(EMAIL_MAX_LENGTH).toBe(254);
    expect(USERNAME_MIN_LENGTH).toBe(3);
    expect(USERNAME_MAX_LENGTH).toBe(32);
  });

  it('accepts plain addresses and rejects the obvious mistakes', () => {
    expect(emailProblem('alice@example.com')).toBeNull();
    expect(emailProblem('  alice@example.com ')).toBeNull();
    expect(emailProblem('')).not.toBeNull();
    expect(emailProblem('alice')).not.toBeNull();
    expect(emailProblem('alice@')).not.toBeNull();
    expect(emailProblem('a@@b.com')).not.toBeNull();
    expect(emailProblem('Alice <alice@example.com>')).not.toBeNull();
  });

  it('enforces the email length limit', () => {
    const atLimit = 'a'.repeat(EMAIL_MAX_LENGTH - '@example.com'.length) + '@example.com';
    expect(emailProblem(atLimit)).toBeNull();
    expect(emailProblem('a' + atLimit)).not.toBeNull();
  });

  it('accepts 3–32 letters, digits, dot, underscore and dash', () => {
    expect(usernameProblem('abc')).toBeNull();
    expect(usernameProblem('Bob_the-Builder.2')).toBeNull();
    expect(usernameProblem('a'.repeat(32))).toBeNull();
    expect(usernameProblem('ab')).not.toBeNull();
    expect(usernameProblem('a'.repeat(33))).not.toBeNull();
    expect(usernameProblem('has space')).not.toBeNull();
  });

  it('never lets a username look like an email', () => {
    expect(usernameProblem('alice@example.com')).toMatch(/@/);
  });
});
