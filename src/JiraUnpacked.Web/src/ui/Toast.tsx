type Tone = 'success' | 'danger';

interface ToastProps {
  message: string;
  tone?: Tone;
  onDismiss: () => void;
}

const TONES: Record<Tone, string> = {
  success: 'border-[var(--ju-success)] text-[var(--ju-success)]',
  danger: 'border-[var(--ju-danger)] text-[var(--ju-danger)]',
};

export function Toast({ message, tone = 'success', onDismiss }: ToastProps) {
  return (
    <div
      role="status"
      aria-live="polite"
      className={`flex items-center gap-3 rounded-md border bg-[var(--ju-surface)] px-4 py-2 text-sm shadow-sm ${TONES[tone]}`}
    >
      <span>{message}</span>
      <button
        type="button"
        onClick={onDismiss}
        aria-label="Dismiss"
        className="ml-auto text-[var(--ju-muted)] hover:text-[var(--ju-text)]"
      >
        &times;
      </button>
    </div>
  );
}
