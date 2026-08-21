import { apiFetch } from './client';
import type { PagedResult } from './issues';

export interface InviteCodeResponse { code: string; expiresAtUtc: string }

export interface AdminUserListItem {
  id: string;
  email: string;
  username: string;
  role: string;
  status: string;
  createdAt: string;
}

export const listUsers = (page = 1, pageSize = 50) =>
  apiFetch<PagedResult<AdminUserListItem>>(`/api/admin/users?page=${page}&pageSize=${pageSize}`);

export const createInviteCode = () =>
  apiFetch<InviteCodeResponse>('/api/admin/invite-codes', { method: 'POST' });

export const blockUser = (userId: string) =>
  apiFetch<void>(`/api/admin/users/${encodeURIComponent(userId)}/block`, { method: 'POST' });

export const unblockUser = (userId: string) =>
  apiFetch<void>(`/api/admin/users/${encodeURIComponent(userId)}/unblock`, { method: 'POST' });

/** Irreversible: cascades across every tenant table. Gate behind a typed confirmation. */
export const deleteUser = (userId: string) =>
  apiFetch<void>(`/api/admin/users/${encodeURIComponent(userId)}`, { method: 'DELETE' });
