import type { HTMLAttributes } from 'react';

type Tone = 'neutral' | 'success' | 'warning' | 'danger';

const TONES: Record<Tone, string> = {
  neutral: 'bg-[var(--ju-surface)] text-[var(--ju-muted)] border border-[var(--ju-border)]',
  success: 'bg-[var(--ju-success)]/10 text-[var(--ju-success)] border border-[var(--ju-success)]/30',
  warning: 'bg-[var(--ju-warning)]/10 text-[var(--ju-warning)] border border-[var(--ju-warning)]/30',
  danger: 'bg-[var(--ju-danger)]/10 text-[var(--ju-danger)] border border-[var(--ju-danger)]/30',
};

interface BadgeProps extends HTMLAttributes<HTMLSpanElement> {
  tone?: Tone;
}

export function Badge({ tone = 'neutral', className = '', ...rest }: BadgeProps) {
  return (
    <span
      {...rest}
      className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${TONES[tone]} ${className}`}
    />
  );
}
