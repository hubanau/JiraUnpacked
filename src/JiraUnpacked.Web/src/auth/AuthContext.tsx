import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { getMe, login as loginRequest, logoutRequest } from '../api/auth';
import { clearTokens, getRefreshToken, installAuthBridge, refreshOnce, setTokens } from './session';

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
    setRole(null);
    setUsername(null);
    setStatus('anonymous');
  }, []);

  useEffect(() => {
    installAuthBridge(reset);
  }, [reset]);

  // Boot: a stored refresh token means the session can be resumed without a
  // second sign-in. Anything else lands on the anonymous state.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      if (!getRefreshToken()) {
        if (!cancelled) setStatus('anonymous');
        return;
      }
      const ok = await refreshOnce();
      if (cancelled) return;
      if (!ok) { reset(); return; }
      try {
        const me = await getMe();
        if (cancelled) return;
        setRole(me.role);
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
    setUsername(emailOrUsername);
    setStatus('authenticated');
  }, []);

  const signOut = useCallback(async () => {
    const stored = getRefreshToken();
    try {
      if (stored) await logoutRequest(stored);
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
