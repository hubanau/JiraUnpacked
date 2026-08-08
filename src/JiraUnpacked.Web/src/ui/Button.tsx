import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { Spinner } from './Spinner';

type Variant = 'primary' | 'secondary' | 'danger';

const VARIANTS: Record<Variant, string> = {
  primary: 'bg-[var(--ju-primary)] text-[var(--ju-primary-text)] hover:opacity-90',
  secondary: 'bg-[var(--ju-surface)] text-[var(--ju-text)] border border-[var(--ju-border)] hover:bg-[var(--ju-bg)]',
  danger: 'bg-[var(--ju-danger)] text-white hover:opacity-90',
};

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  loading?: boolean;
  children: ReactNode;
}

export function Button({ variant = 'primary', loading = false, disabled, children, className = '', ...rest }: ButtonProps) {
  return (
    <button
      {...rest}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={`inline-flex items-center justify-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition disabled:cursor-not-allowed disabled:opacity-60 ${VARIANTS[variant]} ${className}`}
    >
      {loading && <Spinner aria-label="Working" />}
      {children}
    </button>
  );
}
