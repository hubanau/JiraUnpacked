import type { HTMLAttributes } from 'react';

type Tone = 'neutral' | 'accent' | 'success' | 'warning' | 'danger';

/**
 * Soft tint plus the role colour for text, rather than a 10%-opacity wash. The
 * tint tokens are real colours in both themes, so contrast holds in dark mode —
 * an opacity wash over a dark surface goes muddy.
 */
const TONES: Record<Tone, string> = {
  neutral: 'bg-[var(--ju-surface-2)] text-[var(--ju-text-secondary)] border-[var(--ju-border)]',
  accent: 'bg-[var(--ju-primary-soft)] text-[var(--ju-primary)] border-[var(--ju-primary)]/25',
  success: 'bg-[var(--ju-success-soft)] text-[var(--ju-success)] border-[var(--ju-success)]/25',
  warning: 'bg-[var(--ju-warning-soft)] text-[var(--ju-warning)] border-[var(--ju-warning)]/25',
  danger: 'bg-[var(--ju-danger-soft)] text-[var(--ju-danger)] border-[var(--ju-danger)]/25',
};

interface BadgeProps extends HTMLAttributes<HTMLSpanElement> {
  tone?: Tone;
}

export function Badge({ tone = 'neutral', className = '', ...rest }: BadgeProps) {
  return (
    <span
      {...rest}
      className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium ${TONES[tone]} ${className}`}
    />
  );
}
