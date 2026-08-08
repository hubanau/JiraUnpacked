import { useState, type FormEvent } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { Button, Card, Field } from '../ui';
import { ApiError } from '../api/errors';
import { useAuth } from '../auth/AuthContext';

export default function Login() {
  const { signIn } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [emailOrUsername, setEmailOrUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await signIn(emailOrUsername, password);
      // Return the user to whatever they originally asked for.
      const from = (location.state as { from?: string } | null)?.from ?? '/';
      navigate(from, { replace: true });
    } catch (err) {
      setError(err instanceof ApiError ? err.title : 'Sign-in failed.');
      setBusy(false);
    }
  }

  return (
    <Card>
      <form onSubmit={submit}>
        <h1>Sign in</h1>
        {error && <p role="alert">{error}</p>}
        <Field label="Email or username" id="identifier">
          <input id="identifier" autoComplete="username" value={emailOrUsername}
                 onChange={(e) => setEmailOrUsername(e.target.value)} />
        </Field>
        <Field label="Password" id="password">
          <input id="password" type="password" autoComplete="current-password" value={password}
                 onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Button type="submit" loading={busy}>Sign in</Button>
        <Link to="/register">Have an invite code? Create an account</Link>
      </form>
    </Card>
  );
}
