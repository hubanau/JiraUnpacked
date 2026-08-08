import { apiFetch } from './client';

export interface TokenResponse {
  accessToken: string;
  expiresAtUtc: string;
  refreshToken: string;
}

export interface MeResponse {
  sub: string | null;
  role: string | null;
  endUserId: string | null;
}

export const login = (emailOrUsername: string, password: string) =>
  apiFetch<TokenResponse>('/api/auth/login', {
    method: 'POST', body: { emailOrUsername, password }, skipAuthRetry: true,
  });

export const register = (inviteCode: string, email: string, username: string, password: string) =>
  apiFetch<void>('/api/auth/register', {
    method: 'POST', body: { inviteCode, email, username, password }, skipAuthRetry: true,
  });

export const verify = (email: string, code: string) =>
  apiFetch<void>('/api/auth/verify', {
    method: 'POST', body: { email, code }, skipAuthRetry: true,
  });

export const resendVerification = (email: string) =>
  apiFetch<void>('/api/auth/verify/resend', {
    method: 'POST', body: { email }, skipAuthRetry: true,
  });

export const refreshTokens = (refreshToken: string) =>
  apiFetch<TokenResponse>('/api/auth/refresh', {
    method: 'POST', body: { refreshToken }, skipAuthRetry: true,
  });

export const logoutRequest = (refreshToken: string) =>
  apiFetch<void>('/api/auth/logout', {
    method: 'POST', body: { refreshToken }, skipAuthRetry: true,
  });

export const getMe = () => apiFetch<MeResponse>('/api/auth/me');

export const deleteMe = () => apiFetch<void>('/api/auth/me', { method: 'DELETE' });
