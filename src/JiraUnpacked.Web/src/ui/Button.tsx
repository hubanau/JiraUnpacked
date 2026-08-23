import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { Spinner } from './Spinner';

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger';
type Size = 'sm' | 'md';

const VARIANTS: Record<Variant, string> = {
  primary:
    'bg-[var(--ju-primary)] text-[var(--ju-primary-text)] hover:bg-[var(--ju-primary-hover)] active:translate-y-px',
  secondary:
    'bg-[var(--ju-surface)] text-[var(--ju-text)] border border-[var(--ju-border)] hover:border-[var(--ju-border-strong)] hover:bg-[var(--ju-surface-2)] active:translate-y-px',
  ghost:
    'text-[var(--ju-text-secondary)] hover:bg-[var(--ju-surface-2)] hover:text-[var(--ju-text)]',
  danger:
    'bg-[var(--ju-danger)] text-white hover:opacity-90 active:translate-y-px',
};

const SIZES: Record<Size, string> = {
  sm: 'px-2.5 py-1.5 text-xs gap-1.5',
  md: 'px-4 py-2 text-sm gap-2',
};

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
  loading?: boolean;
  children: ReactNode;
}

export function Button({
  variant = 'primary', size = 'md', loading = false, disabled, children, className = '', ...rest
}: ButtonProps) {
  return (
    <button
      {...rest}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={`inline-flex items-center justify-center rounded-[var(--ju-radius)] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-60 disabled:active:translate-y-0 ${SIZES[size]} ${VARIANTS[variant]} ${className}`}
    >
      {loading && <Spinner aria-label="Working" />}
      {children}
    </button>
  );
}
