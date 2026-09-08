import { useState, type FormEvent } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { Button, Field } from '../ui';
import { AuthLayout } from '../shell/AuthLayout';
import { ApiError } from '../api/errors';
import { useAuth } from '../auth/AuthContext';

export default function Login() {
  const { signIn } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [emailOrUsername, setEmailOrUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [detail, setDetail] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Set by ResetPassword on its way here. A reset revokes every session, so the user lands on
  // sign-in rather than signed in, and this says why instead of leaving it feeling like a failure.
  const justReset = (location.state as { passwordReset?: boolean } | null)?.passwordReset === true;

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setDetail(null);
    try {
      await signIn(emailOrUsername, password);
      // Return the user to whatever they originally asked for.
      const from = (location.state as { from?: string } | null)?.from ?? '/';
      navigate(from, { replace: true });
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.title);
        setDetail(err.detail ?? null);
      } else {
        setError('Sign-in failed.');
        setDetail(null);
      }
      setBusy(false);
    }
  }

  return (
    <AuthLayout
      title="Sign in"
      footer={<Link to="/register">Have an invite code? Create an account</Link>}
    >
      <form onSubmit={submit} className="flex flex-col gap-4">
        {justReset && (
          <div role="status" className="rounded-[var(--ju-radius)] border border-[var(--ju-success)]/25 bg-[var(--ju-success-soft)] px-3 py-2">
            <p className="text-sm font-medium text-[var(--ju-success)]">
              Password updated. Sign in with your new password.
            </p>
          </div>
        )}
        {error && (
          <div role="alert" className="rounded-[var(--ju-radius)] border border-[var(--ju-danger)]/25 bg-[var(--ju-danger-soft)] px-3 py-2">
            <p className="text-sm font-medium text-[var(--ju-danger)]">{error}</p>
            {detail && <p className="mt-0.5 text-xs text-[var(--ju-danger)]">{detail}</p>}
          </div>
        )}
        <Field label="Email or username" id="identifier">
          <input id="identifier" autoComplete="username" value={emailOrUsername}
                 onChange={(e) => setEmailOrUsername(e.target.value)} />
        </Field>
        <Field label="Password" id="password">
          <input id="password" type="password" autoComplete="current-password" value={password}
                 onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Button type="submit" loading={busy} className="w-full">Sign in</Button>
        <Link to="/forgot-password" className="text-center text-sm text-[var(--ju-primary)] underline underline-offset-2">
          Forgot your password?
        </Link>
      </form>
    </AuthLayout>
  );
}
