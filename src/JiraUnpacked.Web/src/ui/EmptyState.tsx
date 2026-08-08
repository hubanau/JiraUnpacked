import type { ReactNode } from 'react';

interface EmptyStateProps {
  title: string;
  description?: string;
  action?: ReactNode;
}

export function EmptyState({ title, description, action }: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center gap-2 rounded-lg border border-dashed border-[var(--ju-border)] p-8 text-center">
      <p className="text-sm font-medium text-[var(--ju-text)]">{title}</p>
      {description && <p className="text-sm text-[var(--ju-muted)]">{description}</p>}
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}
