import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { ListValue, SmartSprintKey } from '../../api/issues';
import { MultiValuePicker, SingleValueSelect } from './ValuePicker';

const OPTIONS = [{ name: 'Backlog', count: 42 }, { name: 'In Progress', count: 5 }];

const SMART = [
  { key: 'active' as const, label: 'Active sprint', count: 1 },
  { key: 'future' as const, label: 'Future sprints', count: 0 },
  { key: 'open' as const, label: 'Open sprints (active + future)', count: 1 },
  { key: 'closed' as const, label: 'Closed sprints', count: 4 },
];

function renderMulti(initial: ListValue[] = [], max = 100, smart?: typeof SMART) {
  let latest = initial;
  function Harness() {
    const [selected, setSelected] = useState<ListValue[]>(initial);
    return (
      <MultiValuePicker
        label="Values for condition 1"
        options={OPTIONS}
        emptyCount={7}
        smart={smart}
        selected={selected}
        max={max}
        onChange={(next) => { latest = next; setSelected(next); }}
      />
    );
  }
  render(<Harness />);
  return { latest: () => latest };
}

describe('MultiValuePicker', () => {
  it('lists (Empty) first, then every value, each with its count', async () => {
    renderMulti();
    await userEvent.setup().click(screen.getByText('Choose values'));

    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent))
      .toEqual(['(Empty) 7', 'Backlog 42', 'In Progress 5']);
  });

  it('adds and removes values in the order chosen, (Empty) included', async () => {
    const user = userEvent.setup();
    const picker = renderMulti();
    await user.click(screen.getByText('Choose values'));

    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));
    await user.click(screen.getByRole('checkbox', { name: /^\(empty\)/i }));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));
    await user.click(screen.getByRole('checkbox', { name: /^in progress/i }));

    expect(picker.latest()).toEqual([null, 'In Progress']);
    expect(screen.getByRole('button', { name: 'Remove (Empty)' })).toBeInTheDocument();
  });

  it('narrows the list as you search, keeping (Empty) only when it matches', async () => {
    const user = userEvent.setup();
    renderMulti();
    await user.click(screen.getByText('Choose values'));
    const search = screen.getByRole('searchbox', { name: 'Search Values for condition 1' });

    await user.type(search, 'prog');
    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent)).toEqual(['In Progress 5']);

    await user.clear(search);
    await user.type(search, 'emp');
    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent)).toEqual(['(Empty) 7']);
  });

  it('shows each chosen value once, even if a link repeats it', () => {
    renderMulti(['Backlog', 'Backlog', null]);

    expect(screen.getAllByRole('button', { name: /^remove /i }).map((b) => b.getAttribute('aria-label')))
      .toEqual(['Remove Backlog', 'Remove (Empty)']);
  });

  it('keeps a chosen value the list no longer offers, so it can still be seen and removed', async () => {
    const picker = renderMulti(['Retired status']);

    expect(screen.getByText('Retired status')).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Remove Retired status' }));

    expect(picker.latest()).toEqual([]);
  });

  it('stops at the limit', () => {
    renderMulti(['Backlog'], 1);

    expect(screen.getByRole('checkbox', { name: /^in progress/i })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: /^backlog/i })).toBeEnabled();
    expect(screen.getByText(/at most 1 values/i)).toBeInTheDocument();
  });

  it('closes on Escape and returns focus to its summary', async () => {
    // jsdom does not apply the UA stylesheet that hides a closed <details>' content, so the
    // popover's own `open` state -- what the Escape handler actually flips -- is what is
    // asserted here, rather than the search box's visibility (which real browsers, but not
    // jsdom, would also hide).
    const user = userEvent.setup();
    renderMulti();
    const summary = screen.getByText('Choose values');
    await user.click(summary);
    const details = summary.closest('details') as HTMLDetailsElement;
    expect(details.open).toBe(true);

    await user.keyboard('{Escape}');

    expect(details.open).toBe(false);
    expect(summary).toHaveFocus();
  });

  it('shows a smart value from a link as a chip named for what it means, and removes it', async () => {
    const user = userEvent.setup();
    const picker = renderMulti([{ smart: 'open' }, 'Backlog', { smart: 'open' }]);

    expect(screen.getByText('Open sprints (active + future)')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /^Remove Open sprints/ })).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: 'Remove Open sprints (active + future)' }));

    expect(picker.latest()).toEqual(['Backlog']);
  });
});

describe('SingleValueSelect', () => {
  function renderSingle(value = '') {
    const onPick = vi.fn();
    const onPickEmpty = vi.fn();
    render(
      <label>
        Value
        <SingleValueSelect value={value} options={OPTIONS} emptyCount={7} onPick={onPick} onPickEmpty={onPickEmpty} />
      </label>,
    );
    return { onPick, onPickEmpty };
  }

  it('offers (Empty) first, then each value with its count', () => {
    renderSingle();

    expect(within(screen.getByRole('combobox')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['— choose —', '(Empty) (7)', 'Backlog (42)', 'In Progress (5)']);
  });

  it('reports a value by its name, and (Empty) separately', async () => {
    const user = userEvent.setup();
    const { onPick, onPickEmpty } = renderSingle();

    await user.selectOptions(screen.getByRole('combobox'), 'In Progress (5)');
    await user.selectOptions(screen.getByRole('combobox'), '(Empty) (7)');

    expect(onPick).toHaveBeenCalledWith('In Progress');
    expect(onPickEmpty).toHaveBeenCalledTimes(1);
  });

  it('still shows a chosen value the list does not offer', () => {
    renderSingle('Retired');

    expect(screen.getByRole('combobox')).toHaveDisplayValue('Retired (0)');
  });
});

describe('Smart section', () => {
  it('lists the smart values after (Empty) and before the names, and picks one', async () => {
    const user = userEvent.setup();
    const picker = renderMulti([], 100, SMART);
    await user.click(screen.getByText('Choose values'));

    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent)).toEqual([
      '(Empty) 7', 'Active sprint 1', 'Future sprints 0', 'Open sprints (active + future) 1', 'Closed sprints 4',
      'Backlog 42', 'In Progress 5',
    ]);
    expect(within(screen.getByRole('group', { name: 'Smart' })).getAllByRole('checkbox')).toHaveLength(4);

    await user.click(screen.getByRole('checkbox', { name: /^open sprints/i }));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));

    expect(picker.latest()).toEqual([{ smart: 'open' }, 'Backlog']);
  });

  it('searches the smart values too', async () => {
    const user = userEvent.setup();
    renderMulti([], 100, SMART);
    await user.click(screen.getByText('Choose values'));

    await user.type(screen.getByRole('searchbox'), 'clos');

    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent)).toEqual(['Closed sprints 4']);
  });

  it('skips a smart value this SPA cannot name, in both pickers', async () => {
    // A newer server may offer a key this build has no label for: skipped, never shown raw.
    const withUnknown = [...SMART, { key: 'nextQuarter' as unknown as SmartSprintKey, label: 'Next quarter', count: 6 }];

    const user = userEvent.setup();
    renderMulti([], 100, withUnknown);
    await user.click(screen.getByText('Choose values'));
    expect(within(screen.getByRole('group', { name: 'Smart' })).getAllByRole('checkbox')).toHaveLength(4);
    expect(screen.queryByText(/next quarter|nextQuarter/i)).not.toBeInTheDocument();

    render(
      <SingleValueSelect aria-label="Value" value="" options={OPTIONS} emptyCount={7} smart={withUnknown}
        onPick={vi.fn()} onPickEmpty={vi.fn()} onPickSmart={vi.fn()} />,
    );
    const single = within(screen.getByLabelText('Value')).getAllByRole('option').map((o) => o.textContent);
    expect(single).toEqual([
      '— choose —', '(Empty) (7)', 'Active sprint (1)', 'Future sprints (0)', 'Open sprints (active + future) (1)',
      'Closed sprints (4)', 'Backlog (42)', 'In Progress (5)',
    ]);
  });

  it('offers the smart values in the single select, and shows the chosen one', async () => {
    const onPickSmart = vi.fn();
    const { rerender } = render(
      <SingleValueSelect aria-label="Value" value="" options={OPTIONS} emptyCount={7} smart={SMART}
        onPick={vi.fn()} onPickEmpty={vi.fn()} onPickSmart={onPickSmart} />,
    );

    expect(within(screen.getByLabelText('Value')).getAllByRole('option').map((o) => o.textContent)).toEqual([
      '— choose —', '(Empty) (7)', 'Active sprint (1)', 'Future sprints (0)', 'Open sprints (active + future) (1)',
      'Closed sprints (4)', 'Backlog (42)', 'In Progress (5)',
    ]);

    await userEvent.setup().selectOptions(screen.getByLabelText('Value'), 'Active sprint (1)');
    expect(onPickSmart).toHaveBeenCalledWith('active');

    rerender(
      <SingleValueSelect aria-label="Value" value="" options={OPTIONS} emptyCount={7} smart={SMART} smartValue="closed"
        onPick={vi.fn()} onPickEmpty={vi.fn()} onPickSmart={onPickSmart} />,
    );
    expect(screen.getByLabelText('Value')).toHaveDisplayValue('Closed sprints (4)');
  });
});
