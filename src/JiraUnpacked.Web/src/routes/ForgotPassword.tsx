import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { Button, Field } from '../ui';
import { AuthLayout } from '../shell/AuthLayout';
import { ApiError } from '../api/errors';
import { forgotPassword } from '../api/auth';

export default function ForgotPassword() {
  const [email, setEmail] = useState('');
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await forgotPassword(email);
      setSent(true);
    } catch (err) {
      // Only a genuine failure to reach the endpoint surfaces here — rate limiting, or the
      // server being down. An unknown address is NOT an error: the server answers 202 for it
      // on purpose, and showing anything different would rebuild the account-existence oracle
      // on the client that the server took care to avoid.
      setError(err instanceof ApiError ? err.title : 'Could not send the reset email.');
    } finally {
      setBusy(false);
    }
  }

  if (sent) {
    return (
      <AuthLayout
        title="Check your email"
        description="If that address belongs to an active account, a reset link is on its way. The link expires shortly and can be used once."
        footer={<Link to="/login">Back to sign in</Link>}
      >
        <p className="text-sm text-[var(--ju-muted)]">
          Nothing arrived? Check the spam folder, or{' '}
          <button
            type="button"
            onClick={() => setSent(false)}
            className="text-[var(--ju-primary)] underline underline-offset-2"
          >
            try a different address
          </button>
          .
        </p>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout
      title="Reset your password"
      description="Enter the address you signed up with and we will send you a link."
      footer={<Link to="/login">Back to sign in</Link>}
    >
      <form onSubmit={submit} className="flex flex-col gap-4">
        {error && (
          <div role="alert" className="rounded-[var(--ju-radius)] border border-[var(--ju-danger)]/25 bg-[var(--ju-danger-soft)] px-3 py-2">
            <p className="text-sm font-medium text-[var(--ju-danger)]">{error}</p>
          </div>
        )}
        <Field label="Email" id="email">
          <input
            id="email"
            type="email"
            autoComplete="email"
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
        </Field>
        <Button type="submit" loading={busy} className="w-full">Send reset link</Button>
      </form>
    </AuthLayout>
  );
}
