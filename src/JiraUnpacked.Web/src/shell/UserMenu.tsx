import { useState } from 'react';
import { Button, Field, Modal } from '../ui';
import { ApiError } from '../api/errors';
import { deleteMe } from '../api/auth';
import { useAuth } from '../auth/AuthContext';

const CONFIRM_WORD = 'DELETE';

function DeleteAccountDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { signOut } = useAuth();
  const [typed, setTyped] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [detail, setDetail] = useState<string | null>(null);

  async function confirm() {
    setBusy(true);
    setError(null);
    setDetail(null);
    try {
      await deleteMe();
      await signOut();
    } catch (e) {
      if (e instanceof ApiError) {
        setError(e.title);
        setDetail(e.detail ?? null);
      } else {
        setError('Could not delete the account.');
        setDetail(null);
      }
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} title="Delete your account">
      <p>
        This permanently deletes your account and every connection, project, and
        synced issue belonging to it. This cannot be undone.
      </p>
      <Field label={`Type ${CONFIRM_WORD} to confirm`} id="confirm-delete" error={error ?? undefined}>
        <input id="confirm-delete" value={typed} onChange={(e) => setTyped(e.target.value)} />
      </Field>
      {detail && <p>{detail}</p>}
      <Button variant="danger" disabled={typed !== CONFIRM_WORD} loading={busy} onClick={() => void confirm()}>
        Delete account
      </Button>
    </Modal>
  );
}

export function UserMenu() {
  const { username, signOut } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);

  return (
    <div className="relative">
      <Button
        type="button"
        variant="secondary"
        aria-haspopup="menu"
        aria-expanded={menuOpen}
        onClick={() => setMenuOpen((open) => !open)}
      >
        {username ?? 'Account'}
      </Button>
      {menuOpen && (
        <div
          role="menu"
          className="absolute right-0 z-10 mt-1 flex w-48 flex-col gap-1 rounded-md border border-[var(--ju-border)] bg-[var(--ju-bg)] p-1 shadow-lg"
        >
          <button
            type="button"
            role="menuitem"
            className="rounded px-3 py-2 text-left text-sm text-[var(--ju-text)] hover:bg-[var(--ju-surface)]"
            onClick={() => {
              setMenuOpen(false);
              void signOut();
            }}
          >
            Sign out
          </button>
          <button
            type="button"
            role="menuitem"
            className="rounded px-3 py-2 text-left text-sm text-[var(--ju-danger)] hover:bg-[var(--ju-surface)]"
            onClick={() => {
              setMenuOpen(false);
              setDeleteOpen(true);
            }}
          >
            Delete account
          </button>
        </div>
      )}
      {/* Mounted only while open: the dialog's typed/error state must not
          survive a close, or a prior "DELETE" entry + Escape leaves the
          confirm button armed the next time the dialog is opened. */}
      {deleteOpen && <DeleteAccountDialog open onClose={() => setDeleteOpen(false)} />}
    </div>
  );
}
