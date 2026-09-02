import type { ReactNode } from 'react';

interface EmptyStateProps {
  title: string;
  description?: string;
  action?: ReactNode;
}

export function EmptyState({ title, description, action }: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center gap-2 rounded-[var(--ju-radius-lg)] border border-dashed border-[var(--ju-border-strong)] bg-[var(--ju-surface)] p-10 text-center">
      <p className="text-base font-medium text-[var(--ju-text)]">{title}</p>
      {description && <p className="max-w-sm text-sm text-[var(--ju-muted)]">{description}</p>}
      {action && <div className="mt-3">{action}</div>}
    </div>
  );
}
