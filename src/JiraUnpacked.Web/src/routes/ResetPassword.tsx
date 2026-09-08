import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { Button, Field } from '../ui';
import { AuthLayout } from '../shell/AuthLayout';
import { ApiError } from '../api/errors';
import { resetPassword } from '../api/auth';
import { MIN_CLASSES, MIN_LENGTH, assessPassword } from '../auth/password';

export default function ResetPassword() {
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const navigate = useNavigate();

  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [detail, setDetail] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const assessment = assessPassword(password);
  const matches = password.length > 0 && password === confirm;

  // Said up front rather than after the user has chosen and typed a password twice. A link
  // that was truncated by a mail client is the common cause, and nothing on this page can fix it.
  if (!token) {
    return (
      <AuthLayout
        title="This link is incomplete"
        description="The reset link is missing its token. Mail clients sometimes cut long links in half."
        footer={<Link to="/forgot-password">Request a new link</Link>}
      >
        <p className="text-sm text-[var(--ju-muted)]">
          Open the link directly from the email, or request a fresh one.
        </p>
      </AuthLayout>
    );
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setDetail(null);
    try {
      await resetPassword(token, password);
      // Deliberately NOT signed in here. The reset just revoked every session for this
      // account; quietly issuing a new one would contradict the thing that makes a reset
      // worth doing when someone else had access.
      navigate('/login', { replace: true, state: { passwordReset: true } });
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.title);
        setDetail(err.detail ?? null);
      } else {
        setError('Could not reset the password.');
      }
      setBusy(false);
    }
  }

  return (
    <AuthLayout
      title="Choose a new password"
      footer={<Link to="/login">Back to sign in</Link>}
    >
      <form onSubmit={submit} className="flex flex-col gap-4">
        {error && (
          <div role="alert" className="rounded-[var(--ju-radius)] border border-[var(--ju-danger)]/25 bg-[var(--ju-danger-soft)] px-3 py-2">
            <p className="text-sm font-medium text-[var(--ju-danger)]">{error}</p>
            {detail && <p className="mt-0.5 text-xs text-[var(--ju-danger)]">{detail}</p>}
          </div>
        )}
        <Field label="New password" id="password">
          <input
            id="password"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>
        <Field
          label="Confirm new password"
          id="confirm"
          error={confirm.length > 0 && !matches ? 'Passwords do not match.' : undefined}
        >
          <input
            id="confirm"
            type="password"
            autoComplete="new-password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
          />
        </Field>
        <ul aria-live="polite" className="flex flex-col gap-1 text-xs text-[var(--ju-muted)]">
          <li style={{ color: assessment.longEnough ? 'var(--ju-success)' : undefined }}>
            {assessment.longEnough ? '✓' : '○'} At least {MIN_LENGTH} characters
          </li>
          <li style={{ color: assessment.enoughClasses ? 'var(--ju-success)' : undefined }}>
            {assessment.enoughClasses ? '✓' : '○'} At least {MIN_CLASSES} of: lowercase, uppercase, digit, symbol
          </li>
        </ul>
        <Button
          type="submit"
          loading={busy}
          disabled={!assessment.valid || !matches}
          className="w-full"
        >
          Set new password
        </Button>
      </form>
    </AuthLayout>
  );
}
