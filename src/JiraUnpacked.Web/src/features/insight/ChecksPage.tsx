import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { listChecks, runCheck } from '../../api/checks';
import type { GapCheckInfo, GapCheckResult, GapCheckViolation } from '../../api/checks';
import { getProject } from '../../api/projects';
import { queryKeys } from '../../api/queryKeys';
import {
  Badge, Button, Card, EmptyState, ErrorState, Spinner, Table, TBody, TD, TH, THead, TR,
} from '../../ui';
import { isCheckRunnable } from './format';

// '—' — never render the string "null" for a nullable field.
const EMPTY = '—';

function sectionErrorTitle(error: unknown, fallback: string): string {
  return error instanceof ApiError ? error.title : fallback;
}

function SectionHeading({ children }: { children: string }) {
  return <h2 className="mb-3 text-base font-medium text-[var(--ju-text)]">{children}</h2>;
}

interface CheckCatalogProps {
  checks: GapCheckInfo[];
  selectedId: string | null;
  onSelect: (checkId: string) => void;
}

/**
 * Every check the project exposes, rendered as a toggle group. Clicking one
 * selects it and (via the enabled useQuery below) runs it. The spec calls
 * for name *and* description visible — a `title` attribute alone only
 * surfaces on hover (and not at all on touch), so the description is
 * rendered as real text beside each button instead.
 */
function CheckCatalog({ checks, selectedId, onSelect }: CheckCatalogProps) {
  return (
    <div className="flex flex-col gap-2" role="group" aria-label="Available checks">
      {checks.map((check) => (
        <div key={check.id} className="flex flex-wrap items-baseline gap-2">
          <Button
            type="button"
            variant={check.id === selectedId ? 'primary' : 'secondary'}
            aria-pressed={check.id === selectedId}
            onClick={() => onSelect(check.id)}
          >
            {check.name}
          </Button>
          <span className="text-sm text-[var(--ju-muted)]">{check.description}</span>
        </div>
      ))}
    </div>
  );
}

/**
 * NotConfigured means the check could not run at all — no issue type on the
 * connection is classified as an Epic, so nothing was checked. This is
 * deliberately a warning, not a result: no success wording, no violation
 * count, anywhere in this component. Issue-type classes are
 * connection-scoped (Spec 2), not project-scoped, so the fix-it link goes to
 * the connection detail page rather than anything project-scoped.
 */
function NotConfiguredWarning({ connectionId }: { connectionId: string | undefined }) {
  return (
    <div className="flex flex-col gap-3 rounded-lg border border-[var(--ju-warning)]/30 bg-[var(--ju-warning)]/10 p-4">
      <Badge tone="warning">Not configured</Badge>
      <p className="text-sm font-medium text-[var(--ju-text)]">This check could not run.</p>
      <p className="text-sm text-[var(--ju-muted)]">
        No issue type on this connection is classified as an Epic, so there was nothing to check.
        Classify one to enable it.
      </p>
      {connectionId && (
        <Link
          to={`/connections/${connectionId}`}
          className="text-sm font-medium text-[var(--ju-primary)] hover:underline"
        >
          Edit issue types
        </Link>
      )}
    </div>
  );
}

interface ViolationRowsProps {
  projectId: string;
  violation: GapCheckViolation;
  expanded: boolean;
  onToggle: () => void;
}

/** One violation row, plus its (initially hidden) offending-children rows when expanded. */
function ViolationRows({ projectId, violation, expanded, onToggle }: ViolationRowsProps) {
  const hasChildren = violation.children.length > 0;

  return (
    <>
      <TR>
        <TD>
          <Link
            to={`/projects/${projectId}/issues/${encodeURIComponent(violation.jiraIssueId)}`}
            className="text-[var(--ju-primary)] hover:underline"
          >
            {violation.jiraKey}
          </Link>
        </TD>
        <TD>{violation.summary ?? EMPTY}</TD>
        <TD>{violation.status ?? EMPTY}</TD>
        <TD>{violation.class}</TD>
        <TD>{violation.issueType ?? EMPTY}</TD>
        <TD>
          {hasChildren && (
            <Button variant="secondary" onClick={onToggle} aria-expanded={expanded}>
              {expanded ? 'Hide children' : 'Show children'}
            </Button>
          )}
        </TD>
      </TR>
      {expanded && violation.children.map((child) => (
        <TR key={child.jiraIssueId}>
          <TD className="pl-8">
            <Link
              to={`/projects/${projectId}/issues/${encodeURIComponent(child.jiraIssueId)}`}
              className="text-[var(--ju-primary)] hover:underline"
            >
              {child.jiraKey}
            </Link>
          </TD>
          <TD>{child.summary ?? EMPTY}</TD>
          <TD>{child.status ?? EMPTY}</TD>
          <TD>{child.class}</TD>
          <TD>{EMPTY}</TD>
          <TD />
        </TR>
      ))}
    </>
  );
}

interface ViolationsTableProps {
  projectId: string;
  violations: GapCheckResult['violations'];
  onPrev: () => void;
  onNext: () => void;
  expandedIds: Set<string>;
  onToggleExpand: (jiraIssueId: string) => void;
}

/** State 3: Ok with violations. Paging is driven by violations.totalCount, same shape as StatusAgingSection's. */
function ViolationsTable({
  projectId, violations, onPrev, onNext, expandedIds, onToggleExpand,
}: ViolationsTableProps) {
  const totalPages = Math.max(1, Math.ceil(violations.totalCount / violations.pageSize));

  return (
    <div className="flex flex-col gap-4">
      <Table>
        <THead>
          <TR>
            <TH>Key</TH>
            <TH>Summary</TH>
            <TH>Status</TH>
            <TH>Class</TH>
            <TH>Issue type</TH>
            <TH aria-label="Actions" />
          </TR>
        </THead>
        <TBody>
          {violations.items.map((violation) => (
            <ViolationRows
              key={violation.jiraIssueId}
              projectId={projectId}
              violation={violation}
              expanded={expandedIds.has(violation.jiraIssueId)}
              onToggle={() => onToggleExpand(violation.jiraIssueId)}
            />
          ))}
        </TBody>
      </Table>

      <div className="flex items-center justify-between">
        <p className="text-sm text-[var(--ju-muted)]">
          Page {violations.page} of {totalPages} · {violations.totalCount} violations
        </p>
        <div className="flex gap-2">
          <Button variant="secondary" disabled={violations.page <= 1} onClick={onPrev}>
            Previous
          </Button>
          <Button variant="secondary" disabled={violations.page >= totalPages} onClick={onNext}>
            Next
          </Button>
        </div>
      </div>
    </div>
  );
}

interface CheckResultSectionProps {
  projectId: string;
  connectionId: string | undefined;
  query: UseQueryResult<GapCheckResult>;
  onPrev: () => void;
  onNext: () => void;
  expandedIds: Set<string>;
  onToggleExpand: (jiraIssueId: string) => void;
}

/**
 * The three mutually-exclusive result states live here, gated on
 * isCheckRunnable rather than a literal string comparison — an unrecognised
 * status falls through to the warning path (state 1) instead of being read
 * as success, same as an explicit 'NotConfigured' does.
 */
function CheckResultSection({
  projectId, connectionId, query, onPrev, onNext, expandedIds, onToggleExpand,
}: CheckResultSectionProps) {
  const { data, isPending, error } = query;
  const runnable = data ? isCheckRunnable(data.status) : false;

  return (
    <Card>
      <SectionHeading>Result</SectionHeading>
      {isPending && <Spinner aria-label="Running check" />}
      {error && <ErrorState title={sectionErrorTitle(error, 'Could not run this check.')} />}
      {data && !runnable && <NotConfiguredWarning connectionId={connectionId} />}
      {data && runnable && data.violations.totalCount === 0 && (
        <div className="flex flex-col gap-3">
          <Badge tone="success">Clean run</Badge>
          <EmptyState title="No violations found" description="This check ran and found nothing to report." />
        </div>
      )}
      {data && runnable && data.violations.totalCount > 0 && (
        <ViolationsTable
          projectId={projectId}
          violations={data.violations}
          onPrev={onPrev}
          onNext={onNext}
          expandedIds={expandedIds}
          onToggleExpand={onToggleExpand}
        />
      )}
    </Card>
  );
}

export default function ChecksPage() {
  // Route always supplies :id here (see App.tsx); the '' fallback only
  // covers the type, not a real navigation state.
  const { id = '' } = useParams();

  // The project is fetched purely for its jiraConnectionId, which the
  // NotConfigured state's fix-it link needs (issue-type classes are
  // connection-scoped, not project-scoped). Hoisted above every other hook,
  // and this component has no conditional return at all — every hook here
  // always runs, every render, in the same order.
  const projectQuery = useQuery({
    queryKey: queryKeys.project(id),
    queryFn: () => getProject(id),
  });

  const checksQuery = useQuery({
    queryKey: queryKeys.checks(id),
    queryFn: () => listChecks(id),
  });

  const [selectedCheckId, setSelectedCheckId] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());

  // Selecting a check is what runs it: the query only fires once
  // selectedCheckId is set, via `enabled`.
  const resultQuery = useQuery({
    queryKey: queryKeys.checkResult(id, selectedCheckId ?? '', page),
    queryFn: () => runCheck(id, selectedCheckId as string, page),
    enabled: selectedCheckId !== null,
  });

  function selectCheck(checkId: string) {
    setSelectedCheckId(checkId);
    setPage(1);
    setExpandedIds(new Set());
  }

  function toggleExpanded(jiraIssueId: string) {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(jiraIssueId)) next.delete(jiraIssueId);
      else next.add(jiraIssueId);
      return next;
    });
  }

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-xl font-medium tracking-tight text-[var(--ju-text)]">Gap checks</h1>

      <Card>
        <SectionHeading>Available checks</SectionHeading>
        {checksQuery.isPending && <Spinner aria-label="Loading checks" />}
        {checksQuery.error && (
          <ErrorState title={sectionErrorTitle(checksQuery.error, 'Could not load checks.')} />
        )}
        {checksQuery.data && checksQuery.data.length === 0 && (
          <EmptyState title="No checks available" description="There is nothing configured to check yet." />
        )}
        {checksQuery.data && checksQuery.data.length > 0 && (
          <CheckCatalog checks={checksQuery.data} selectedId={selectedCheckId} onSelect={selectCheck} />
        )}
      </Card>

      {selectedCheckId && (
        <CheckResultSection
          projectId={id}
          connectionId={projectQuery.data?.jiraConnectionId}
          query={resultQuery}
          onPrev={() => setPage((p) => Math.max(1, p - 1))}
          onNext={() => setPage((p) => p + 1)}
          expandedIds={expandedIds}
          onToggleExpand={toggleExpanded}
        />
      )}
    </div>
  );
}
