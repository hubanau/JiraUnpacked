import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/setup';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import type { FilterCondition } from '../../api/issues';
import { ConditionRow } from './ConditionRow';

function renderRow(initial: FilterCondition, extra: { historyAtCap?: boolean } = {}) {
  const seen: FilterCondition[] = [];
  function Harness() {
    const [condition, setCondition] = useState(initial);
    return (
      <ConditionRow
        projectId="p1"
        catalog={FILTER_CATALOG}
        condition={condition}
        label="1"
        historyAtCap={extra.historyAtCap}
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

  it('bounds the amount from 1 to 3650, as the server does', () => {
    renderRow({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } });

    const amount = screen.getByLabelText('Amount for condition 1');
    expect(amount).toHaveAttribute('min', '1');
    expect(amount).toHaveAttribute('max', '3650');
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

  it('names the browser’s zone on an unfinished period row — the zone picking one will capture', async () => {
    inZone('Europe/Warsaw');
    const user = userEvent.setup();
    const row = renderRow({ field: 'updatedAt', op: 'InPeriod' });

    expect(screen.getByText('Europe/Warsaw')).toBeInTheDocument();
    expect(screen.queryByText('UTC')).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Period for condition 1'), 'today');
    expect(row.last()).toEqual({ field: 'updatedAt', op: 'InPeriod', value: { period: 'today', timeZone: 'Europe/Warsaw' } });
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

describe('ConditionRow — history', () => {
  function mockHistory() {
    server.use(
      // The first tests start on a plain field, whose values are fetched.
      http.get('/api/projects/p1/filter-values', () => HttpResponse.json({ field: 'status', values: [], emptyCount: 0 })),
      http.get('/api/projects/p1/history-fields', () =>
        HttpResponse.json({ fields: [{ name: 'Sprint', count: 9 }, { name: 'status', count: 4 }] })),
      http.get('/api/projects/p1/history-values', ({ request }) =>
        HttpResponse.json(new URL(request.url).searchParams.get('kind') === 'author'
          ? { values: [{ name: 'Alice', count: 3 }], emptyCount: 1 }
          : { values: [{ name: 'PLAT 101', count: 5 }, { name: 'PLAT 102', count: 2 }], emptyCount: 4 })),
    );
  }

  it('lists "History of a field…" last, and starts it at "was" with nothing chosen', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({ field: 'status', op: 'Eq', value: '' });

    const fields = within(screen.getByLabelText('Field for condition 1')).getAllByRole('option');
    expect(fields.at(-1)).toHaveTextContent('History of a field…');
    await user.selectOptions(screen.getByLabelText('Field for condition 1'), 'history');

    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: '', values: [] } });
    expect(within(screen.getByLabelText('Operator for condition 1')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['was', 'changed', 'number of changes']);
  });

  it('"was": picks the field, then values — (Empty) included', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({ field: 'history', op: 'Was', value: { historyField: '', values: [] } });

    await waitFor(() => expect(screen.getByLabelText('History field for condition 1').tagName).toBe('SELECT'));
    await user.selectOptions(screen.getByLabelText('History field for condition 1'), 'Sprint');
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: [] } });

    const values = await screen.findByRole('group', { name: 'Values for condition 1' });
    await user.click(within(values).getByRole('checkbox', { name: /^PLAT 101/ }));
    await user.click(within(values).getByRole('checkbox', { name: /^\(Empty\)/ }));
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101', null] } });
  });

  it('"changed": from, to and by are optional; authors offer no (Empty); unticking drops the key', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({ field: 'history', op: 'Changed', value: { historyField: 'status' } });

    expect(await screen.findByRole('group', { name: 'From values for condition 1' })).toBeInTheDocument();
    expect(await screen.findByRole('group', { name: 'To values for condition 1' })).toBeInTheDocument();
    const by = await screen.findByRole('group', { name: 'Authors for condition 1' });
    expect(within(by).queryByRole('checkbox', { name: /^\(Empty\)/ })).toBeNull();

    await user.click(within(by).getByRole('checkbox', { name: /^Alice/ }));
    expect(row.last()).toEqual({ field: 'history', op: 'Changed', value: { historyField: 'status', by: ['Alice'] } });
    await user.click(within(by).getByRole('checkbox', { name: /^Alice/ }));
    expect(row.last()).toEqual({ field: 'history', op: 'Changed', value: { historyField: 'status' } });
  });

  it('"number of changes": starts at ≥ 2, takes a count and a window', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101'] } });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'ChangeCount');
    expect(row.last()).toEqual({ field: 'history', op: 'ChangeCount', value: { historyField: 'Sprint', compare: 'Gte', count: 2 } });
    expect(within(screen.getByLabelText('Compare for condition 1')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['=', '≠', '>', '≥', '<', '≤']);

    await user.clear(screen.getByLabelText('Count for condition 1'));
    expect(row.last()).toEqual({ field: 'history', op: 'ChangeCount', value: { historyField: 'Sprint', compare: 'Gte' } });
    await user.type(screen.getByLabelText('Count for condition 1'), '3');
    await user.selectOptions(screen.getByLabelText('During for condition 1'), 'inLast');
    expect(row.last()).toEqual({
      field: 'history', op: 'ChangeCount',
      value: { historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 30, unit: 'days' } },
    });
  });

  it('keeps the field, and the window, when switching between history operators', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({
      field: 'history', op: 'ChangeCount',
      value: { historyField: 'Sprint', compare: 'Gte', count: 3, during: { kind: 'inLast', amount: 7, unit: 'days' } },
    });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Changed');
    expect(row.last()).toEqual({
      field: 'history', op: 'Changed', value: { historyField: 'Sprint', during: { kind: 'inLast', amount: 7, unit: 'days' } },
    });
    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Was');
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: [] } });
  });

  it('choosing another history field clears the old field’s values, keeping the window', async () => {
    mockHistory();
    const user = userEvent.setup();
    const during = { kind: 'inLast', amount: 7, unit: 'days' } as const;
    const row = renderRow({
      field: 'history', op: 'Changed',
      value: { historyField: 'status', from: ['To Do'], to: ['Done'], by: ['Alice'], during },
    });

    await waitFor(() => expect(screen.getByLabelText('History field for condition 1').tagName).toBe('SELECT'));
    await user.selectOptions(screen.getByLabelText('History field for condition 1'), 'Sprint');
    // "status was Done" must not quietly become "Sprint was Done".
    expect(row.last()).toEqual({ field: 'history', op: 'Changed', value: { historyField: 'Sprint', during } });
  });

  it('choosing another history field for "was" empties its values', async () => {
    mockHistory();
    const user = userEvent.setup();
    const row = renderRow({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101'] } });

    await waitFor(() => expect(screen.getByLabelText('History field for condition 1').tagName).toBe('SELECT'));
    await user.selectOptions(screen.getByLabelText('History field for condition 1'), 'status');
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'status', values: [] } });
  });

  it('a typed history field clears the values when the input is left with another name, not on every keystroke', async () => {
    server.use(
      http.get('/api/projects/p1/history-fields', () => new HttpResponse(null, { status: 500 })),
      http.get('/api/projects/p1/history-values', () => new HttpResponse(null, { status: 500 })),
    );
    const user = userEvent.setup();
    const row = renderRow({ field: 'history', op: 'Was', value: { historyField: 'status', values: ['Blocked'] } });
    await screen.findByText(/type the name as Jira reports it/i);
    const input = screen.getByLabelText('History field for condition 1');

    // Left with the same name — even after editing it on the way — the values stay.
    await user.click(input);
    await user.type(input, '{Backspace}s');
    await user.tab();
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'status', values: ['Blocked'] } });

    // Typing another name keeps them while typing…
    await user.clear(input);
    await user.type(input, 'priority');
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'priority', values: ['Blocked'] } });
    // …and leaving the input clears them.
    await user.tab();
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'priority', values: [] } });
  });

  it('does not take the typed values input away from someone typing when the list arrives', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    server.use(
      http.get('/api/projects/p1/history-fields', () => HttpResponse.json({ fields: [{ name: 'Sprint', count: 9 }] })),
      http.get('/api/projects/p1/history-values', async () => {
        await gate;
        return HttpResponse.json({ values: [{ name: 'PLAT 101', count: 5 }], emptyCount: 0 });
      }),
    );
    const user = userEvent.setup();
    const row = renderRow({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: [] } });

    const input = screen.getByLabelText('Values for condition 1');
    await user.type(input, 'PLAT 1');
    await act(async () => {
      release();
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    // Still the same, focused input, with what was typed — the list waits.
    expect(screen.getByLabelText('Values for condition 1')).toBe(input);
    expect(input).toHaveFocus();
    expect(input).toHaveValue('PLAT 1');
    await user.type(input, '01{Enter}');
    expect(row.last()).toEqual({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: ['PLAT 101'] } });

    // Leaving it lets the list take over, keeping the chosen value.
    await user.click(document.body);
    const values = await screen.findByRole('group', { name: 'Values for condition 1' });
    expect(within(values).getByRole('checkbox', { name: /^PLAT 101/ })).toBeChecked();
  });

  it('takes at most 50 values in a history list', async () => {
    mockHistory();
    const fifty = Array.from({ length: 50 }, (_, i) => `PLAT ${200 + i}`);
    renderRow({ field: 'history', op: 'Was', value: { historyField: 'Sprint', values: fifty } });

    const values = await screen.findByRole('group', { name: 'Values for condition 1' });
    expect(within(values).getByText('You can add at most 50 values.')).toBeInTheDocument();
    expect(within(values).getByRole('checkbox', { name: /^PLAT 101/ })).toBeDisabled();
  });

  it('disables "History of a field…" at the cap, saying so', () => {
    mockHistory();
    renderRow({ field: 'status', op: 'Eq', value: '' }, { historyAtCap: true });

    const option = within(screen.getByLabelText('Field for condition 1')).getByRole('option', { name: /History of a field/ });
    expect(option).toBeDisabled();
    expect(option).toHaveTextContent('History of a field… (limit: 5)');
  });
});
