import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { DEFAULT_PAGE_SIZE, queryIssues } from '../../api/issues';
import type { FilterCondition, IssueFilterRequest, IssueListItem, PagedResult } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Button, EmptyState, Spinner, Table, TBody, TD, TH, THead, TR } from '../../ui';

const EMPTY = '—'; // '—' — never render the string "null" for a nullable field.

interface IssueListProps {
  // Exists purely so this task's tests can render a filtered-empty state
  // before EX3's FilterBuilder lands. Production never passes it — it
  // always mounts with an empty condition set.
  initialConditions?: FilterCondition[];
}

export default function IssueList({ initialConditions = [] }: IssueListProps = {}) {
  // Route always supplies :id here (see App.tsx); the '' fallback only
  // covers the type, not a real navigation state.
  const { id = '' } = useParams();

  // IssueList owns this state so EX3's FilterBuilder can drive it via
  // onChange once it mounts in the slot below.
  const [conditions, setConditions] = useState<FilterCondition[]>(initialConditions);
  // `setConditions` has no caller yet — EX3 wires `onChange={setConditions}`
  // when it mounts <FilterBuilder /> in the slot below. Referencing it here
  // keeps the state (and its setter) already owned by this component ahead
  // of that, without tripping noUnusedLocals in the meantime.
  void setConditions;

  const [page, setPage] = useState(1);

  // Narrowing the filter can leave the user stranded on a page that no
  // longer exists once results shrink — jump back to page 1 whenever the
  // condition set changes so an empty page never gets mistaken for "no
  // matches this filter".
  useEffect(() => {
    setPage(1);
  }, [conditions]);

  const request: IssueFilterRequest = { conditions, page, pageSize: DEFAULT_PAGE_SIZE };
  const { data, isPending, error } = useQuery({
    queryKey: queryKeys.issues(id, request),
    queryFn: () => queryIssues(id, request),
    // Bridges the *pending* phase of a new page/filter with the previous
    // result so an in-flight request doesn't blank the table.
    placeholderData: (prev) => prev,
  });

  // TanStack Query clears `data` once a query settles into an error state —
  // placeholderData only covers the pending phase, not a rejected request.
  // Retaining the last successful page ourselves is what keeps rows on
  // screen once a 400 comes back (see the "keeps the previous results on
  // screen" test), on top of the smoother in-flight behaviour
  // placeholderData already gives during a slow-but-valid request.
  const [lastGoodData, setLastGoodData] = useState<PagedResult<IssueListItem> | undefined>(undefined);
  useEffect(() => {
    if (data) setLastGoodData(data);
  }, [data]);

  const shown = data ?? lastGoodData;

  if (isPending && !shown) {
    return <Spinner aria-label="Loading issues" />;
  }

  const totalPages = shown ? Math.max(1, Math.ceil(shown.totalCount / shown.pageSize)) : 1;
  // Only treat a confirmed current result as "empty" — while a request is
  // erroring, `shown` may just be the last good (non-empty or empty) page,
  // and the alert above already says something went wrong with this one.
  const showEmpty = !error && shown && shown.items.length === 0;
  const showTable = shown && shown.items.length > 0;

  return (
    <div className="flex flex-col gap-4">
      {error && (
        <p role="alert" className="text-sm text-[var(--ju-danger)]">
          {error instanceof ApiError ? error.title : 'Could not load issues.'}
        </p>
      )}

      {/* EX3 mounts <FilterBuilder conditions={conditions} onChange={setConditions} /> here. */}

      {showEmpty && conditions.length === 0 && (
        <EmptyState
          title="No issues yet"
          description="Run a sync to pull this project's issues from Jira."
          action={<Link to={`/projects/${id}`}>Go to sync</Link>}
        />
      )}

      {showEmpty && conditions.length > 0 && (
        <EmptyState
          title="No issues match this filter"
          description="Try removing or loosening a condition."
        />
      )}

      {showTable && shown && (
        <>
          <Table>
            <THead>
              <TR>
                <TH>Key</TH>
                <TH>Summary</TH>
                <TH>Status</TH>
                <TH>Type</TH>
                <TH>Assignee</TH>
                <TH>Sprint</TH>
                <TH>Points</TH>
                <TH>Updated</TH>
              </TR>
            </THead>
            <TBody>
              {shown.items.map((item) => (
                <TR key={item.jiraIssueId}>
                  <TD>
                    <Link
                      to={`/projects/${id}/issues/${encodeURIComponent(item.jiraIssueId)}`}
                      className="text-[var(--ju-primary)] hover:underline"
                    >
                      {item.jiraKey}
                    </Link>
                  </TD>
                  <TD>{item.summary ?? EMPTY}</TD>
                  <TD>{item.status ?? EMPTY}</TD>
                  <TD>{item.issueType ?? EMPTY}</TD>
                  <TD>{item.assignee ?? EMPTY}</TD>
                  <TD>{item.sprint ?? EMPTY}</TD>
                  <TD>{item.storyPoints ?? EMPTY}</TD>
                  <TD>{new Date(item.jiraUpdatedAt).toLocaleDateString()}</TD>
                </TR>
              ))}
            </TBody>
          </Table>

          <div className="flex items-center justify-between">
            <p className="text-sm text-[var(--ju-muted)]">
              Page {shown.page} of {totalPages} · {shown.totalCount} issues
            </p>
            <div className="flex gap-2">
              <Button
                variant="secondary"
                disabled={shown.page <= 1}
                onClick={() => setPage((p) => p - 1)}
              >
                Previous
              </Button>
              <Button
                variant="secondary"
                disabled={shown.page >= totalPages}
                onClick={() => setPage((p) => p + 1)}
              >
                Next
              </Button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

/**
 * Route-level wrapper. React Router does not remount a component when only
 * a route param changes at the same path — a direct
 * /projects/A/issues -> /projects/B/issues transition would otherwise carry
 * IssueList's `page`, `conditions`, and `lastGoodData` state from project A
 * into project B, so a failed request for B could render its error above
 * A's stale rows (looking like real data for the wrong project). Keying by
 * `id` forces a clean remount on every project switch instead.
 */
export function IssueListRoute() {
  const { id = '' } = useParams();
  return <IssueList key={id} />;
}
