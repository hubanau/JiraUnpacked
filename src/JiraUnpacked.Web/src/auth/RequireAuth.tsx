import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { Spinner } from '../ui';
import { useAuth } from './AuthContext';

export function RequireAuth() {
  const { status } = useAuth();
  const location = useLocation();

  // 'loading' is a distinct state on purpose: redirecting during the boot
  // refresh would bounce a returning user to the sign-in page for a moment.
  if (status === 'loading') {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <Spinner aria-label="Restoring your session" />
      </div>
    );
  }

  if (status === 'anonymous') {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  return <Outlet />;
}
