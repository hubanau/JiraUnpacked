import { apiFetch } from './client';

/**
 * No refresh token: it is delivered only as an httpOnly cookie the browser stores and
 * replays, so script never sees it — not here, and not at sign-in.
 */
export interface TokenResponse {
  accessToken: string;
  expiresAtUtc: string;
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

/** Bodyless: the token travels in the cookie, which the browser attaches. */
export const refreshTokens = () =>
  apiFetch<TokenResponse>('/api/auth/refresh', { method: 'POST', skipAuthRetry: true });

/** Bodyless, and the server clears the cookie — the only thing that can. */
export const logoutRequest = () =>
  apiFetch<void>('/api/auth/logout', { method: 'POST', skipAuthRetry: true });

/**
 * Always resolves when the server is reachable, whatever the address — the endpoint answers
 * 202 unconditionally so it cannot be used to discover which addresses are registered. The
 * client must not add a distinction the server deliberately withheld.
 */
export const forgotPassword = (email: string) =>
  apiFetch<void>('/api/auth/password/forgot', {
    method: 'POST', body: { email }, skipAuthRetry: true,
  });

export const resetPassword = (token: string, newPassword: string) =>
  apiFetch<void>('/api/auth/password/reset', {
    method: 'POST', body: { token, newPassword }, skipAuthRetry: true,
  });

export const getMe = () => apiFetch<MeResponse>('/api/auth/me');

export const deleteMe = () => apiFetch<void>('/api/auth/me', { method: 'DELETE' });
