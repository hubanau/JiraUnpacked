import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import AddProject from './AddProject';

function renderAddProject() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/connections/c1/projects/new']}>
        <Routes>
          <Route path="/connections/:connectionId/projects/new" element={<AddProject />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

// Every test in this file needs the picker populated; the specific
// POST behaviour is what varies per test, declared via server.use below.
beforeEach(() => {
  server.use(
    http.get('/api/connections/c1/projects-available', () => HttpResponse.json([
      { id: '1', key: 'ACME', name: 'Acme Platform' },
      { id: '2', key: 'OPS', name: 'Operations' },
    ])),
  );
});

async function pickProjectAndSubmit(user: ReturnType<typeof userEvent.setup>) {
  await user.selectOptions(await screen.findByLabelText(/jira project/i), 'ACME');
  await user.click(screen.getByRole('button', { name: /add project/i }));
}

describe('AddProject', () => {
  it('offers the connection’s real projects as options, not a text field', async () => {
    renderAddProject();
    expect(await screen.findByRole('option', { name: 'ACME — Acme Platform' })).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: /project/i })).not.toBeInTheDocument();
  });

  it('reads a 409 as an already-added project', async () => {
    server.use(http.post('/api/connections/c1/projects', () => new HttpResponse(null, { status: 409 })));
    const user = userEvent.setup();
    renderAddProject();

    await pickProjectAndSubmit(user);

    expect(await screen.findByRole('alert')).toHaveTextContent(/already added/i);
  });

  it('rejects 59 client-side and accepts 60', async () => {
    const user = userEvent.setup();
    renderAddProject();
    const interval = await screen.findByLabelText(/sync interval/i);
    const submit = screen.getByRole('button', { name: /add project/i });

    await user.clear(interval);
    await user.type(interval, '59');
    expect(screen.getByRole('alert')).toHaveTextContent(/at least 60 minutes/i);
    expect(submit).toBeDisabled();

    await user.clear(interval);
    await user.type(interval, '60');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(submit).toBeEnabled();
  });

  it('still surfaces the server 400 when the client thought the interval was fine', async () => {
    server.use(http.post('/api/connections/c1/projects', () =>
      HttpResponse.json({ title: 'Sync interval must be at least 60 minutes.' }, { status: 400 })));
    const user = userEvent.setup();
    renderAddProject();

    await pickProjectAndSubmit(user);

    expect(await screen.findByRole('alert')).toHaveTextContent(/at least 60 minutes/i);
  });
});
