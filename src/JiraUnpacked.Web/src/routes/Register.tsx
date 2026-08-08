import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Button, Card, Field } from '../ui';
import { ApiError } from '../api/errors';
import { register } from '../api/auth';
import { MIN_CLASSES, MIN_LENGTH, assessPassword } from '../auth/password';

export default function Register() {
  const navigate = useNavigate();
  const [inviteCode, setInviteCode] = useState('');
  const [email, setEmail] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [detail, setDetail] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const assessment = assessPassword(password);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!assessment.valid) return;
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
    <Card>
      <form onSubmit={submit}>
        <h1>Create account</h1>
        {error && (
          <div role="alert">
            <p>{error}</p>
            {detail && <p>{detail}</p>}
          </div>
        )}
        <Field label="Invite code" id="inviteCode">
          <input id="inviteCode" autoComplete="off" value={inviteCode}
                 onChange={(e) => setInviteCode(e.target.value)} />
        </Field>
        <Field label="Email" id="email">
          <input id="email" type="email" autoComplete="email" value={email}
                 onChange={(e) => setEmail(e.target.value)} />
        </Field>
        <Field label="Username" id="username">
          <input id="username" autoComplete="username" value={username}
                 onChange={(e) => setUsername(e.target.value)} />
        </Field>
        <Field label="Password" id="password">
          <input id="password" type="password" autoComplete="new-password" value={password}
                 onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <ul aria-live="polite" className="text-xs">
          <li style={{ color: assessment.longEnough ? 'var(--ju-success)' : undefined }}>
            {assessment.longEnough ? '✓' : '○'} At least {MIN_LENGTH} characters
          </li>
          <li style={{ color: assessment.enoughClasses ? 'var(--ju-success)' : undefined }}>
            {assessment.enoughClasses ? '✓' : '○'} At least {MIN_CLASSES} of: lowercase, uppercase, digit, symbol
          </li>
        </ul>
        <Button type="submit" loading={busy} disabled={!assessment.valid}>Create account</Button>
        <Link to="/login">Already have an account? Sign in</Link>
      </form>
    </Card>
  );
}
