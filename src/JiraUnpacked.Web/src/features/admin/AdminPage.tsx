import { useEffect, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { Button, Card, Field, Modal } from '../../ui';
import { ApiError } from '../../api/errors';
import {
  blockUser,
  createInviteCode,
  deleteUser,
  unblockUser,
  type InviteCodeResponse,
} from '../../api/admin';
import { useAuth } from '../../auth/AuthContext';

const CONFIRM_WORD = 'DELETE';

function errorTitle(error: unknown, fallback: string): string {
  return error instanceof ApiError ? error.title : fallback;
}

function InviteCodeSection() {
  const [result, setResult] = useState<InviteCodeResponse | null>(null);
  const mutation = useMutation({
    mutationFn: createInviteCode,
    // Never console.log the code (or any other credential) — the only
    // place it may appear is the DOM, and only until this page is left.
    onSuccess: setResult,
  });

  return (
    <Card className="flex flex-col gap-3">
      <h2 className="text-base font-semibold text-[var(--ju-text)]">Invite codes</h2>
      <p className="text-sm text-[var(--ju-muted)]">
        Generate a one-time code a new user redeems to register an account.
      </p>
      <div>
        <Button type="button" onClick={() => mutation.mutate()} loading={mutation.isPending}>
          Generate invite code
        </Button>
      </div>
      {mutation.isError && (
        <p role="alert">{errorTitle(mutation.error, 'Could not generate an invite code.')}</p>
      )}
      {result && (
        <div className="flex flex-col gap-1 rounded-md border border-[var(--ju-border)] p-3">
          <p>
            Code: <strong className="font-mono">{result.code}</strong>
          </p>
          <p className="text-sm text-[var(--ju-muted)]">
            Expires {new Date(result.expiresAtUtc).toLocaleString()}.
          </p>
          <p className="text-sm text-[var(--ju-muted)]">
            This code is single-use and cannot be retrieved again once you leave this page —
            copy it now and share it with the person you are inviting.
          </p>
          <div>
            <Button
              type="button"
              variant="secondary"
              onClick={() => {
                navigator.clipboard?.writeText(result.code)?.catch(() => {});
              }}
            >
              Copy code
            </Button>
          </div>
        </div>
      )}
    </Card>
  );
}

function DeleteUserDialog({
  userId,
  open,
  onClose,
  onDeleted,
}: {
  userId: string;
  open: boolean;
  onClose: () => void;
  onDeleted: (userId: string) => void;
}) {
  const [typed, setTyped] = useState('');
  const mutation = useMutation({
    mutationFn: () => deleteUser(userId),
    // Silent success would leave the admin unsure whether the delete
    // actually happened — name the deleted id back in UserActionsSection's
    // own status line, then close the dialog.
    onSuccess: () => {
      onDeleted(userId);
      onClose();
    },
  });

  return (
    <Modal open={open} onClose={onClose} title="Delete user">
      <p className="text-sm text-[var(--ju-text)]">
        This permanently deletes the user and cascades across every tenant table. This cannot
        be undone.
      </p>
      {mutation.isError && (
        <p role="alert">{errorTitle(mutation.error, 'Could not delete the user.')}</p>
      )}
      <Field label={`Type ${CONFIRM_WORD} to confirm`} id="confirm-delete-user">
        <input value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" />
      </Field>
      <div className="mt-4 flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button
          type="button"
          variant="danger"
          disabled={typed !== CONFIRM_WORD}
          loading={mutation.isPending}
          onClick={() => mutation.mutate()}
        >
          Delete
        </Button>
      </div>
    </Modal>
  );
}

function UserActionsSection() {
  const [userId, setUserId] = useState('');
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [deletedUserId, setDeletedUserId] = useState<string | null>(null);

  // mutationFn takes the id as an argument (rather than closing over
  // `userId`) so `.variables` records exactly which id a result belongs to
  // — used below to name the affected user in the success message.
  const block = useMutation({ mutationFn: (id: string) => blockUser(id) });
  const unblock = useMutation({ mutationFn: (id: string) => unblockUser(id) });
  // Pulled out as plain locals (rather than depending on `block.reset`
  // directly) so the effect below depends only on these — TanStack Query
  // binds `reset` once on the underlying observer, so these never change
  // across renders. Depending on `block`/`unblock` themselves instead would
  // be self-defeating: those objects change on every pending/success/error
  // transition too, so the effect would fire immediately after mutate()
  // and reset the very result it just produced.
  const { reset: resetBlock } = block;
  const { reset: resetUnblock } = unblock;

  // Block/unblock results (and the delete confirmation) must never survive
  // a change to the User ID field: without this, blocking user A, pasting
  // user B's id, then clicking Restore access showed "User blocked." AND
  // "Access restored." simultaneously, with neither naming a user — on an
  // admin screen whose actions are consequential, that reads as ambiguous
  // confirmation of who was actually acted on.
  useEffect(() => {
    resetBlock();
    resetUnblock();
    setDeletedUserId(null);
  }, [userId, resetBlock, resetUnblock]);

  const hasUserId = userId.trim() !== '';

  return (
    <Card className="flex flex-col gap-3">
      <h2 className="text-base font-semibold text-[var(--ju-text)]">User actions</h2>
      <p className="text-sm text-[var(--ju-muted)]">
        There is no user directory here — there is no endpoint that lists users. Get the id
        from the user directly, or look it up in the database, then act on it below.
      </p>
      <Field label="User ID" id="userId">
        <input value={userId} onChange={(e) => setUserId(e.target.value)} autoComplete="off" />
      </Field>
      <div className="flex gap-2">
        <Button
          type="button"
          onClick={() => block.mutate(userId)}
          loading={block.isPending}
          disabled={!hasUserId}
        >
          Block user
        </Button>
        <Button
          type="button"
          variant="secondary"
          onClick={() => unblock.mutate(userId)}
          loading={unblock.isPending}
          disabled={!hasUserId}
        >
          Restore access
        </Button>
        <Button
          type="button"
          variant="danger"
          onClick={() => setDeleteOpen(true)}
          disabled={!hasUserId}
        >
          Delete user
        </Button>
      </div>
      {block.isError && <p role="alert">{errorTitle(block.error, 'Could not block the user.')}</p>}
      {block.isSuccess && <p role="status">User {block.variables} blocked.</p>}
      {unblock.isError && (
        <p role="alert">{errorTitle(unblock.error, 'Could not restore the user.')}</p>
      )}
      {unblock.isSuccess && <p role="status">Access restored for user {unblock.variables}.</p>}
      {deletedUserId && <p role="status">User {deletedUserId} deleted.</p>}
      {/* Mounted only while open, mirroring UserMenu's delete-account dialog
          (src/shell/UserMenu.tsx): an unconditionally-mounted dialog would let
          its typed confirmation and mutation state survive a close, so
          reopening after Escape could show the confirm button already
          enabled — that shipped as a real bug in Spec 1. */}
      {deleteOpen && (
        <DeleteUserDialog
          userId={userId}
          open
          onClose={() => setDeleteOpen(false)}
          onDeleted={setDeletedUserId}
        />
      )}
    </Card>
  );
}

export default function AdminPage() {
  const { role } = useAuth();

  // This check only hides the controls for a nicer UX when the client's
  // notion of role happens to be non-admin — it is NOT the security
  // boundary. The `Admin` policy is enforced server-side on every
  // /api/admin/* endpoint, so a stale or forged client role can, at worst,
  // reveal these buttons; it can never grant an action. Any 403 the server
  // returns must still surface to the admin verbatim (see the mutation
  // error handling in InviteCodeSection / UserActionsSection /
  // DeleteUserDialog above) rather than being treated as unreachable.
  if (role !== 'Admin') {
    return (
      <Card>
        <p>Admin tools are not available for this account.</p>
      </Card>
    );
  }

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-lg font-semibold text-[var(--ju-text)]">Admin</h1>
      <InviteCodeSection />
      <UserActionsSection />
    </div>
  );
}
