import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { AuthProvider } from '../auth/AuthContext';
import Login from './Login';

function renderLogin() {
  return render(
    <MemoryRouter>
      <AuthProvider><Login /></AuthProvider>
    </MemoryRouter>,
  );
}

describe('Login', () => {
  it('shows the server message when credentials are rejected', async () => {
    server.use(http.post('/api/auth/login', () =>
      HttpResponse.json({ title: 'Invalid credentials.' }, { status: 401 })));
    const user = userEvent.setup();
    renderLogin();

    await user.type(screen.getByLabelText(/email or username/i), 'someone');
    await user.type(screen.getByLabelText(/password/i), 'whatever12A!');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid credentials.');
  });

  it('shows the rate-limit message on 429 rather than a generic failure', async () => {
    server.use(http.post('/api/auth/login', () => new HttpResponse(null, { status: 429 })));
    const user = userEvent.setup();
    renderLogin();

    await user.type(screen.getByLabelText(/email or username/i), 'someone');
    await user.type(screen.getByLabelText(/password/i), 'whatever12A!');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/too many attempts/i);
  });
});
