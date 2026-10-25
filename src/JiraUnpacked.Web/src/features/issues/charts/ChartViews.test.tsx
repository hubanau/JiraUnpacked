import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { ChartGroup, IssueChart } from '../../../api/issues';
import { chartFixture } from '../../../test/chartFixture';
import { ChartBars } from './ChartBars';
import { ChartDonut } from './ChartDonut';
import { ChartTable } from './ChartTable';

const many: IssueChart = {
  ...chartFixture,
  groupBy: 'team',
  groups: Array.from({ length: 12 }, (_, i): ChartGroup => ({ key: `team${i}`, issues: 12 - i, storyPoints: i })),
};

describe('ChartBars', () => {
  it('shows the top 10 as filter buttons and folds the rest into a plain Other row', () => {
    render(<ChartBars chart={many} measure="issues" onPick={vi.fn()} />);

    expect(screen.getAllByRole('button', { name: /^Filter to/ })).toHaveLength(10);
    expect(screen.getByText('Other')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Filter to Other/ })).toBeNull();
  });

  it('filters to the clicked bar’s value', async () => {
    const onPick = vi.fn();
    render(<ChartBars chart={chartFixture} measure="issues" onPick={onPick} />);

    await userEvent.click(screen.getByRole('button', { name: 'Filter to To Do: 5' }));
    await userEvent.click(screen.getByRole('button', { name: 'Filter to (Empty): 1' }));

    expect(onPick).toHaveBeenNthCalledWith(1, 'To Do');
    expect(onPick).toHaveBeenNthCalledWith(2, null);
  });

  it('re-ranks by story points', () => {
    render(<ChartBars chart={chartFixture} measure="points" onPick={vi.fn()} />);

    expect(screen.getAllByRole('button')[0]).toHaveAccessibleName('Filter to In Progress: 13');
  });

  it('disables every bar with the limit hint', () => {
    render(<ChartBars chart={chartFixture} measure="issues" onPick={vi.fn()} pickHint="Limit: 20 conditions" />);

    for (const button of screen.getAllByRole('button')) {
      expect(button).toBeDisabled();
      expect(button).toHaveAttribute('title', 'Limit: 20 conditions');
    }
  });
});

const months: IssueChart = {
  ...chartFixture,
  groupBy: 'createdAt',
  groups: Array.from({ length: 12 }, (_, i): ChartGroup => ({
    key: `2026-${String(i + 1).padStart(2, '0')}`, issues: 10 + i, storyPoints: 0,
  })),
};

describe('ChartBars — scale', () => {
  it('draws no bar for Other, so a large Other cannot flatten the real values', () => {
    const big: IssueChart = { ...months, truncated: true, other: { groups: 50, issues: 5000, storyPoints: 0 } };
    render(<ChartBars chart={big} measure="issues" onPick={vi.fn()} />);

    expect(screen.getAllByTestId('bar')).toHaveLength(12);
    expect(screen.getByText('Earlier')).toBeInTheDocument();
    expect(screen.getByText('5,000')).toBeInTheDocument();
    expect(screen.getAllByTestId('bar').at(-1)).toHaveStyle({ width: '100%' });
  });
});

describe('ChartDonut', () => {
  it('puts the total and the measure in the centre', () => {
    render(<ChartDonut chart={chartFixture} measure="issues" onPick={vi.fn()} />);

    const centre = screen.getByTestId('donut-total');
    expect(centre).toHaveTextContent('11');
    expect(centre).toHaveTextContent('Issues');
  });

  it('lists the top 8 and Other in its legend, the slices clickable through it', async () => {
    const onPick = vi.fn();
    render(<ChartDonut chart={many} measure="issues" onPick={onPick} />);

    const legend = screen.getByRole('list', { name: 'Legend' });
    expect(within(legend).getAllByRole('listitem')).toHaveLength(9);
    expect(within(legend).getAllByRole('button')).toHaveLength(8);

    await userEvent.click(within(legend).getByRole('button', { name: /^Filter to team0/ }));
    expect(onPick).toHaveBeenCalledWith('team0');
  });

  it('gives a long timeline at most 8 coloured slices, the oldest folded into Earlier', () => {
    render(<ChartDonut chart={months} measure="issues" onPick={vi.fn()} />);

    const items = within(screen.getByRole('list', { name: 'Legend' })).getAllByRole('listitem');
    expect(items).toHaveLength(9);
    expect(items[0]).toHaveTextContent('Earlier');
    const swatches = items.map((li) => (li.querySelector('[aria-hidden="true"]') as HTMLElement).style.background);
    expect(swatches).toEqual([
      'var(--ju-chart-other)',
      ...Array.from({ length: 8 }, (_, i) => `var(--ju-chart-${i + 1})`),
    ]);
  });

  it('colours values by their position among values: (Empty) does not use up a colour', () => {
    const emptyFirst: IssueChart = { ...chartFixture, groups: [{ key: null, issues: 9, storyPoints: 0 }, ...chartFixture.groups.slice(0, 2)] };
    render(<ChartDonut chart={emptyFirst} measure="issues" onPick={vi.fn()} />);

    const swatches = within(screen.getByRole('list', { name: 'Legend' })).getAllByRole('listitem')
      .map((li) => (li.querySelector('[aria-hidden="true"]') as HTMLElement).style.background);
    expect(swatches).toEqual(['var(--ju-chart-other)', 'var(--ju-chart-1)', 'var(--ju-chart-2)']);
  });

  it('shows points in the centre for the points measure', () => {
    render(<ChartDonut chart={chartFixture} measure="points" onPick={vi.fn()} />);

    expect(screen.getByTestId('donut-total')).toHaveTextContent('21');
    expect(screen.getByTestId('donut-total')).toHaveTextContent('Points');
  });
});

describe('ChartTable', () => {
  it('lists every group with both measures and its share', () => {
    render(<ChartTable chart={chartFixture} measure="issues" onPick={vi.fn()} />);

    const rows = screen.getAllByRole('row').slice(1);
    expect(rows).toHaveLength(4);
    expect(within(rows[0]).getAllByRole('cell').map((c) => c.textContent))
      .toEqual(['To Do', '5', '8', '45%']);
    expect(within(rows[3]).getAllByRole('cell')[0]).toHaveTextContent('(Empty)');
  });

  it('sorts by a column header, flipping on a second click', async () => {
    render(<ChartTable chart={chartFixture} measure="issues" onPick={vi.fn()} />);
    const firstValue = () => within(screen.getAllByRole('row')[1]).getAllByRole('cell')[0].textContent;

    await userEvent.click(screen.getByRole('button', { name: 'Sort by Story points' }));
    expect(firstValue()).toBe('In Progress');

    await userEvent.click(screen.getByRole('button', { name: 'Sort by Story points' }));
    expect(firstValue()).not.toBe('In Progress');
  });

  it('sorts a timeline by Value in time order, not alphabetically', async () => {
    render(<ChartTable chart={months} measure="issues" onPick={vi.fn()} />);

    await userEvent.click(screen.getByRole('button', { name: 'Sort by Value' }));

    const values = screen.getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('cell')[0].textContent);
    expect(values.slice(0, 3)).toEqual(['Jan 2026', 'Feb 2026', 'Mar 2026']);
  });

  it('adds an Other row for groups the server left out', () => {
    render(<ChartTable
      chart={{ ...chartFixture, truncated: true, other: { groups: 600, issues: 40, storyPoints: 9 } }}
      measure="issues" onPick={vi.fn()}
    />);

    expect(screen.getByText('Other (600 more values)')).toBeInTheDocument();
  });

  it('filters to a row’s value', async () => {
    const onPick = vi.fn();
    render(<ChartTable chart={chartFixture} measure="issues" onPick={onPick} />);

    await userEvent.click(screen.getByRole('button', { name: 'Filter to Done' }));
    expect(onPick).toHaveBeenCalledWith('Done');
  });
});
