import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import ForgotPassword from './ForgotPassword';
import ResetPassword from './ResetPassword';

const renderForgot = () =>
  render(
    <MemoryRouter initialEntries={['/forgot-password']}>
      <Routes>
        <Route path="/forgot-password" element={<ForgotPassword />} />
      </Routes>
    </MemoryRouter>,
  );

const renderReset = (search = '?token=tok-123') =>
  render(
    <MemoryRouter initialEntries={[`/reset-password${search}`]}>
      <Routes>
        <Route path="/reset-password" element={<ResetPassword />} />
        <Route path="/login" element={<p>sign in page</p>} />
      </Routes>
    </MemoryRouter>,
  );

describe('ForgotPassword', () => {
  it('shows the same confirmation whether or not the address exists', async () => {
    // The server answers 202 for every address on purpose. If the client distinguished them it
    // would rebuild the account-existence oracle the server took care to avoid, so the only
    // honest client behaviour is one outcome.
    server.use(http.post('/api/auth/password/forgot', () => new HttpResponse(null, { status: 202 })));

    renderForgot();
    await userEvent.type(screen.getByLabelText(/email/i), 'nobody@example.com');
    await userEvent.click(screen.getByRole('button', { name: /send reset link/i }));

    expect(await screen.findByText(/check your email/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('surfaces a rate limit rather than pretending the email was sent', async () => {
    // A 429 means the request never happened. Claiming success would leave the user waiting
    // for an email that is not coming.
    server.use(http.post('/api/auth/password/forgot', () => new HttpResponse(null, { status: 429 })));

    renderForgot();
    await userEvent.type(screen.getByLabelText(/email/i), 'someone@example.com');
    await userEvent.click(screen.getByRole('button', { name: /send reset link/i }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.queryByText(/check your email/i)).not.toBeInTheDocument();
  });
});

describe('ResetPassword', () => {
  it('says the link is incomplete when the token is missing, before asking for a password', async () => {
    renderReset('');

    // Mail clients truncate long links. Discovering that after choosing and typing a password
    // twice is the worst moment to find out, and nothing on the page can fix it.
    expect(await screen.findByText(/link is incomplete/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/new password/i)).not.toBeInTheDocument();
  });

  it('keeps submit disabled until the password is valid AND confirmed', async () => {
    renderReset();
    const submit = screen.getByRole('button', { name: /set new password/i });

    expect(submit).toBeDisabled();

    await userEvent.type(screen.getByLabelText('New password'), 'Strong-Pass-99!');
    expect(submit).toBeDisabled(); // valid, but unconfirmed

    await userEvent.type(screen.getByLabelText(/confirm/i), 'Strong-Pass-99!');
    expect(submit).toBeEnabled();
  });

  it('flags a mismatched confirmation', async () => {
    renderReset();

    await userEvent.type(screen.getByLabelText('New password'), 'Strong-Pass-99!');
    await userEvent.type(screen.getByLabelText(/confirm/i), 'Strong-Pass-98!');

    expect(await screen.findByText(/do not match/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /set new password/i })).toBeDisabled();
  });

  it('lands on sign-in rather than signing the user in', async () => {
    server.use(http.post('/api/auth/password/reset', () => new HttpResponse(null, { status: 204 })));

    renderReset();
    await userEvent.type(screen.getByLabelText('New password'), 'Strong-Pass-99!');
    await userEvent.type(screen.getByLabelText(/confirm/i), 'Strong-Pass-99!');
    await userEvent.click(screen.getByRole('button', { name: /set new password/i }));

    // The reset revoked every session for this account. Quietly issuing a fresh one here would
    // contradict the thing that makes a reset worth doing when someone else had access.
    expect(await screen.findByText('sign in page')).toBeInTheDocument();
  });

  it('shows the server message when the token is rejected', async () => {
    server.use(http.post('/api/auth/password/reset', () =>
      HttpResponse.json({ title: 'This reset link is invalid or has expired.' }, { status: 400 })));

    renderReset();
    await userEvent.type(screen.getByLabelText('New password'), 'Strong-Pass-99!');
    await userEvent.type(screen.getByLabelText(/confirm/i), 'Strong-Pass-99!');
    await userEvent.click(screen.getByRole('button', { name: /set new password/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/invalid or has expired/i);
    expect(screen.queryByText('sign in page')).not.toBeInTheDocument();
  });
});
