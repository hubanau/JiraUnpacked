import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import AddConnection from './AddConnection';

function renderAddConnection() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <AddConnection />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function fillAndSubmit(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/jira site url/i), 'https://x.atlassian.net');
  await user.type(screen.getByLabelText(/^email$/i), 'a@b.c');
  await user.type(screen.getByLabelText(/api token/i), 'tok');
  await user.click(screen.getByRole('button', { name: /add connection/i }));
}

describe('AddConnection', () => {
  it('shows the credentials message on 400', async () => {
    server.use(http.post('/api/connections', () =>
      HttpResponse.json({ title: 'Jira credentials could not be validated.' }, { status: 400 })));
    const user = userEvent.setup();
    renderAddConnection();

    await fillAndSubmit(user);

    expect(await screen.findByRole('alert')).toHaveTextContent(/credentials could not be validated/i);
  });

  it('shows the upstream-Jira message on 502, not the credentials message', async () => {
    server.use(http.post('/api/connections', () => new HttpResponse(null, { status: 502 })));
    const user = userEvent.setup();
    renderAddConnection();

    await fillAndSubmit(user);

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/jira did not respond/i);
    expect(alert).not.toHaveTextContent(/credentials/i);
  });

  it('renders the API token as a password field', () => {
    renderAddConnection();
    expect(screen.getByLabelText(/api token/i)).toHaveAttribute('type', 'password');
  });
});
