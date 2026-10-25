import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../../test/setup';
import { chartFixture } from '../../../test/chartFixture';
import { DEFAULT_CHART, type ChartState } from '../useListState';
import { ChartPanel } from './ChartPanel';

function renderPanel(props: Partial<React.ComponentProps<typeof ChartPanel>> = {}) {
  const handlers = { onChange: vi.fn(), onClose: vi.fn(), onPick: vi.fn() };
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <ChartPanel projectId="p1" enabled chart={DEFAULT_CHART} {...handlers} {...props} />
    </QueryClientProvider>,
  );
  return handlers;
}

const serveChart = (body: unknown = chartFixture, status = 200) => {
  const requests: unknown[] = [];
  server.use(http.post('/api/projects/p1/issues/chart', async ({ request }) => {
    requests.push(await request.json());
    return HttpResponse.json(body as object, { status });
  }));
  return requests;
};

describe('ChartPanel', () => {
  it('draws the chart of the list’s filter with the issue total', async () => {
    const requests = serveChart();
    renderPanel({ filter: { logic: 'And', conditions: [{ field: 'team', op: 'Eq', value: 'mobile' }] } });

    expect(await screen.findByRole('button', { name: 'Filter to To Do: 5' })).toBeInTheDocument();
    expect(screen.getByText('Total: 11 issues')).toBeInTheDocument();
    expect(requests).toEqual([
      { filter: { logic: 'And', conditions: [{ field: 'team', op: 'Eq', value: 'mobile' }] }, groupBy: 'status' },
    ]);
  });

  it('says how many issues a points chart counts as zero', async () => {
    serveChart();
    renderPanel({ chart: { ...DEFAULT_CHART, measure: 'points' } });

    expect(await screen.findByText('Total: 21 points · 3 issues without points')).toBeInTheDocument();
  });

  it('reports every control change as a new chart state', async () => {
    serveChart();
    const { onChange, onClose } = renderPanel();
    await screen.findByText('Total: 11 issues');

    await userEvent.selectOptions(screen.getByLabelText('Group by'), 'team');
    await userEvent.click(screen.getByRole('button', { name: 'Story points' }));
    await userEvent.click(screen.getByRole('button', { name: 'Donut chart' }));
    await userEvent.click(screen.getByRole('button', { name: 'Close chart' }));

    const next = (patch: Partial<ChartState>) => ({ ...DEFAULT_CHART, ...patch });
    expect(onChange.mock.calls).toEqual([
      [next({ groupBy: 'team' })], [next({ measure: 'points' })], [next({ type: 'donut' })],
    ]);
    expect(onClose).toHaveBeenCalled();
  });

  it('marks the current measure and chart type as pressed', async () => {
    serveChart();
    renderPanel({ chart: { groupBy: 'status', measure: 'points', type: 'table' } });
    await screen.findByText(/Total:/);

    expect(screen.getByRole('button', { name: 'Story points' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Issues' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByRole('button', { name: 'Table' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('table')).toBeInTheDocument();
  });

  it('turns a click on a group into the condition that narrows the list to it', async () => {
    serveChart();
    const { onPick } = renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: 'Filter to To Do: 5' }));
    await userEvent.click(screen.getByRole('button', { name: 'Filter to (Empty): 1' }));

    expect(onPick.mock.calls).toEqual([
      [{ field: 'status', op: 'Eq', value: 'To Do' }], [{ field: 'status', op: 'IsNull' }],
    ]);
  });

  it('shows the server’s reason when the chart cannot be drawn', async () => {
    serveChart({ title: "Unknown field 'nope'." }, 400);
    renderPanel();

    expect(await screen.findByRole('alert')).toHaveTextContent("Unknown field 'nope'.");
  });

  it('says so when nothing matches', async () => {
    serveChart({ ...chartFixture, totalIssues: 0, totalStoryPoints: 0, issuesWithoutPoints: 0, groups: [] });
    renderPanel();

    expect(await screen.findByText('No issues match this filter')).toBeInTheDocument();
  });

  it('does not ask for a chart until the list may query', async () => {
    const requests = serveChart();
    renderPanel({ enabled: false });

    expect(screen.getByLabelText('Group by')).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 50));
    expect(requests).toHaveLength(0);
  });
});
