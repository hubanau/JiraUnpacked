import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
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

function mockSprintValues() {
  server.use(http.get('/api/projects/p1/filter-values', () => HttpResponse.json({
    field: 'sprint',
    values: [{ name: 'Sprint 7', count: 3 }],
    emptyCount: 2,
    smart: [
      { key: 'active', label: 'Active sprint', count: 3 },
      { key: 'future', label: 'Future sprints', count: 0 },
      { key: 'open', label: 'Open sprints (active + future)', count: 3 },
      { key: 'closed', label: 'Closed sprints', count: 9 },
    ],
  })));
}

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

  it('keeps a smart value that arrived in a link when the fallback is used', async () => {
    failValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'sprint', op: 'In', values: [{ smart: 'active' }] });

    await user.type(await screen.findByLabelText('Value for condition 1'), 'S1{Enter}');

    expect(row.last()).toEqual({ field: 'sprint', op: 'In', values: [{ smart: 'active' }, 'S1'] });
  });

  it('marks a field the catalog no longer has, instead of silently showing another', () => {
    renderRow({ field: 'retired', op: 'Eq', value: 'x' });

    const select = screen.getByLabelText('Field for condition 1');
    expect(select).toHaveValue('retired');
    expect(select).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('alert')).toHaveTextContent(/can no longer be filtered on/i);
  });
});

describe('ConditionRow — ranges and relative dates', () => {
  afterEach(() => vi.restoreAllMocks());

  const inZone = (timeZone: string) => {
    const real = Intl.DateTimeFormat().resolvedOptions();
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ ...real, timeZone });
  };

  it('names the date operators in words, in the catalog’s order', () => {
    renderRow({ field: 'createdAt', op: 'Lt', value: '' });

    const ops = within(screen.getByLabelText('Operator for condition 1')).getAllByRole('option').map((o) => o.textContent);
    expect(ops).toEqual(['<', '≤', '>', '≥', 'between', 'in the last', 'more than … ago', 'in period']);
  });

  it('takes a date range with either end left open', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'createdAt', op: 'Lt', value: '2026-01-01' });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Between');
    expect(row.last()).toEqual({ field: 'createdAt', op: 'Between', values: [null, null] });

    fireEvent.change(screen.getByLabelText('From for condition 1'), { target: { value: '2026-09-01' } });
    fireEvent.change(screen.getByLabelText('To for condition 1'), { target: { value: '2026-09-30' } });
    expect(row.last()).toEqual({ field: 'createdAt', op: 'Between', values: ['2026-09-01', '2026-09-30'] });

    fireEvent.change(screen.getByLabelText('From for condition 1'), { target: { value: '' } });
    expect(row.last()).toEqual({ field: 'createdAt', op: 'Between', values: [null, '2026-09-30'] });
    expect(screen.getByLabelText('To for condition 1')).toHaveAttribute('type', 'date');
  });

  it('takes a story-point range as numbers', () => {
    const row = renderRow({ field: 'storyPoints', op: 'Between', values: [null, null] });

    expect(screen.getByLabelText('From for condition 1')).toHaveAttribute('type', 'number');
    fireEvent.change(screen.getByLabelText('From for condition 1'), { target: { value: '1.5' } });
    fireEvent.change(screen.getByLabelText('To for condition 1'), { target: { value: '8' } });

    expect(row.last()).toEqual({ field: 'storyPoints', op: 'Between', values: [1.5, 8] });
  });

  it('starts "in the last" at 14 days and edits the amount and the unit', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'updatedAt', op: 'Lt', value: '' });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'InLast');
    expect(row.last()).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } });
    expect(screen.getByLabelText('Amount for condition 1')).toHaveValue(14);

    fireEvent.change(screen.getByLabelText('Amount for condition 1'), { target: { value: '30' } });
    await user.selectOptions(screen.getByLabelText('Unit for condition 1'), 'weeks');
    expect(row.last()).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 30, unit: 'weeks' } });

    fireEvent.change(screen.getByLabelText('Amount for condition 1'), { target: { value: '' } });
    expect(row.last()).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 0, unit: 'weeks' } });
    expect(screen.getByLabelText('Amount for condition 1')).toHaveValue(null);
  });

  it('reads "more than [14] [days] ago" and keeps the amount when flipped', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'updatedAt', op: 'InLast', value: { amount: 3, unit: 'months' } });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'NotInLast');

    expect(row.last()).toEqual({ field: 'updatedAt', op: 'NotInLast', value: { amount: 3, unit: 'months' } });
    expect(screen.getByText('ago')).toBeInTheDocument();
    expect(within(screen.getByLabelText('Unit for condition 1')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['hours', 'days', 'weeks', 'months']);
  });

  it('captures the browser’s zone with "in period", shows it, and keeps it when the period changes', async () => {
    inZone('Europe/Warsaw');
    const user = userEvent.setup();
    const row = renderRow({ field: 'createdAt', op: 'Lt', value: '' });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'InPeriod');
    expect(row.last()).toEqual({ field: 'createdAt', op: 'InPeriod', value: { period: 'thisWeek', timeZone: 'Europe/Warsaw' } });
    expect(screen.getByText('Europe/Warsaw')).toBeInTheDocument();

    const periods = within(screen.getByLabelText('Period for condition 1')).getAllByRole('option').map((o) => o.textContent);
    expect(periods).toEqual([
      'Today', 'Yesterday', 'This week', 'Last week', 'This month', 'Last month',
      'This quarter', 'Last quarter', 'This year', 'Last year',
    ]);

    inZone('America/New_York');   // the zone was captured when the operator was chosen, not now
    await user.selectOptions(screen.getByLabelText('Period for condition 1'), 'lastMonth');
    expect(row.last()).toEqual({ field: 'createdAt', op: 'InPeriod', value: { period: 'lastMonth', timeZone: 'Europe/Warsaw' } });
  });

  it('keeps a zoneless period from a link zoneless — it means UTC and says so', async () => {
    inZone('Europe/Warsaw');
    const user = userEvent.setup();
    const row = renderRow({ field: 'updatedAt', op: 'InPeriod', value: { period: 'today' } });

    expect(screen.getByText('UTC')).toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('Period for condition 1'), 'yesterday');

    expect(row.last()).toEqual({ field: 'updatedAt', op: 'InPeriod', value: { period: 'yesterday' } });
  });
});

describe('ConditionRow — smart sprint values', () => {
  it('picks a smart value for "is", and keeps it when flipped to "is not"', async () => {
    mockSprintValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'sprint', op: 'Eq', value: '' });
    await waitForPicker();

    await user.selectOptions(screen.getByLabelText('Value for condition 1'), 'Active sprint (3)');
    expect(row.last()).toEqual({ field: 'sprint', op: 'Eq', value: { smart: 'active' } });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Neq');
    expect(row.last()).toEqual({ field: 'sprint', op: 'Neq', value: { smart: 'active' } });
    expect(screen.getByLabelText('Value for condition 1')).toHaveDisplayValue('Active sprint (3)');
  });

  it('picks smart values and names together for "is any of"', async () => {
    mockSprintValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'sprint', op: 'In', values: [] });

    await user.click(await screen.findByText('Choose values'));
    await user.click(await screen.findByRole('checkbox', { name: /^open sprints/i }));
    await user.click(screen.getByRole('checkbox', { name: /^sprint 7/i }));

    expect(row.last()).toEqual({ field: 'sprint', op: 'In', values: [{ smart: 'open' }, 'Sprint 7'] });
  });
});
