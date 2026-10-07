import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { HistoryFieldSelect } from './HistoryFieldSelect';

function renderSelect(initial = '') {
  const seen: string[] = [];
  function Harness() {
    const [value, setValue] = useState(initial);
    return <HistoryFieldSelect projectId="p1" label="1" value={value} onChange={(next) => { seen.push(next); setValue(next); }} />;
  }
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Harness />
    </QueryClientProvider>,
  );
  return { last: () => seen.at(-1) };
}

const fieldList = () => server.use(http.get('/api/projects/p1/history-fields', () =>
  HttpResponse.json({ fields: [{ name: 'Sprint', count: 42 }, { name: 'status', count: 7 }] })));

const waitForSelect = () =>
  waitFor(() => expect(screen.getByLabelText('History field for condition 1').tagName).toBe('SELECT'));

describe('HistoryFieldSelect', () => {
  it('lists this project’s history fields with their counts, most-changed first', async () => {
    fieldList();
    const user = userEvent.setup();
    const select = renderSelect();
    await waitForSelect();

    expect(within(screen.getByLabelText('History field for condition 1')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['— choose —', 'Sprint (42)', 'status (7)']);
    await user.selectOptions(screen.getByLabelText('History field for condition 1'), 'Sprint');
    expect(select.last()).toBe('Sprint');
  });

  it('keeps a field from a link that this project’s changelog does not have', async () => {
    fieldList();
    renderSelect('Story Points');
    await waitForSelect();

    expect(screen.getByLabelText('History field for condition 1')).toHaveValue('Story Points');
    expect(screen.getByRole('option', { name: 'Story Points (0)' })).toBeInTheDocument();
  });

  it('falls back to typing the name when the list fails', async () => {
    server.use(http.get('/api/projects/p1/history-fields', () => new HttpResponse(null, { status: 500 })));
    const user = userEvent.setup();
    const select = renderSelect();

    expect(await screen.findByText(/type the name as Jira reports it/i)).toBeInTheDocument();
    await user.type(screen.getByLabelText('History field for condition 1'), 'Sprint');
    expect(select.last()).toBe('Sprint');
  });
});
