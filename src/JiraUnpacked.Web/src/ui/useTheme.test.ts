import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { THEME_KEY, useTheme } from './useTheme';

describe('useTheme', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.classList.remove('dark');
  });

  it('defaults to the OS preference when nothing is stored', () => {
    vi.stubGlobal('matchMedia', (q: string) => ({
      matches: q.includes('dark'),
      media: q, addEventListener() {}, removeEventListener() {},
    }));

    const { result } = renderHook(() => useTheme());

    expect(result.current.theme).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('persists an explicit choice and applies the dark class', () => {
    vi.stubGlobal('matchMedia', (q: string) => ({
      matches: false, media: q, addEventListener() {}, removeEventListener() {},
    }));

    const { result } = renderHook(() => useTheme());
    expect(result.current.theme).toBe('light');

    act(() => result.current.toggle());

    expect(result.current.theme).toBe('dark');
    expect(localStorage.getItem(THEME_KEY)).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('prefers the stored choice over the OS preference', () => {
    localStorage.setItem(THEME_KEY, 'light');
    vi.stubGlobal('matchMedia', (q: string) => ({
      matches: true, media: q, addEventListener() {}, removeEventListener() {},
    }));

    const { result } = renderHook(() => useTheme());

    expect(result.current.theme).toBe('light');
  });
});
