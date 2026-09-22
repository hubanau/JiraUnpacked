import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import Register from './Register';

function renderRegister() {
  return render(
    <MemoryRouter initialEntries={['/register']}>
      <Routes>
        <Route path="/register" element={<Register />} />
        <Route path="/verify" element={<p>verify page</p>} />
      </Routes>
    </MemoryRouter>,
  );
}

async function fill(user: ReturnType<typeof userEvent.setup>, email: string, username: string) {
  await user.type(screen.getByLabelText(/invite code/i), 'invite-123');
  await user.type(screen.getByLabelText(/^email$/i), email);
  await user.type(screen.getByLabelText(/^username$/i), username);
  await user.type(screen.getByLabelText(/^password$/i), 'Correct-Horse-9!');
}

describe('Register', () => {
  it('rejects a username containing @ before anything is sent', async () => {
    let posted = false;
    server.use(http.post('/api/auth/register', () => {
      posted = true;
      return HttpResponse.json({});
    }));
    const user = userEvent.setup();
    renderRegister();

    await fill(user, 'alice@example.com', 'alice@example.com');
    await user.tab();

    expect(await screen.findByRole('alert')).toHaveTextContent(/can't contain '@'/i);
    expect(screen.getByRole('button', { name: /create account/i })).toBeDisabled();
    expect(posted).toBe(false);
  });

  it('flags an invalid email once the field is left', async () => {
    const user = userEvent.setup();
    renderRegister();

    await user.type(screen.getByLabelText(/^email$/i), 'not-an-email');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    await user.tab();

    expect(await screen.findByRole('alert')).toHaveTextContent(/valid email/i);
  });

  it('shows the username rules as a hint', () => {
    renderRegister();
    expect(screen.getByLabelText(/^username$/i)).toHaveAccessibleDescription(/3–32 letters/);
  });

  it('submits valid input and moves on to verification', async () => {
    let body: unknown = null;
    server.use(http.post('/api/auth/register', async ({ request }) => {
      body = await request.json();
      return HttpResponse.json({ message: 'ok' });
    }));
    const user = userEvent.setup();
    renderRegister();

    await fill(user, 'alice@example.com', 'alice.smith');
    await user.click(screen.getByRole('button', { name: /create account/i }));

    expect(await screen.findByText('verify page')).toBeInTheDocument();
    expect(body).toMatchObject({ email: 'alice@example.com', username: 'alice.smith' });
  });

  it('shows the server problem title when the server disagrees', async () => {
    server.use(http.post('/api/auth/register', () =>
      HttpResponse.json({ title: 'Enter a valid email address.' }, { status: 400 })));
    const user = userEvent.setup();
    renderRegister();

    await fill(user, 'alice@example', 'alice.smith');
    await user.click(screen.getByRole('button', { name: /create account/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a valid email address.');
  });
});
