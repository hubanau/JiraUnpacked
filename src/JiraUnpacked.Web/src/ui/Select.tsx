import type { SelectHTMLAttributes } from 'react';

type SelectProps = SelectHTMLAttributes<HTMLSelectElement>;

export function Select({ className = '', ...rest }: SelectProps) {
  return (
    <select
      {...rest}
      className={`rounded-md border border-[var(--ju-border)] bg-[var(--ju-bg)] px-3 py-2 text-sm text-[var(--ju-text)] focus:outline-none focus:ring-2 focus:ring-[var(--ju-primary)] disabled:cursor-not-allowed disabled:opacity-60 ${className}`}
    />
  );
}
