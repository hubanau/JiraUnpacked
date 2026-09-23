import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { MultiValuePicker, SingleValueSelect } from './ValuePicker';

const OPTIONS = [{ name: 'Backlog', count: 42 }, { name: 'In Progress', count: 5 }];

function renderMulti(initial: (string | null)[] = [], max = 100) {
  let latest = initial;
  function Harness() {
    const [selected, setSelected] = useState<(string | null)[]>(initial);
    return (
      <MultiValuePicker
        label="Values for condition 1"
        options={OPTIONS}
        emptyCount={7}
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
