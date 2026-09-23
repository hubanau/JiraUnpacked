import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { DEFAULT_PAGE_SIZE, exportIssuesCsv, queryIssues } from '../../api/issues';
import type { IssueFilterRequest, IssueListItem, PagedResult } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Button, EmptyState, Spinner, Table, TBody, TD, TH, THead, TR } from '../../ui';
import { FilterBuilder, useFilterCatalog } from './FilterBuilder';
import { useListState } from './useListState';
import { pruneTree } from './filterTree';
import { ColumnChooser } from './ColumnChooser';
import { ISSUE_COLUMNS, ALWAYS_ON } from './columns';
import { saveBlob } from './saveBlob';

export default function IssueList() {
  // Route always supplies :id here (see App.tsx); the '' fallback only
  // covers the type, not a real navigation state.
  const { id = '' } = useParams();

  // The view lives in the URL, not in component state: that makes a filtered list shareable as
  // a link and survives reload and Back, neither of which useState does.
  const { tree, setTree, columns, setColumns, sortBy, sortDescending, setSort } = useListState();

  const [page, setPage] = useState(1);

  // Narrowing the filter can leave the user stranded on a page that no longer exists — jump
  // back to page 1 whenever the tree changes. `tree` keeps its identity across the URL's echo
  // of our own writes (see useListState), so this fires once per edit.
  useEffect(() => {
    setPage(1);
  }, [tree]);

  // Shared with FilterBuilder through the query cache: same key, fetched once.
  const { data: catalog } = useFilterCatalog();
  const knownFields = useMemo(
    () => (catalog ? new Set(catalog.fields.map((f) => f.name)) : undefined),
    [catalog],
  );

  // What the server sees: the tree without disabled parts, unfinished rows or retired fields
  // (see pruneTree). Undefined = no filter — never an empty group, which the server refuses.
  const filter = useMemo(() => pruneTree(tree, knownFields), [tree, knownFields]);

  const paging = { page, pageSize: DEFAULT_PAGE_SIZE, sortBy, sortDescending };
  const request: IssueFilterRequest = filter ? { filter, ...paging } : paging;
  const { data, isPending, error, isPlaceholderData } = useQuery({
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

  const exportCsv = useMutation({
    mutationFn: () => exportIssuesCsv(id, { filter, sortBy, sortDescending, columns }),
    onSuccess: ({ blob, filename }) => saveBlob(blob, filename ?? 'issues.csv'),
  });

  if (isPending && !shown) {
    return <Spinner aria-label="Loading issues" />;
  }

  const totalPages = shown ? Math.max(1, Math.ceil(shown.totalCount / shown.pageSize)) : 1;
  // Only treat a confirmed *current* result as "empty" — gated on `data`
  // (not `shown`, which also covers the last-good/error fallback) and on
  // !isPlaceholderData. Across a query-key change (e.g. clearing a filter
  // that had returned 0 matches), placeholderData keeps `data` pointing at
  // the *previous* result for the whole in-flight duration; without the
  // isPlaceholderData guard that stale 0-item result would flash the wrong
  // one of the two empty states (e.g. "Run a sync" on a healthy project)
  // until the new request settles.
  const showEmpty = !error && !isPlaceholderData && data?.items.length === 0;
  const showTable = shown && shown.items.length > 0;

  // Catalog order, not URL order: the URL records WHICH columns, not how they are arranged, so
  // re-checking one must not move it to the end of the table.
  const visibleColumns = ISSUE_COLUMNS.filter((c) => columns.includes(c.id));

  return (
    <div className="flex flex-col gap-4">
      {error && (
        <p role="alert" className="text-sm text-[var(--ju-danger)]">
          {error instanceof ApiError ? error.title : 'Could not load issues.'}
        </p>
      )}

      {exportCsv.error && (
        <p role="alert" className="text-sm text-[var(--ju-danger)]">
          {exportCsv.error instanceof ApiError ? exportCsv.error.title : 'Could not export issues.'}
        </p>
      )}

      <div className="flex items-start justify-between gap-4">
        <div className="flex-1">
          <FilterBuilder projectId={id} tree={tree} onChange={setTree} />
        </div>
        <div className="flex items-center gap-2">
          <ColumnChooser selected={columns} onChange={setColumns} />
          <Button
            variant="secondary"
            loading={exportCsv.isPending}
            onClick={() => exportCsv.mutate()}
          >
            Export CSV
          </Button>
        </div>
      </div>

      {showEmpty && !filter && (
        <EmptyState
          title="No issues yet"
          description="Run a sync to pull this project's issues from Jira."
          action={<Link to={`/projects/${id}`}>Go to sync</Link>}
        />
      )}

      {showEmpty && filter && (
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
                {visibleColumns.map((col) => (
                  <TH
                    key={col.id}
                    // "none" rather than omitted: leaving it off reads as "not sortable", which
                    // is a different claim from "sortable, not currently sorted".
                    aria-sort={
                      sortBy === col.id ? (sortDescending ? 'descending' : 'ascending') : 'none'
                    }
                  >
                    <button
                      type="button"
                      aria-label={`Sort by ${col.header}`}
                      className="inline-flex items-center gap-1"
                      // A column already sorted flips; a newly chosen one starts descending,
                      // which is what people expect of Points, a date or anything ranked.
                      onClick={() => setSort(col.id, sortBy === col.id ? !sortDescending : true)}
                    >
                      {col.header}
                      {/* Decorative: aria-sort on the header already carries this. */}
                      {sortBy === col.id && (
                        <span aria-hidden="true">{sortDescending ? '↓' : '↑'}</span>
                      )}
                    </button>
                  </TH>
                ))}
              </TR>
            </THead>
            <TBody>
              {shown.items.map((item) => (
                <TR key={item.jiraIssueId}>
                  {visibleColumns.map((col) => (
                    <TD key={col.id}>
                      {/* Key is the only cell with behaviour of its own: it links to the issue.
                          Everything else is the catalog's plain render. */}
                      {col.id === ALWAYS_ON ? (
                        <Link
                          to={`/projects/${id}/issues/${encodeURIComponent(item.jiraIssueId)}`}
                          className="text-[var(--ju-primary)] hover:underline"
                        >
                          {item.jiraKey}
                        </Link>
                      ) : col.render(item)}
                    </TD>
                  ))}
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
