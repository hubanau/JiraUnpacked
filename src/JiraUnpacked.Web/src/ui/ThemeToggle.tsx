import { useTheme } from './useTheme';

export function ThemeToggle() {
  const { theme, toggle } = useTheme();
  const isDark = theme === 'dark';

  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={isDark ? 'Switch to light theme' : 'Switch to dark theme'}
      aria-pressed={isDark}
      className="inline-flex items-center justify-center rounded-md border border-[var(--ju-border)] bg-[var(--ju-surface)] px-3 py-2 text-sm text-[var(--ju-text)] transition hover:bg-[var(--ju-bg)]"
    >
      {isDark ? 'Dark' : 'Light'}
    </button>
  );
}
