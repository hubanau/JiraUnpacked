import { apiFetch } from './client';

export interface InviteCodeResponse { code: string; expiresAtUtc: string }

export const createInviteCode = () =>
  apiFetch<InviteCodeResponse>('/api/admin/invite-codes', { method: 'POST' });

export const blockUser = (userId: string) =>
  apiFetch<void>(`/api/admin/users/${userId}/block`, { method: 'POST' });

export const unblockUser = (userId: string) =>
  apiFetch<void>(`/api/admin/users/${userId}/unblock`, { method: 'POST' });

/** Irreversible: cascades across every tenant table. Gate behind a typed confirmation. */
export const deleteUser = (userId: string) =>
  apiFetch<void>(`/api/admin/users/${userId}`, { method: 'DELETE' });
