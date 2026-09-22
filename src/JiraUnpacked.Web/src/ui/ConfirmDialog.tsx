import type { ReactNode } from 'react';
import { Button } from './Button';
import { Modal } from './Modal';

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  /** What will happen, in plain words — the whole point of asking. */
  children: ReactNode;
  confirmLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
  pending?: boolean;
  /** Shown inside the dialog, so a refusal (e.g. a sync still running) is read where it was asked. */
  error?: string | null;
}

/**
 * The explicit second step in front of a destructive action. Cancel is the first focusable
 * control, so Enter on an accidentally opened dialog does nothing irreversible.
 */
export function ConfirmDialog({
  open, title, children, confirmLabel, onConfirm, onCancel, pending = false, error,
}: ConfirmDialogProps) {
  return (
    <Modal open={open} onClose={onCancel} title={title}>
      <div className="flex flex-col gap-4 text-sm text-[var(--ju-text)]">
        <div>{children}</div>
        {error && (
          <p role="alert" className="text-sm text-[var(--ju-danger)]">
            {error}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button variant="secondary" onClick={onCancel} disabled={pending}>
            Cancel
          </Button>
          <Button variant="danger" onClick={onConfirm} loading={pending}>
            {confirmLabel}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
