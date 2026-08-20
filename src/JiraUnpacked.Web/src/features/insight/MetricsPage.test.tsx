import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import MetricsPage from './MetricsPage';
import { NO_STATUS_LABEL, NOT_ENOUGH_DATA } from './format';
import type {
  StatusAgingReport, StoryCountsReport, ThroughputReport, TimeInProgressReport,
} from '../../api/metrics';

// Deliberately "not enough data" by default so the section-isolation test
// can assert time-in-progress still rendered without needing its own
// override — a null average, not a real one, is the safest default because
// it can never be confused with a genuine value another test is checking.
const DEFAULT_STORY_COUNTS: StoryCountsReport = {
  byStatus: [{ status: 'Open', class: 'ToDo', count: 5, storyPoints: 8 }],
  totalIssues: 5,
  totalStoryPoints: 8,
};

const DEFAULT_TIME_IN_PROGRESS: TimeInProgressReport = {
  averageDays: null,
  completedIssuesConsidered: 0,
  issuesExcluded: 0,
};

const DEFAULT_THROUGHPUT: ThroughputReport = {
  bucket: 'week',
  from: '',
  to: '',
  buckets: [{ periodStart: '2026-01-05', completed: 2 }],
};

const DEFAULT_STATUS_AGING: StatusAgingReport = {
  items: { items: [], page: 1, pageSize: 50, totalCount: 0 },
  histogram: [{ band: '0-7', count: 0 }],
};

interface MockMetricsOptions {
  storyCounts?: StoryCountsReport;
  storyCountsStatus?: number;
  timeInProgress?: TimeInProgressReport;
  timeInProgressStatus?: number;
  throughput?: ThroughputReport;
  throughputStatus?: number;
  throughputBody?: unknown;
  statusAging?: StatusAgingReport;
  statusAgingStatus?: number;
}

/**
 * MetricsPage always fires all four metric requests on mount, so every test
 * mocks all four endpoints — the options a test doesn't care about fall
 * back to a "boring" default rather than being left unmocked (MSW's
 * onUnhandledRequest: 'error' would fail the test if one were missed).
 */
function mockMetrics(options: MockMetricsOptions = {}) {
  server.use(
    http.get('/api/projects/:id/metrics/story-counts', () =>
      options.storyCountsStatus
        ? HttpResponse.json({ title: 'Could not load story counts.' }, { status: options.storyCountsStatus })
        : HttpResponse.json(options.storyCounts ?? DEFAULT_STORY_COUNTS)),
    http.get('/api/projects/:id/metrics/time-in-progress', () =>
      options.timeInProgressStatus
        ? HttpResponse.json({ title: 'Could not load time in progress.' }, { status: options.timeInProgressStatus })
        : HttpResponse.json(options.timeInProgress ?? DEFAULT_TIME_IN_PROGRESS)),
    http.get('/api/projects/:id/metrics/throughput', () =>
      options.throughputStatus
        ? HttpResponse.json(options.throughputBody ?? { title: 'That request was not valid.' },
          { status: options.throughputStatus })
        : HttpResponse.json(options.throughput ?? DEFAULT_THROUGHPUT)),
    http.get('/api/projects/:id/metrics/status-aging', () =>
      options.statusAgingStatus
        ? HttpResponse.json({ title: 'Could not load status aging.' }, { status: options.statusAgingStatus })
        : HttpResponse.json(options.statusAging ?? DEFAULT_STATUS_AGING)),
  );
}

function renderMetrics() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1/metrics']}>
        <Routes>
          <Route path="/projects/:id/metrics" element={<MetricsPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('MetricsPage', () => {
  it('renders "not enough data" for a null average and never a zero', async () => {
    mockMetrics({ timeInProgress: { averageDays: null, completedIssuesConsidered: 0, issuesExcluded: 4 } });
    renderMetrics();

    expect(await screen.findByText(NOT_ENOUGH_DATA)).toBeInTheDocument();
    expect(screen.queryByText('0 days')).not.toBeInTheDocument();
  });

  it('renders a genuine zero average as a number, distinguishing it from no data', async () => {
    mockMetrics({ timeInProgress: { averageDays: 0, completedIssuesConsidered: 7, issuesExcluded: 0 } });
    renderMetrics();

    expect(await screen.findByText('0 days')).toBeInTheDocument();
    expect(screen.queryByText(NOT_ENOUGH_DATA)).not.toBeInTheDocument();
  });

  it('labels a null status group explicitly and never as "null"', async () => {
    mockMetrics({ storyCounts: {
      byStatus: [{ status: null, class: 'Other', count: 3, storyPoints: 5 }],
      totalIssues: 3, totalStoryPoints: 5,
    } });
    renderMetrics();

    expect(await screen.findByText(NO_STATUS_LABEL)).toBeInTheDocument();
    expect(screen.queryByText('null')).not.toBeInTheDocument();
  });

  it('renders throughput bars even when every bucket is zero', async () => {
    mockMetrics({ throughput: { bucket: 'week', from: '', to: '', buckets: [
      { periodStart: '2026-01-05', completed: 0 }, { periodStart: '2026-01-12', completed: 0 },
    ] } });
    renderMetrics();

    expect(await screen.findByText('2026-01-05')).toBeInTheDocument();
    expect(screen.queryByText(/no data/i)).not.toBeInTheDocument();
  });

  it('shows the server message verbatim for an invalid bucket', async () => {
    mockMetrics({ throughputStatus: 400, throughputBody: { error: 'bucket must be one of: day, week, month' } });
    renderMetrics();

    expect(await screen.findByText(/bucket must be one of/i)).toBeInTheDocument();
  });

  // Section isolation: one failure must not blank the page.
  it('renders the other sections when one metric fails', async () => {
    mockMetrics({ storyCountsStatus: 500 });
    renderMetrics();

    expect(await screen.findByText(NOT_ENOUGH_DATA)).toBeInTheDocument(); // time-in-progress still rendered
    expect(screen.getByRole('alert')).toBeInTheDocument(); // story-counts error shown
  });
});
