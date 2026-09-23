import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import type { FilterCondition } from '../../api/issues';
import { ConditionRow } from './ConditionRow';

function renderRow(initial: FilterCondition) {
  const seen: FilterCondition[] = [];
  function Harness() {
    const [condition, setCondition] = useState(initial);
    return (
      <ConditionRow
        projectId="p1"
        catalog={FILTER_CATALOG}
        condition={condition}
        label="1"
        onChange={(next) => { seen.push(next); setCondition(next); }}
      />
    );
  }
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Harness />
    </QueryClientProvider>,
  );
  return { last: () => seen.at(-1) };
}

function mockValues() {
  server.use(http.get('/api/projects/p1/filter-values', () => HttpResponse.json({
    field: 'status',
    values: [{ name: 'Backlog', count: 42 }, { name: 'In Progress', count: 5 }],
    emptyCount: 7,
  })));
}

function failValues() {
  server.use(http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })));
}

const waitForPicker = () =>
  waitFor(() => expect(screen.getByLabelText('Value for condition 1').tagName).toBe('SELECT'));

describe('ConditionRow', () => {
  it('names operators in words, in the catalog’s order', () => {
    renderRow({ field: 'jiraKey', op: 'Eq', value: '' });

    const ops = within(screen.getByLabelText('Operator for condition 1')).getAllByRole('option').map((o) => o.textContent);
    expect(ops).toEqual(['is', 'is any of', 'is none of', 'contains', 'starts with']);
  });

  it('offers (Empty) first, then each value with its count', async () => {
    mockValues();
    renderRow({ field: 'status', op: 'Eq', value: '' });
    await waitForPicker();

    const options = within(screen.getByLabelText('Value for condition 1')).getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual(['— choose —', '(Empty) (7)', 'Backlog (42)', 'In Progress (5)']);
  });

  it('turns "is (Empty)" into "is empty" and "is not (Empty)" into "is not empty"', async () => {
    mockValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'Eq', value: '' });
    await waitForPicker();

    await user.selectOptions(screen.getByLabelText('Value for condition 1'), '(Empty) (7)');
    expect(row.last()).toEqual({ field: 'status', op: 'IsNull' });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Neq');
    await user.selectOptions(screen.getByLabelText('Value for condition 1'), '(Empty) (7)');
    expect(row.last()).toEqual({ field: 'status', op: 'IsNotNull' });
  });

  it('picks several values for "is any of", (Empty) included, and never one twice', async () => {
    mockValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'In', values: [] });

    await user.click(await screen.findByText('Choose values'));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));
    await user.click(screen.getByRole('checkbox', { name: /^\(empty\)/i }));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));

    expect(row.last()).toEqual({ field: 'status', op: 'In', values: [null, 'Backlog'] });
  });

  it('keeps the chosen list when flipping "is any of" to "is none of"', async () => {
    mockValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'In', values: ['Backlog'] });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'NotIn');

    expect(row.last()).toEqual({ field: 'status', op: 'NotIn', values: ['Backlog'] });
  });

  it('falls back to typing when the value list cannot be loaded, without splitting names', async () => {
    failValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'In', values: [] });

    await user.type(await screen.findByLabelText('Value for condition 1'), 'In Progress{Enter}');

    expect(row.last()).toEqual({ field: 'status', op: 'In', values: ['In Progress'] });
  });

  it('keeps an (Empty) that arrived in a link when the fallback is used', async () => {
    failValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'sprint', op: 'In', values: [null] });

    await user.type(await screen.findByLabelText('Value for condition 1'), 'S1{Enter}');

    expect(row.last()).toEqual({ field: 'sprint', op: 'In', values: [null, 'S1'] });
  });

  it('splits a pasted list of keys into upper-cased chips, once each', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'jiraKey', op: 'NotIn', values: [] });

    await user.click(screen.getByLabelText('Value for condition 1'));
    await user.paste('acme-1, ACME-2\nacme-1');

    expect(row.last()).toEqual({ field: 'jiraKey', op: 'NotIn', values: ['ACME-1', 'ACME-2'] });
  });

  it('asks for no value on "is not empty"', () => {
    renderRow({ field: 'summary', op: 'IsNotNull' });

    expect(screen.queryByLabelText('Value for condition 1')).toBeNull();
  });

  it('sends a typed value for "does not contain"', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'summary', op: 'NotContains', value: '' });

    await user.type(screen.getByLabelText('Value for condition 1'), 'spike');

    expect(row.last()).toEqual({ field: 'summary', op: 'NotContains', value: 'spike' });
  });

  it('marks a field the catalog no longer has, instead of silently showing another', () => {
    renderRow({ field: 'retired', op: 'Eq', value: 'x' });

    const select = screen.getByLabelText('Field for condition 1');
    expect(select).toHaveValue('retired');
    expect(select).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('alert')).toHaveTextContent(/can no longer be filtered on/i);
  });
});
