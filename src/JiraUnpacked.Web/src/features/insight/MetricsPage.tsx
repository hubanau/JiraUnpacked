import { useState, type ChangeEvent } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import {
  getStatusAging, getStoryCounts, getThroughput, getTimeInProgress,
} from '../../api/metrics';
import type {
  StatusAgingReport, StoryCountsReport, ThroughputBucketSize, ThroughputReport,
  TimeInProgressReport,
} from '../../api/metrics';
import { queryKeys } from '../../api/queryKeys';
import { Button, Card, EmptyState, ErrorState, Field, Select, Spinner, Table, TBody, TD, TH, THead, TR } from '../../ui';
import { BarRow } from './BarRow';
import { formatAverageDays, formatStatusLabel } from './format';

// '—' — never render the string "null" for a nullable field.
const EMPTY = '—';

const DATE_INPUT_CLASS =
  'rounded-md border border-[var(--ju-border)] bg-[var(--ju-bg)] px-3 py-2 text-sm text-[var(--ju-text)] ' +
  'focus:outline-none focus:ring-2 focus:ring-[var(--ju-primary)] disabled:cursor-not-allowed disabled:opacity-60';

function sectionErrorTitle(error: unknown, fallback: string): string {
  return error instanceof ApiError ? error.title : fallback;
}

function SectionHeading({ children }: { children: string }) {
  return <h2 className="mb-3 text-base font-semibold text-[var(--ju-text)]">{children}</h2>;
}

/**
 * Story counts: a BarRow per status (labelled via formatStatusLabel, so a
 * null status reads as "(no status)" rather than "null"), the same rows as
 * a table with class and story points, then the totals.
 */
function StoryCountsSection({ query }: { query: UseQueryResult<StoryCountsReport> }) {
  const { data, isPending, error } = query;
  const max = data ? Math.max(...data.byStatus.map((s) => s.count), 0) : 0;

  return (
    <Card>
      <SectionHeading>Story counts</SectionHeading>
      {isPending && <Spinner aria-label="Loading story counts" />}
      {error && <ErrorState title={sectionErrorTitle(error, 'Could not load story counts.')} />}
      {data && data.byStatus.length === 0 && (
        <EmptyState title="No stories found" description="There is nothing to count for this project yet." />
      )}
      {data && data.byStatus.length > 0 && (
        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-2">
            {data.byStatus.map((s, i) => (
              // The class is folded into the bar's own label (rather than
              // left bare, as the table's Status column already is) so a
              // status shared across classes reads unambiguously in this
              // list on its own, without duplicating the exact same text
              // node the table below also renders.
              <BarRow
                key={`bar-${i}-${s.status ?? 'none'}`}
                label={`${formatStatusLabel(s.status)} (${s.class})`}
                value={s.count}
                max={max}
                suffix={`(${s.storyPoints} pts)`}
              />
            ))}
          </div>
          <Table>
            <THead>
              <TR>
                <TH>Status</TH>
                <TH>Class</TH>
                <TH>Count</TH>
                <TH>Story points</TH>
              </TR>
            </THead>
            <TBody>
              {data.byStatus.map((s, i) => (
                <TR key={`row-${i}-${s.status ?? 'none'}`}>
                  <TD>{formatStatusLabel(s.status)}</TD>
                  <TD>{s.class}</TD>
                  <TD>{s.count}</TD>
                  <TD>{s.storyPoints}</TD>
                </TR>
              ))}
            </TBody>
          </Table>
          <p className="text-sm text-[var(--ju-muted)]">
            {data.totalIssues} issues · {data.totalStoryPoints} story points
          </p>
        </div>
      )}
    </Card>
  );
}

/**
 * Time in progress: formatAverageDays is the only thing allowed to decide
 * whether the headline reads as "Not enough data" or a real number — never
 * re-implemented here. completedIssuesConsidered/issuesExcluded are shown
 * right beside it, because an average over a handful of issues with a pile
 * excluded deserves visible doubt, not silent confidence.
 */
function TimeInProgressSection({ query }: { query: UseQueryResult<TimeInProgressReport> }) {
  const { data, isPending, error } = query;

  return (
    <Card>
      <SectionHeading>Time in progress</SectionHeading>
      {isPending && <Spinner aria-label="Loading time in progress" />}
      {error && <ErrorState title={sectionErrorTitle(error, 'Could not load time in progress.')} />}
      {data && (
        <div className="flex flex-col gap-1">
          <p className="text-2xl font-semibold text-[var(--ju-text)]">
            {formatAverageDays(data.averageDays)}
          </p>
          <p className="text-sm text-[var(--ju-muted)]">
            {data.completedIssuesConsidered} completed issues considered · {data.issuesExcluded} excluded
          </p>
        </div>
      )}
    </Card>
  );
}

interface ThroughputSectionProps {
  query: UseQueryResult<ThroughputReport>;
  bucket: ThroughputBucketSize;
  onBucketChange: (bucket: ThroughputBucketSize) => void;
  from: string;
  onFromChange: (value: string) => void;
  to: string;
  onToChange: (value: string) => void;
}

/**
 * Throughput: bucket + optional date-range controls above the bars. Every
 * bucket renders — including an all-zero period, because "nothing
 * completed" is a finding, not missing data, and collapsing it into an
 * empty state would hide that signal. Both throughput 400s (bad bucket,
 * from-after-to) arrive as ApiError.title and render verbatim via
 * sectionErrorTitle, same as any other section error.
 */
function ThroughputSection({
  query, bucket, onBucketChange, from, onFromChange, to, onToChange,
}: ThroughputSectionProps) {
  const { data, isPending, error } = query;
  const max = data ? Math.max(...data.buckets.map((b) => b.completed), 0) : 0;

  return (
    <Card>
      <SectionHeading>Throughput</SectionHeading>
      <div className="mb-4 flex flex-wrap items-end gap-3">
        <Field label="Bucket" id="throughput-bucket">
          <Select
            value={bucket}
            onChange={(e: ChangeEvent<HTMLSelectElement>) => onBucketChange(e.target.value as ThroughputBucketSize)}
          >
            <option value="day">Day</option>
            <option value="week">Week</option>
            <option value="month">Month</option>
          </Select>
        </Field>
        <Field label="From" id="throughput-from">
          <input
            type="date"
            className={DATE_INPUT_CLASS}
            value={from}
            onChange={(e: ChangeEvent<HTMLInputElement>) => onFromChange(e.target.value)}
          />
        </Field>
        <Field label="To" id="throughput-to">
          <input
            type="date"
            className={DATE_INPUT_CLASS}
            value={to}
            onChange={(e: ChangeEvent<HTMLInputElement>) => onToChange(e.target.value)}
          />
        </Field>
      </div>
      {isPending && <Spinner aria-label="Loading throughput" />}
      {error && <ErrorState title={sectionErrorTitle(error, 'Could not load throughput.')} />}
      {data && data.buckets.length === 0 && (
        <EmptyState title="No periods in this range" description="Widen the date range or pick a different bucket." />
      )}
      {data && data.buckets.length > 0 && (
        <div className="flex flex-col gap-2">
          {data.buckets.map((b, i) => (
            <BarRow key={`${b.periodStart}-${i}`} label={b.periodStart} value={b.completed} max={max} />
          ))}
        </div>
      )}
    </Card>
  );
}

interface StatusAgingSectionProps {
  query: UseQueryResult<StatusAgingReport>;
  onPrev: () => void;
  onNext: () => void;
}

/**
 * Status aging: the histogram bands as bars, then the paged item table.
 * Pagination is driven by items.totalCount, same shape as IssueList's.
 */
function StatusAgingSection({ query, onPrev, onNext }: StatusAgingSectionProps) {
  const { data, isPending, error } = query;
  const max = data ? Math.max(...data.histogram.map((b) => b.count), 0) : 0;
  const totalPages = data ? Math.max(1, Math.ceil(data.items.totalCount / data.items.pageSize)) : 1;

  return (
    <Card>
      <SectionHeading>Status aging</SectionHeading>
      {isPending && <Spinner aria-label="Loading status aging" />}
      {error && <ErrorState title={sectionErrorTitle(error, 'Could not load status aging.')} />}
      {data && (
        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-2">
            {data.histogram.map((band, i) => (
              <BarRow key={`${band.band}-${i}`} label={band.band} value={band.count} max={max} />
            ))}
          </div>

          {data.items.items.length === 0 ? (
            <EmptyState title="No aging issues" description="Nothing is currently open long enough to age." />
          ) : (
            <>
              <Table>
                <THead>
                  <TR>
                    <TH>Key</TH>
                    <TH>Summary</TH>
                    <TH>Current status</TH>
                    <TH>Class</TH>
                    <TH>Age (days)</TH>
                    <TH>Entered at</TH>
                  </TR>
                </THead>
                <TBody>
                  {data.items.items.map((item) => (
                    <TR key={item.jiraIssueId}>
                      <TD>{item.jiraKey}</TD>
                      <TD>{item.summary ?? EMPTY}</TD>
                      <TD>{item.currentStatus ?? EMPTY}</TD>
                      <TD>{item.class}</TD>
                      <TD>{item.ageDays}</TD>
                      <TD>{new Date(item.enteredAt).toLocaleDateString()}</TD>
                    </TR>
                  ))}
                </TBody>
              </Table>

              <div className="flex items-center justify-between">
                <p className="text-sm text-[var(--ju-muted)]">
                  Page {data.items.page} of {totalPages} · {data.items.totalCount} issues
                </p>
                <div className="flex gap-2">
                  <Button variant="secondary" disabled={data.items.page <= 1} onClick={onPrev}>
                    Previous
                  </Button>
                  <Button variant="secondary" disabled={data.items.page >= totalPages} onClick={onNext}>
                    Next
                  </Button>
                </div>
              </div>
            </>
          )}
        </div>
      )}
    </Card>
  );
}

export default function MetricsPage() {
  // Route always supplies :id here (see App.tsx); the '' fallback only
  // covers the type, not a real navigation state.
  const { id = '' } = useParams();

  // Four independent useQuery calls — one per section — so a slow or
  // failing metric never blanks the other three. All hooks sit here, above
  // every conditional return (which only exist inside the section
  // components below, each scoped to its own card).
  const storyCountsQuery = useQuery({
    queryKey: queryKeys.storyCounts(id),
    queryFn: () => getStoryCounts(id),
  });

  const timeInProgressQuery = useQuery({
    queryKey: queryKeys.timeInProgress(id),
    queryFn: () => getTimeInProgress(id),
  });

  const [bucket, setBucket] = useState<ThroughputBucketSize>('week');
  // Held as '' rather than undefined: queryKeys.throughput(projectId, bucket,
  // from, to) takes strings, and an undefined segment would make two
  // different unset states collapse into a cache key containing a hole.
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const throughputQuery = useQuery({
    queryKey: queryKeys.throughput(id, bucket, from, to),
    // getThroughput only receives a defined from/to when the string is
    // non-empty, so the query string stays absent rather than sending
    // ?from=&to=.
    queryFn: () => getThroughput(id, bucket, from || undefined, to || undefined),
  });

  const [agingPage, setAgingPage] = useState(1);
  const statusAgingQuery = useQuery({
    queryKey: queryKeys.statusAging(id, agingPage),
    queryFn: () => getStatusAging(id, agingPage),
  });

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-lg font-semibold text-[var(--ju-text)]">Metrics</h1>

      <StoryCountsSection query={storyCountsQuery} />
      <TimeInProgressSection query={timeInProgressQuery} />
      <ThroughputSection
        query={throughputQuery}
        bucket={bucket}
        onBucketChange={setBucket}
        from={from}
        onFromChange={setFrom}
        to={to}
        onToChange={setTo}
      />
      <StatusAgingSection
        query={statusAgingQuery}
        onPrev={() => setAgingPage((p) => Math.max(1, p - 1))}
        onNext={() => setAgingPage((p) => p + 1)}
      />
    </div>
  );
}
