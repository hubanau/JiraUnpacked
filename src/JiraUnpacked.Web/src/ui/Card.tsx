import type { HTMLAttributes } from 'react';

interface CardProps extends HTMLAttributes<HTMLDivElement> {
  /** Drop the built-in padding when the card wraps a table that pads its own cells. */
  flush?: boolean;
}

/**
 * A card is white on a tinted page, which is where its depth comes from — not
 * from a shadow. The hairline shadow only stops the edge looking painted on.
 */
export function Card({ flush = false, className = '', ...rest }: CardProps) {
  return (
    <div
      {...rest}
      className={`rounded-[var(--ju-radius-lg)] border border-[var(--ju-border)] bg-[var(--ju-surface)] shadow-[var(--ju-shadow-sm)] ${flush ? '' : 'p-5'} ${className}`}
    />
  );
}
