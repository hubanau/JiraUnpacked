import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { HistoryWindow } from '../../api/issues';
import { DuringControl } from './DuringControl';

function renderDuring(initial?: HistoryWindow) {
  const seen: (HistoryWindow | undefined)[] = [];
  function Harness() {
    const [value, setValue] = useState(initial);
    return <DuringControl label="1" value={value} onChange={(next) => { seen.push(next); setValue(next); }} />;
  }
  render(<Harness />);
  return { last: () => seen.at(-1) };
}

const inZone = (timeZone: string) => {
  const real = Intl.DateTimeFormat().resolvedOptions();
  vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ ...real, timeZone });
};

describe('DuringControl', () => {
  afterEach(() => vi.restoreAllMocks());

  it('offers any time, in the last, in period and between — starting at any time', () => {
    renderDuring();
    const select = screen.getByLabelText('During for condition 1');
    expect(select).toHaveValue('');
    expect(within(select).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['any time', 'in the last', 'in period', 'between']);
  });

  it('starts "in the last" at 30 days, and edits the amount and the unit', async () => {
    const user = userEvent.setup();
    const during = renderDuring();
    await user.selectOptions(screen.getByLabelText('During for condition 1'), 'inLast');
    expect(during.last()).toEqual({ kind: 'inLast', amount: 30, unit: 'days' });

    await user.selectOptions(screen.getByLabelText('During: unit for condition 1'), 'weeks');
    expect(during.last()).toEqual({ kind: 'inLast', amount: 30, unit: 'weeks' });

    await user.clear(screen.getByLabelText('During: amount for condition 1'));
    expect(during.last()).toEqual({ kind: 'inLast', amount: 0, unit: 'weeks' });   // unfinished: never sent
  });

  it('captures the browser’s zone with "in period" and names it', async () => {
    inZone('Europe/Warsaw');
    const user = userEvent.setup();
    const during = renderDuring();
    await user.selectOptions(screen.getByLabelText('During for condition 1'), 'inPeriod');
    expect(during.last()).toEqual({ kind: 'inPeriod', period: 'thisWeek', timeZone: 'Europe/Warsaw' });

    await user.selectOptions(screen.getByLabelText('During: period for condition 1'), 'lastMonth');
    expect(during.last()).toEqual({ kind: 'inPeriod', period: 'lastMonth', timeZone: 'Europe/Warsaw' });
    expect(screen.getByText('Europe/Warsaw')).toBeInTheDocument();
  });

  it('takes two dates with either end open, and goes back to any time', async () => {
    const user = userEvent.setup();
    const during = renderDuring();
    await user.selectOptions(screen.getByLabelText('During for condition 1'), 'between');
    expect(during.last()).toEqual({ kind: 'between', from: null, to: null });

    fireEvent.change(screen.getByLabelText('During: to for condition 1'), { target: { value: '2026-09-30' } });
    expect(during.last()).toEqual({ kind: 'between', from: null, to: '2026-09-30' });

    await user.selectOptions(screen.getByLabelText('During for condition 1'), '');
    expect(during.last()).toBeUndefined();
  });
});
