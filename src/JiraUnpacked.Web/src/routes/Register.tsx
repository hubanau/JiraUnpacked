import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Button, Field } from '../ui';
import { AuthLayout } from '../shell/AuthLayout';
import { ApiError } from '../api/errors';
import { register } from '../api/auth';
import { MIN_CLASSES, MIN_LENGTH, assessPassword } from '../auth/password';
import {
  EMAIL_MAX_LENGTH, USERNAME_HINT, USERNAME_MAX_LENGTH, emailProblem, usernameProblem,
} from '../auth/accountInput';

export default function Register() {
  const navigate = useNavigate();
  const [inviteCode, setInviteCode] = useState('');
  const [email, setEmail] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [detail, setDetail] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // A field's problem is shown once the user has left it, not on the first keystroke.
  const [touched, setTouched] = useState({ email: false, username: false });

  const assessment = assessPassword(password);
  const emailIssue = emailProblem(email);
  const usernameIssue = usernameProblem(username);
  const canSubmit = assessment.valid && !emailIssue && !usernameIssue;

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!canSubmit) {
      setTouched({ email: true, username: true });
      return;
    }
    setBusy(true);
    setError(null);
    setDetail(null);
    try {
      await register(inviteCode, email, username, password);
      // Router state, not a query string: a query string would put the
      // email in server access logs and browser history. Verify.tsx still
      // falls back to ?email=... for a bookmarked/existing link.
      navigate('/verify', { state: { email } });
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.title);
        setDetail(err.detail ?? null);
      } else {
        setError('Registration failed.');
      }
      setBusy(false);
    }
  }

  return (
    <AuthLayout
      title="Create account"
      description="Registration is invite-only."
      footer={<Link to="/login">Already have an account? Sign in</Link>}
    >
      <form onSubmit={submit} className="flex flex-col gap-4">
        {error && (
          <div role="alert" className="rounded-[var(--ju-radius)] border border-[var(--ju-danger)]/25 bg-[var(--ju-danger-soft)] px-3 py-2">
            <p className="text-sm font-medium text-[var(--ju-danger)]">{error}</p>
            {detail && <p className="mt-0.5 text-xs text-[var(--ju-danger)]">{detail}</p>}
          </div>
        )}
        <Field label="Invite code" id="inviteCode">
          <input id="inviteCode" autoComplete="off" value={inviteCode}
                 onChange={(e) => setInviteCode(e.target.value)} />
        </Field>
        <Field label="Email" id="email"
               error={touched.email && emailIssue ? emailIssue : undefined}>
          <input id="email" type="email" autoComplete="email" maxLength={EMAIL_MAX_LENGTH} value={email}
                 onChange={(e) => setEmail(e.target.value)}
                 onBlur={() => setTouched((t) => ({ ...t, email: true }))} />
        </Field>
        <Field label="Username" id="username" hint={USERNAME_HINT}
               error={touched.username && usernameIssue ? usernameIssue : undefined}>
          <input id="username" autoComplete="username" maxLength={USERNAME_MAX_LENGTH} value={username}
                 onChange={(e) => setUsername(e.target.value)}
                 onBlur={() => setTouched((t) => ({ ...t, username: true }))} />
        </Field>
        <Field label="Password" id="password">
          <input id="password" type="password" autoComplete="new-password" value={password}
                 onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <ul aria-live="polite" className="flex flex-col gap-1 text-xs text-[var(--ju-muted)]">
          <li style={{ color: assessment.longEnough ? 'var(--ju-success)' : undefined }}>
            {assessment.longEnough ? '✓' : '○'} At least {MIN_LENGTH} characters
          </li>
          <li style={{ color: assessment.enoughClasses ? 'var(--ju-success)' : undefined }}>
            {assessment.enoughClasses ? '✓' : '○'} At least {MIN_CLASSES} of: lowercase, uppercase, digit, symbol
          </li>
        </ul>
        <Button type="submit" loading={busy} disabled={!canSubmit} className="w-full">Create account</Button>
      </form>
    </AuthLayout>
  );
}
