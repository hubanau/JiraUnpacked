import { useState } from 'react';
import { useMutation, useQuery, useQueryClient, type UseMutationResult } from '@tanstack/react-query';
import { Badge, Button, Card, EmptyState, Field, Modal, Spinner, Table, TBody, TD, TH, THead, TR } from '../../ui';
import { ApiError } from '../../api/errors';
import {
  blockUser,
  createInviteCode,
  deleteUser,
  listUsers,
  unblockUser,
  type AdminUserListItem,
  type InviteCodeResponse,
} from '../../api/admin';
import { queryKeys } from '../../api/queryKeys';
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
    // actually happened — name the deleted id back in UsersSection's own
    // status line, then close the dialog.
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

type BlockMutation = UseMutationResult<void, unknown, string>;

function UserRow({
  user,
  block,
  unblock,
  onRequestDelete,
}: {
  user: AdminUserListItem;
  block: BlockMutation;
  unblock: BlockMutation;
  onRequestDelete: (userId: string) => void;
}) {
  // Guarding every result on `variables === user.id` — not just "is there a
  // result" — is what keeps a block/unblock response from ever rendering
  // against a row it wasn't for. `block`/`unblock` are single mutation
  // instances shared by every row in the table (so a fresh action on any
  // row cleanly supersedes stale pending/success state from another), and
  // this guard is what turns "the most recent result" into "the result for
  // *this* row" — a late response for a different row simply fails the
  // equality check and renders nothing here.
  const blockMatches = block.variables === user.id;
  const unblockMatches = unblock.variables === user.id;

  return (
    <TR>
      <TD>{user.username}</TD>
      <TD>{user.email}</TD>
      <TD>
        <Badge tone={user.role === 'Admin' ? 'neutral' : 'success'}>{user.role}</Badge>
      </TD>
      <TD>
        <Badge tone={user.status === 'Blocked' ? 'danger' : 'success'}>{user.status}</Badge>
      </TD>
      <TD>{new Date(user.createdAt).toLocaleDateString()}</TD>
      <TD>
        <div className="flex flex-col gap-1">
          <div className="flex gap-2">
            <Button
              type="button"
              onClick={() => block.mutate(user.id)}
              loading={block.isPending && blockMatches}
            >
              Block
            </Button>
            <Button
              type="button"
              variant="secondary"
              onClick={() => unblock.mutate(user.id)}
              loading={unblock.isPending && unblockMatches}
            >
              Unblock
            </Button>
            <Button type="button" variant="danger" onClick={() => onRequestDelete(user.id)}>
              Delete user
            </Button>
          </div>
          {block.isError && blockMatches && (
            <p role="alert">{errorTitle(block.error, 'Could not block the user.')}</p>
          )}
          {block.isSuccess && blockMatches && <p role="status">User {user.username} blocked.</p>}
          {unblock.isError && unblockMatches && (
            <p role="alert">{errorTitle(unblock.error, 'Could not restore the user.')}</p>
          )}
          {unblock.isSuccess && unblockMatches && (
            <p role="status">Access restored for user {user.username}.</p>
          )}
        </div>
      </TD>
    </TR>
  );
}

function UsersSection() {
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const [deleteTargetId, setDeleteTargetId] = useState<string | null>(null);
  const [deletedUserId, setDeletedUserId] = useState<string | null>(null);

  const { data, isPending, error } = useQuery({
    queryKey: queryKeys.adminUsers(page),
    queryFn: () => listUsers(page),
  });

  const invalidateList = () =>
    queryClient.invalidateQueries({ queryKey: queryKeys.adminUsers(page) });

  // mutationFn takes the id as an argument (rather than closing over a
  // single selected id) so `.variables` records exactly which row a result
  // belongs to — see UserRow's blockMatches/unblockMatches guards. Block
  // and unblock reject non-End-Users server-side with a typed error, so the
  // server's message is what surfaces here rather than the client hiding
  // admins from the list.
  const block = useMutation({
    mutationFn: (id: string) => blockUser(id),
    onSuccess: invalidateList,
  });
  const unblock = useMutation({
    mutationFn: (id: string) => unblockUser(id),
    onSuccess: invalidateList,
  });

  function handleDeleted(id: string) {
    setDeletedUserId(id);
    invalidateList();
  }

  if (isPending) {
    return (
      <Card>
        <Spinner aria-label="Loading users" />
      </Card>
    );
  }

  const items = data?.items ?? [];
  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;
  // `page > 1` is included on its own, not just `totalPages > 1`: deleting the
  // last row on the current page shrinks totalCount (and so totalPages) in
  // the very same response that reports zero items for this page, so a pager
  // gated on totalPages alone would vanish before the admin ever gets a
  // chance to click Previous back to a page that still has rows.
  const showPager = data !== undefined && (totalPages > 1 || page > 1);

  return (
    <Card className="flex flex-col gap-3">
      <h2 className="text-base font-semibold text-[var(--ju-text)]">Users</h2>
      {error && <p role="alert">{errorTitle(error, 'Could not load users.')}</p>}

      {!error && items.length === 0 && (
        <EmptyState
          title={data && data.totalCount > 0 ? 'No users on this page' : 'No users yet'}
          description={
            data && data.totalCount > 0
              ? 'Go back to a previous page to see existing users.'
              : 'Generate an invite code above to create the first account.'
          }
        />
      )}

      {data && items.length > 0 && (
        <Table>
          <THead>
            <TR>
              <TH>Username</TH>
              <TH>Email</TH>
              <TH>Role</TH>
              <TH>Status</TH>
              <TH>Created</TH>
              <TH>Actions</TH>
            </TR>
          </THead>
          <TBody>
            {items.map((user) => (
              <UserRow
                key={user.id}
                user={user}
                block={block}
                unblock={unblock}
                onRequestDelete={setDeleteTargetId}
              />
            ))}
          </TBody>
        </Table>
      )}

      {showPager && data && (
        <div className="flex items-center justify-between">
          <p className="text-sm text-[var(--ju-muted)]">
            Page {data.page} of {totalPages} · {data.totalCount} users
          </p>
          <div className="flex gap-2">
            <Button variant="secondary" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              Previous
            </Button>
            <Button
              variant="secondary"
              disabled={page >= totalPages}
              onClick={() => setPage((p) => p + 1)}
            >
              Next
            </Button>
          </div>
        </div>
      )}

      {deletedUserId && <p role="status">User {deletedUserId} deleted.</p>}

      {/* Mounted only while a row's delete is requested, mirroring UserMenu's
          delete-account dialog (src/shell/UserMenu.tsx): an unconditionally
          mounted dialog would let its typed confirmation and mutation state
          survive a close, so reopening after Escape could show the confirm
          button already enabled — that shipped as a real bug in Spec 1. */}
      {deleteTargetId && (
        <DeleteUserDialog
          userId={deleteTargetId}
          open
          onClose={() => setDeleteTargetId(null)}
          onDeleted={handleDeleted}
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
  // /api/admin/* endpoint (including GET /api/admin/users itself — Users
  // carries no tenant query filter, so that policy is the only guard
  // between an End User and every account on the platform), so a stale or
  // forged client role can, at worst, reveal these controls; it can never
  // grant an action. Any 403 the server returns must still surface to the
  // admin verbatim (see the mutation error handling in InviteCodeSection /
  // UserRow / DeleteUserDialog above) rather than being treated as
  // unreachable.
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
      <UsersSection />
    </div>
  );
}
