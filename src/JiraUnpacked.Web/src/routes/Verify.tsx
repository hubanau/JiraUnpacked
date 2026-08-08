import { useEffect, useState, type FormEvent } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { Button, Card, Field } from '../ui';
import { ApiError } from '../api/errors';
import { resendVerification, verify } from '../api/auth';

const RESEND_COOLDOWN_SECONDS = 60;

export default function Verify() {
  const [searchParams] = useSearchParams();
  const email = searchParams.get('email') ?? '';
  const navigate = useNavigate();

  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [resendError, setResendError] = useState<string | null>(null);
  const [resendBusy, setResendBusy] = useState(false);
  const [secondsLeft, setSecondsLeft] = useState(0);

  // Ticks the countdown down to zero, one second at a time. Each render
  // schedules exactly one timeout and cleans it up — on the next tick, and
  // on unmount — so nothing keeps running once the component is gone.
  useEffect(() => {
    if (secondsLeft <= 0) return;
    const timer = setTimeout(() => setSecondsLeft((s) => s - 1), 1000);
    return () => clearTimeout(timer);
  }, [secondsLeft]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await verify(email, code);
      navigate('/login');
    } catch (err) {
      setError(err instanceof ApiError ? err.title : 'Verification failed.');
      setBusy(false);
    }
  }

  async function resend() {
    setResendBusy(true);
    setResendError(null);
    try {
      await resendVerification(email);
      setSecondsLeft(RESEND_COOLDOWN_SECONDS);
    } catch (err) {
      setResendError(err instanceof ApiError ? err.title : 'Could not resend the code.');
    } finally {
      setResendBusy(false);
    }
  }

  return (
    <Card>
      <form onSubmit={submit}>
        <h1>Verify your email</h1>
        <p>We sent a verification code to {email}.</p>
        {error && <p role="alert">{error}</p>}
        <Field label="Verification code" id="code">
          <input id="code" autoComplete="one-time-code" value={code}
                 onChange={(e) => setCode(e.target.value)} />
        </Field>
        <Button type="submit" loading={busy}>Verify</Button>
      </form>
      {resendError && <p role="alert">{resendError}</p>}
      <Button
        type="button"
        variant="secondary"
        loading={resendBusy}
        disabled={secondsLeft > 0}
        onClick={resend}
      >
        {secondsLeft > 0 ? `Resend in ${secondsLeft}s` : 'Resend code'}
      </Button>
    </Card>
  );
}
