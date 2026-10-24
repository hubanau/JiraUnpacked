import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { DEFAULT_PAGE_SIZE, exportIssuesCsv, queryIssues } from '../../api/issues';
import type { IssueFilterRequest, IssueListItem, JqlRequest, PagedResult } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import type { SavedView } from '../../api/views';
import { Button, EmptyState, Spinner, Table, TBody, TD, TH, THead, TR } from '../../ui';
import { FilterBuilder, useFilterCatalog } from './FilterBuilder';
import { useListState } from './useListState';
import { isEmptyTree, pruneTree } from './filterTree';
import { describeFilter } from './describeFilter';
import { FilterSummary } from './FilterSummary';
import { JqlActions } from './JqlActions';
import { ColumnChooser } from './ColumnChooser';
import { ViewsMenu } from './ViewsMenu';
import { useSavedViews } from './useSavedViews';
import { ISSUE_COLUMNS, ALWAYS_ON, type IssueColumn } from './columns';
import { isOperatorAllowed } from './filterCatalogHelpers';
import {
  CELL_FILTER_VALUES, addCellCondition, cellCondition, cellFilterHint, type CellFilterMode, type CellValue,
} from './cellFilter';
import { CellFilterMenu } from './CellFilterMenu';
import { saveBlob } from './saveBlob';

/** The builder's wrapper: what the summary's toggle opens and closes. */
const BUILDER_ID = 'issue-filter-builder';

export default function IssueList() {
  // Route always supplies :id here (see App.tsx); the '' fallback only
  // covers the type, not a real navigation state.
  const { id = '' } = useParams();

  // The view lives in the URL, not in component state: that makes a filtered list shareable as
  // a link and survives reload and Back, neither of which useState does.
  const {
    tree, setTree, columns, setColumns, sortBy, sortDescending, setSort,
    viewId, hasListState, applyParams, setViewId,
  } = useListState();

  const [page, setPage] = useState(1);

  // Narrowing the filter can leave the user stranded on a page that no longer exists — jump
  // back to page 1 whenever the tree changes. `tree` keeps its identity across the URL's echo
  // of our own writes (see useListState), so this fires once per edit.
  useEffect(() => {
    setPage(1);
  }, [tree]);

  // What a saved view can store, as the list shows it now. Memoised so "modified" is recomputed
  // only when one of these actually changes.
  const snapshot = useMemo(
    () => ({ tree, sortBy, sortDescending, columns }),
    [tree, sortBy, sortDescending, columns],
  );
  const savedViews = useSavedViews(id, { viewId, hasListState, applyParams, setViewId }, snapshot);

  // Whether the builder is open. Decided ONCE, in the render where the list knows what it opened
  // with — the saved-views gate has settled, so a default view counts as loaded on mount: a link
  // or view that brings a filter reads as a sentence first; anything else opens the builder.
  // After that it is the user's (Collapse / Edit filter). Component state on purpose: not the URL,
  // not storage. Set during render (React re-renders at once), as useListState does with its tree.
  const [expanded, setExpanded] = useState<boolean | null>(null);
  if (expanded === null && !savedViews.holdQuery) setExpanded(isEmptyTree(tree));
  const builderOpen = expanded ?? isEmptyTree(tree);

  // A view is a new list: start it on page 1 even when only its sort differs from what was shown.
  const openView = (view: SavedView) => {
    setPage(1);
    savedViews.apply(view);
  };

  // Shared with FilterBuilder through the query cache: same key, fetched once.
  const { data: catalog, isPending: catalogPending } = useFilterCatalog();
  const knownFields = useMemo(
    () => (catalog ? new Set(catalog.fields.map((f) => f.name)) : undefined),
    [catalog],
  );

  // What the server sees: the tree without disabled parts, unfinished rows or retired fields
  // (see pruneTree). Undefined = no filter — never an empty group, which the server refuses.
  const filter = useMemo(() => pruneTree(tree, knownFields), [tree, knownFields]);
  // The sentence reads the very object the request carries: it says what is applied.
  const sentence = useMemo(() => describeFilter(filter), [filter]);
  // What Copy JQL / Open in Jira translate: the very filter and sort the list is queried with.
  const jqlRequest: JqlRequest = useMemo(
    () => (filter ? { filter, sortBy, sortDescending } : { sortBy, sortDescending }),
    [filter, sortBy, sortDescending],
  );

  const paging = { page, pageSize: DEFAULT_PAGE_SIZE, sortBy, sortDescending };
  const request: IssueFilterRequest = filter ? { filter, ...paging } : paging;
  const { data, isPending, error, isPlaceholderData } = useQuery({
    queryKey: queryKeys.issues(id, request),
    queryFn: () => queryIssues(id, request),
    // Until the catalog has settled — loaded OR failed, either way `catalogPending` goes false —
    // `knownFields` is undefined and pruneTree cannot tell a live field from a retired one, so a
    // link built on a retired field would otherwise slip into the very first request. Once the
    // catalog fails for good the fallback is the same as always: send unchecked (see pruneTree).
    // Also waits, on a bare URL only, until the saved views say whether a default view opens
    // instead — otherwise the first request would flash the unfiltered list (see useSavedViews).
    enabled: !catalogPending && !savedViews.holdQuery,
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
  // THIS filter's count: a placeholder (or the last good page after a 400) belongs to another one.
  const count = data && !isPlaceholderData ? data.totalCount : undefined;

  // Click-to-filter. One limit hint for every cell (the tree is the same for all of them); the
  // builder opens so the user sees what the click added. Page 1 follows from the tree changing.
  const cellHint = catalog ? cellFilterHint(tree, catalog) : undefined;
  const filterByCell = (field: string, value: CellValue, mode: CellFilterMode) => {
    setTree(addCellCondition(tree, cellCondition(field, value, mode)));
    setExpanded(true);
  };
  /** A filterable cell (a filter field this catalog still has) carries its ⋯ menu; any other is the plain render. */
  const renderCell = (col: IssueColumn, item: IssueListItem) => {
    const read = CELL_FILTER_VALUES[col.id];
    if (!catalog || !read || !knownFields?.has(col.id)) return col.render(item);
    const value = read(item);
    const hintFor = (mode: CellFilterMode) =>
      cellHint ?? (isOperatorAllowed(catalog, col.id, cellCondition(col.id, value, mode).op)
        ? undefined
        : `Not available for ${col.header}`);
    return (
      <span className="inline-flex items-center gap-1">
        {col.render(item)}
        <CellFilterMenu
          column={col.header}
          value={value}
          includeHint={hintFor('include')}
          excludeHint={hintFor('exclude')}
          onPick={(mode) => filterByCell(col.id, value, mode)}
        />
      </span>
    );
  };

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
        <div className="flex flex-1 flex-col gap-3">
          <FilterSummary
            sentence={sentence}
            count={count}
            expanded={builderOpen}
            controls={BUILDER_ID}
            onToggle={() => setExpanded(!builderOpen)}
          />
          {/* Collapsed means unmounted: nothing of the builder runs, not even its value lists. */}
          {builderOpen && (
            <div id={BUILDER_ID}>
              <FilterBuilder projectId={id} tree={tree} onChange={setTree} />
            </div>
          )}
        </div>
        <div className="flex flex-wrap items-center justify-end gap-2">
          <ViewsMenu
            projectId={id}
            views={savedViews.views}
            unavailable={savedViews.unavailable}
            loaded={savedViews.loaded}
            modified={savedViews.modified}
            snapshot={snapshot}
            onApply={openView}
            onLoad={setViewId}
          />
          <ColumnChooser selected={columns} onChange={setColumns} />
          <Button
            variant="secondary"
            loading={exportCsv.isPending}
            onClick={() => exportCsv.mutate()}
          >
            Export CSV
          </Button>
          <JqlActions projectId={id} request={jqlRequest} />
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
                {visibleColumns.map((col) => (col.sortable === false ? (
                  // No aria-sort at all: this header is not sortable, not merely unsorted.
                  <TH key={col.id}>{col.header}</TH>
                ) : (
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
                )))}
              </TR>
            </THead>
            <TBody>
              {shown.items.map((item) => (
                <TR key={item.jiraIssueId} className="group">
                  {visibleColumns.map((col) => (
                    <TD key={col.id}>
                      {/* Key links to the issue; a filterable column carries its ⋯ menu (renderCell);
                          everything else is the catalog's plain render. */}
                      {col.id === ALWAYS_ON ? (
                        <Link
                          to={`/projects/${id}/issues/${encodeURIComponent(item.jiraIssueId)}`}
                          className="text-[var(--ju-primary)] hover:underline"
                        >
                          {item.jiraKey}
                        </Link>
                      ) : renderCell(col, item)}
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
