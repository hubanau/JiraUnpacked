import { useCallback, useEffect, useState } from 'react';

export const THEME_KEY = 'ju.theme';
export type Theme = 'light' | 'dark';

// localStorage can throw (storage disabled, private-browsing quota, a
// blocked third-party context, etc.). src/auth/session.ts has wrappers for
// exactly this, but src/ui/ must not import from src/api/ or src/auth/ —
// that layering rule is a reviewed invariant — so this module keeps its own
// tiny guarded copy instead of reaching across the boundary.
function safeGetItem(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function safeSetItem(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Best effort; losing persistence here is not fatal.
  }
}

function initialTheme(): Theme {
  const stored = safeGetItem(THEME_KEY);
  if (stored === 'light' || stored === 'dark') return stored;
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function useTheme(): { theme: Theme; toggle: () => void } {
  const [theme, setTheme] = useState<Theme>(initialTheme);

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark');
  }, [theme]);

  const toggle = useCallback(() => {
    setTheme((current) => {
      const next: Theme = current === 'dark' ? 'light' : 'dark';
      safeSetItem(THEME_KEY, next);
      return next;
    });
  }, []);

  return { theme, toggle };
}
