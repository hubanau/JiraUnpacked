import { Button } from './Button';

interface ErrorStateProps {
  title: string;
  detail?: string;
  onRetry?: () => void;
}

export function ErrorState({ title, detail, onRetry }: ErrorStateProps) {
  return (
    <div className="flex flex-col items-center gap-2 rounded-[var(--ju-radius-lg)] border border-[var(--ju-danger)]/25 bg-[var(--ju-danger-soft)] p-8 text-center">
      <p role="alert" className="text-sm font-medium text-[var(--ju-danger)]">
        {title}
      </p>
      {detail && <p className="text-sm text-[var(--ju-muted)]">{detail}</p>}
      {onRetry && (
        <Button variant="secondary" className="mt-2" onClick={onRetry}>
          Retry
        </Button>
      )}
    </div>
  );
}
