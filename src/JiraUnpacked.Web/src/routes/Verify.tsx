import { useEffect, useState, type FormEvent } from 'react';
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { Button, Field } from '../ui';
import { AuthLayout } from '../shell/AuthLayout';
import { ApiError } from '../api/errors';
import { resendVerification, verify } from '../api/auth';

const RESEND_COOLDOWN_SECONDS = 60;

export default function Verify() {
  const location = useLocation();
  const [searchParams] = useSearchParams();
  // Prefer the email passed via router state (Register navigates this way so
  // the address doesn't land in the URL, server access logs, or browser
  // history). Fall back to the ?email= query string so an existing or
  // bookmarked /verify?email=... link still works.
  const stateEmail = (location.state as { email?: string } | null)?.email;
  const email = stateEmail ?? searchParams.get('email') ?? '';
  const navigate = useNavigate();

  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [detail, setDetail] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [resendError, setResendError] = useState<string | null>(null);
  const [resendDetail, setResendDetail] = useState<string | null>(null);
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
    setDetail(null);
    try {
      await verify(email, code);
      navigate('/login');
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.title);
        setDetail(err.detail ?? null);
      } else {
        setError('Verification failed.');
        setDetail(null);
      }
      setBusy(false);
    }
  }

  async function resend() {
    setResendBusy(true);
    setResendError(null);
    setResendDetail(null);
    try {
      await resendVerification(email);
      setSecondsLeft(RESEND_COOLDOWN_SECONDS);
    } catch (err) {
      if (err instanceof ApiError) {
        setResendError(err.title);
        setResendDetail(err.detail ?? null);
      } else {
        setResendError('Could not resend the code.');
        setResendDetail(null);
      }
    } finally {
      setResendBusy(false);
    }
  }

  return (
    <AuthLayout title="Verify your email" description={`We sent a verification code to ${email}.`}>
      <form onSubmit={submit} className="flex flex-col gap-4">
        {error && (
          <div role="alert" className="rounded-[var(--ju-radius)] border border-[var(--ju-danger)]/25 bg-[var(--ju-danger-soft)] px-3 py-2">
            <p className="text-sm font-medium text-[var(--ju-danger)]">{error}</p>
            {detail && <p className="mt-0.5 text-xs text-[var(--ju-danger)]">{detail}</p>}
          </div>
        )}
        <Field label="Verification code" id="code">
          <input id="code" autoComplete="one-time-code" value={code}
                 onChange={(e) => setCode(e.target.value)} />
        </Field>
        <Button type="submit" loading={busy} className="w-full">Verify</Button>
      </form>
      {resendError && (
        <div role="alert">
          <p>{resendError}</p>
          {resendDetail && <p>{resendDetail}</p>}
        </div>
      )}
      <Button
        type="button"
        variant="secondary"
        loading={resendBusy}
        disabled={secondsLeft > 0}
        onClick={resend}
      >
        {secondsLeft > 0 ? `Resend in ${secondsLeft}s` : 'Resend code'}
      </Button>
    </AuthLayout>
  );
}
