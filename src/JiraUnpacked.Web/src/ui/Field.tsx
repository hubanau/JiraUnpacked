import { cloneElement, isValidElement, type ReactElement, type ReactNode } from 'react';

interface FieldProps {
  label: string;
  id: string;
  error?: string;
  hint?: string;
  children: ReactNode;
}

export function Field({ label, id, error, hint, children }: FieldProps) {
  const errorId = `${id}-error`;
  const hintId = `${id}-hint`;
  const describedBy = error ? errorId : hint ? hintId : undefined;

  const child = isValidElement(children)
    ? cloneElement(children as ReactElement<Record<string, unknown>>, {
        id,
        'aria-describedby': describedBy,
        'aria-invalid': error ? true : undefined,
      })
    : children;

  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="text-sm font-medium text-[var(--ju-text)]">
        {label}
      </label>
      {child}
      {error ? (
        <p id={errorId} role="alert" className="text-xs text-[var(--ju-danger)]">
          {error}
        </p>
      ) : hint ? (
        <p id={hintId} className="text-xs text-[var(--ju-muted)]">
          {hint}
        </p>
      ) : null}
    </div>
  );
}
