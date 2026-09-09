import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { getFieldChurn } from '../../api/metrics';
import { queryKeys } from '../../api/queryKeys';
import {
  Button, Card, EmptyState, ErrorState, Field, Select, Spinner,
  Table, TBody, TD, TH, THead, TR,
} from '../../ui';
import { BarRow } from './BarRow';

const EMPTY = '—';

/** The three semantic classes. Active alone is the default: work still in flight. */
const CLASSES = ['Active', 'Other', 'Done'] as const;

const DEFAULT_FIELD = 'Sprint';

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString();
}

export default function ChurnPage() {
  const { id = '' } = useParams<{ id: string }>();

  const [field, setField] = useState(DEFAULT_FIELD);
  const [minChanges, setMinChanges] = useState(2);
  const [classes, setClasses] = useState<string[]>(['Active']);
  const [page, setPage] = useState(1);

  const query = useQuery({
    queryKey: queryKeys.fieldChurn(id, field, minChanges, classes, page),
    queryFn: () => getFieldChurn(id, { field, minChanges, statusClasses: classes, page }),
  });

  const { data, isPending, error } = query;
  const maxBand = data ? Math.max(...data.histogram.map((b) => b.count), 0) : 0;
  const totalPages = data ? Math.max(1, Math.ceil(data.items.totalCount / data.items.pageSize)) : 1;

  // Fields the project actually has history for. Before the first response there is nothing to
  // offer but the current choice — the list is data, not a hardcoded guess at Jira's schema.
  const fieldOptions = data?.availableFields.length ? data.availableFields : [field];

  function toggleClass(c: string) {
    setPage(1);
    setClasses((prev) => (prev.includes(c) ? prev.filter((x) => x !== c) : [...prev, c]));
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-xl font-semibold text-[var(--ju-text)]">Field churn</h1>
        <p className="mt-1 text-sm text-[var(--ju-muted)]">
          Issues whose {field} kept changing, worst first — the stories that keep getting pushed to
          the next sprint. Jira cannot answer this: JQL can test that a field changed, but not how
          many times.
        </p>
      </div>

      <Card className="flex flex-wrap items-end gap-4">
        <Field label="Field" id="churn-field">
          <Select
            id="churn-field"
            value={field}
            onChange={(e) => { setField(e.target.value); setPage(1); }}
          >
            {fieldOptions.map((f) => <option key={f} value={f}>{f}</option>)}
          </Select>
        </Field>

        <Field label="Minimum changes" id="churn-min">
          <input
            id="churn-min"
            type="number"
            min={1}
            value={minChanges}
            onChange={(e) => { setMinChanges(Math.max(1, Number(e.target.value) || 1)); setPage(1); }}
            className="w-28"
          />
        </Field>

        <fieldset className="flex flex-col gap-1">
          <legend className="text-sm font-medium text-[var(--ju-text)]">Current status</legend>
          <div className="flex items-center gap-4">
            {CLASSES.map((c) => (
              <label key={c} className="flex items-center gap-1.5 text-sm text-[var(--ju-text)]">
                <input
                  type="checkbox"
                  checked={classes.includes(c)}
                  onChange={() => toggleClass(c)}
                />
                {c}
              </label>
            ))}
          </div>
        </fieldset>
      </Card>

      {isPending && <Spinner aria-label="Loading field churn" />}
      {error && (
        <ErrorState
          title={error instanceof ApiError ? error.title : 'Could not load field churn.'}
        />
      )}

      {data && classes.length === 0 && (
        <EmptyState
          title="No status selected"
          description="Pick at least one status class to see which issues churned."
        />
      )}

      {data && classes.length > 0 && data.items.totalCount === 0 && (
        <EmptyState
          title={`No issue changed ${data.field} that often`}
          description={`Nothing in the selected statuses changed ${data.field} at least ${minChanges} times. Lower the threshold, or widen the status classes.`}
        />
      )}

      {data && classes.length > 0 && data.items.totalCount > 0 && (
        <>
          <Card>
            <h2 className="mb-3 text-base font-medium text-[var(--ju-text)]">
              Issues by number of changes
            </h2>
            <div className="flex flex-col gap-2">
              {data.histogram.map((b) => (
                <BarRow key={b.band} label={b.band} value={b.count} max={maxBand} />
              ))}
            </div>
          </Card>

          <Card>
            <div className="overflow-x-auto">
              <Table>
                <THead>
                  <TR>
                    <TH>Issue</TH>
                    <TH>Type</TH>
                    <TH>Status</TH>
                    <TH>Team</TH>
                    <TH>Changes</TH>
                    <TH>Distinct</TH>
                    <TH>First</TH>
                    <TH>Last</TH>
                    <TH>Summary</TH>
                  </TR>
                </THead>
                <TBody>
                  {data.items.items.map((i) => (
                    <TR key={i.jiraIssueId}>
                      <TD>
                        <Link
                          className="text-[var(--ju-primary)] underline"
                          to={`/projects/${id}/issues/${i.jiraIssueId}`}
                        >
                          {i.jiraKey}
                        </Link>
                      </TD>
                      <TD>{i.issueType ?? EMPTY}</TD>
                      <TD>{i.currentStatus ?? EMPTY}</TD>
                      <TD>{i.team ?? EMPTY}</TD>
                      <TD className="ju-numeric">{i.changes}</TD>
                      <TD className="ju-numeric">{i.distinctValues}</TD>
                      <TD>{formatDate(i.firstChangedAt)}</TD>
                      <TD>{formatDate(i.lastChangedAt)}</TD>
                      <TD>{i.summary ?? EMPTY}</TD>
                    </TR>
                  ))}
                </TBody>
              </Table>
            </div>

            <div className="mt-4 flex items-center justify-between">
              <span className="text-sm text-[var(--ju-muted)]">
                {data.items.totalCount} issue{data.items.totalCount === 1 ? '' : 's'} · page{' '}
                {data.items.page} of {totalPages}
              </span>
              <div className="flex gap-2">
                <Button
                  variant="secondary"
                  disabled={data.items.page <= 1}
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                >
                  Previous
                </Button>
                <Button
                  variant="secondary"
                  disabled={data.items.page >= totalPages}
                  onClick={() => setPage((p) => p + 1)}
                >
                  Next
                </Button>
              </div>
            </div>
          </Card>
        </>
      )}
    </div>
  );
}
