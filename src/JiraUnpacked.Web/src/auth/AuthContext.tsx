import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { getMe, login as loginRequest, logoutRequest } from '../api/auth';
import {
  clearTokens, installAuthBridge, refreshOnce,
  safeGetItem, safeRemoveItem, safeSetItem, setTokens, USERNAME_KEY,
} from './session';

export type AuthStatus = 'loading' | 'authenticated' | 'anonymous';

interface AuthValue {
  status: AuthStatus;
  role: string | null;
  username: string | null;
  signIn: (emailOrUsername: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('loading');
  const [role, setRole] = useState<string | null>(null);
  const [username, setUsername] = useState<string | null>(null);

  const reset = useCallback(() => {
    clearTokens();
    safeRemoveItem(USERNAME_KEY);
    setRole(null);
    setUsername(null);
    setStatus('anonymous');
  }, []);

  useEffect(() => {
    installAuthBridge(reset);
  }, [reset]);

  // Boot: always attempt a refresh. The cookie is httpOnly, so there is nothing
  // readable to check first — the server's answer IS the check, and an anonymous
  // visitor simply gets one 401. refreshOnce() is single-flight, which is also
  // what makes StrictMode's double-invoked effect safe here.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      const ok = await refreshOnce();
      if (cancelled) return;
      if (!ok) { reset(); return; }
      try {
        const me = await getMe();
        if (cancelled) return;
        setRole(me.role);
        // /api/auth/me carries no display name (sub is a GUID, not a
        // username) — restore what the user typed at sign-in instead.
        setUsername(safeGetItem(USERNAME_KEY));
        setStatus('authenticated');
      } catch {
        if (!cancelled) reset();
      }
    })();
    return () => { cancelled = true; };
  }, [reset]);

  const signIn = useCallback(async (emailOrUsername: string, password: string) => {
    setTokens(await loginRequest(emailOrUsername, password));
    const me = await getMe();
    setRole(me.role);
    safeSetItem(USERNAME_KEY, emailOrUsername);
    setUsername(emailOrUsername);
    setStatus('authenticated');
  }, []);

  const signOut = useCallback(async () => {
    try {
      // The server revokes the token AND clears the cookie — being httpOnly, it
      // is the only party that can.
      await logoutRequest();
    } catch {
      // Revoking server-side is best effort; the local session must end
      // regardless, so a failed call never leaves the user signed in.
    } finally {
      reset();
    }
  }, [reset]);

  const value = useMemo<AuthValue>(
    () => ({ status, role, username, signIn, signOut }),
    [status, role, username, signIn, signOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>.');
  return value;
}
