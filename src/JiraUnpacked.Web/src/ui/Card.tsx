import type { HTMLAttributes } from 'react';

type CardProps = HTMLAttributes<HTMLDivElement>;

export function Card({ className = '', ...rest }: CardProps) {
  return (
    <div
      {...rest}
      className={`rounded-lg border border-[var(--ju-border)] bg-[var(--ju-surface)] p-4 ${className}`}
    />
  );
}
