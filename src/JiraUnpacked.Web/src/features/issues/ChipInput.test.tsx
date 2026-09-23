import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { ChipInput, type ChipInputProps } from './ChipInput';

function renderChips(props: Partial<ChipInputProps> = {}) {
  let latest: string[] = props.values ?? [];
  function Harness() {
    const [values, setValues] = useState<string[]>(props.values ?? []);
    return (
      <ChipInput
        id="chips"
        label="Value for condition 1"
        max={100}
        {...props}
        values={values}
        onChange={(next) => { latest = next; setValues(next); }}
      />
    );
  }
  render(<Harness />);
  return { latest: () => latest, input: () => screen.getByLabelText('Value for condition 1') };
}

describe('ChipInput', () => {
  it('commits a typed value on Enter and clears the box', async () => {
    const chips = renderChips();

    await userEvent.setup().type(chips.input(), 'Done{Enter}');

    expect(chips.latest()).toEqual(['Done']);
    expect(chips.input()).toHaveValue('');
  });

  it('never splits without a separator, so "In Progress" stays one value', async () => {
    const chips = renderChips();

    await userEvent.setup().type(chips.input(), 'In Progress, Later{Enter}');

    expect(chips.latest()).toEqual(['In Progress, Later']);
  });

  it('with a separator, commits on comma or space as you type', async () => {
    const chips = renderChips({ splitOn: /[,\s]+/ });

    await userEvent.setup().type(chips.input(), 'ACME-1,ACME-2 ');

    expect(chips.latest()).toEqual(['ACME-1', 'ACME-2']);
  });

  it('splits a pasted run into normalised chips, each once', async () => {
    const user = userEvent.setup();
    const chips = renderChips({ splitOn: /[,\s]+/, normalize: (s) => s.toUpperCase() });

    await user.click(chips.input());
    await user.paste('acme-1, ACME-2\nacme-1');

    expect(chips.latest()).toEqual(['ACME-1', 'ACME-2']);
  });

  it('removes a chip', async () => {
    const chips = renderChips({ values: ['A', 'B'] });

    await userEvent.setup().click(screen.getByRole('button', { name: 'Remove A' }));

    expect(chips.latest()).toEqual(['B']);
  });

  it('stops at the limit', () => {
    renderChips({ values: ['A', 'B'], max: 2 });

    expect(screen.getByRole('button', { name: 'Add value' })).toBeDisabled();
    expect(screen.getByText(/at most 2 values/i)).toBeInTheDocument();
  });
});
