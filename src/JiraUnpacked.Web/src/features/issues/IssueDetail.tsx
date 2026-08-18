import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { getIssueDetail } from '../../api/issues';
import type { IssueDetail as IssueDetailData, IssueRef, ChangeHistoryEntry } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Badge, Card, ErrorState, Spinner, Table, TBody, TD, TH, THead, TR } from '../../ui';

// '—' — never render the string "null" for a nullable field. Naive
// interpolation of `null` produces exactly that string, and on a screen
// this nullable (summary, every dictionary field, every history value) it
// would read as a real value instead of "there was nothing here".
const EMPTY = '—';

/**
 * A history from/to value is quoted ('Done') rather than shown bare — partly
 * because "changed from 'X' to 'Y'" reads naturally as a log entry, and
 * partly because a bare value can otherwise be textually identical to the
 * same field's *current* value shown as a header badge above (e.g. a
 * "status" change history entry recording "Done" when the issue's current
 * status badge also reads "Done"); quoting keeps the two visually and
 * structurally distinct.
 */
function historyValue(value: string | null): string {
  return value === null ? EMPTY : `'${value}'`;
}

interface DictionaryBadge {
  key: string;
  label: string;
  value: string;
}

/** Non-null dictionary fields (plus story points) become badges; null ones are omitted entirely — never an empty or "null" badge. */
function dictionaryBadges(issue: IssueDetailData['issue']): DictionaryBadge[] {
  const candidates: (DictionaryBadge | null)[] = [
    issue.status !== null ? { key: 'status', label: 'Status', value: issue.status } : null,
    issue.issueType !== null ? { key: 'issueType', label: 'Type', value: issue.issueType } : null,
    issue.priority !== null ? { key: 'priority', label: 'Priority', value: issue.priority } : null,
    issue.assignee !== null ? { key: 'assignee', label: 'Assignee', value: issue.assignee } : null,
    issue.sprint !== null ? { key: 'sprint', label: 'Sprint', value: issue.sprint } : null,
    issue.team !== null ? { key: 'team', label: 'Team', value: issue.team } : null,
    issue.storyPoints !== null ? { key: 'storyPoints', label: 'Points', value: String(issue.storyPoints) } : null,
  ];
  return candidates.filter((b): b is DictionaryBadge => b !== null);
}

function hierarchyLink(projectId: string, ref: IssueRef) {
  return (
    <Link
      to={`/projects/${projectId}/issues/${encodeURIComponent(ref.jiraIssueId)}`}
      className="text-[var(--ju-primary)] hover:underline"
    >
      {ref.jiraKey} — {ref.summary ?? ''}
    </Link>
  );
}

export default function IssueDetail() {
  // Route always supplies both params (see App.tsx); the '' fallback only
  // covers the type, not a real navigation state.
  const { id = '', jiraIssueId = '' } = useParams();

  const { data, isPending, error } = useQuery({
    queryKey: queryKeys.issueDetail(id, jiraIssueId),
    queryFn: () => getIssueDetail(id, jiraIssueId),
  });

  if (isPending) {
    return <Spinner aria-label="Loading issue" />;
  }

  if (error) {
    // A 404 means this issue is not in this project — that is a real
    // error, not one of the "normal empty" states below.
    return <ErrorState title={error instanceof ApiError ? error.title : 'Could not load the issue.'} />;
  }

  const issue = data?.issue;
  const parent = data?.parent ?? null;
  const children = data?.children ?? [];
  const changeHistory = data?.changeHistory ?? [];

  if (!issue) {
    return null;
  }

  const badges = dictionaryBadges(issue);
  const hasHierarchy = parent !== null || children.length > 0;

  return (
    <div className="flex flex-col gap-4">
      <Link to={`/projects/${id}/issues`} className="text-sm text-[var(--ju-primary)] hover:underline">
        ← Back to issues
      </Link>

      <Card className="flex flex-col gap-3">
        <div>
          <h1 className="text-lg font-semibold text-[var(--ju-text)]">{issue.jiraKey}</h1>
          <p className="text-sm text-[var(--ju-muted)]">{issue.summary ?? EMPTY}</p>
        </div>
        {badges.length > 0 && (
          <div className="flex flex-wrap gap-2">
            {badges.map((badge) => (
              <Badge key={badge.key} title={badge.label}>
                {badge.value}
              </Badge>
            ))}
          </div>
        )}
      </Card>

      <Card className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold text-[var(--ju-text)]">Hierarchy</h2>
        {!hasHierarchy && <p className="text-sm text-[var(--ju-muted)]">No parent or children</p>}
        {parent && (
          <p className="text-sm text-[var(--ju-text)]">Parent: {hierarchyLink(id, parent)}</p>
        )}
        {children.length > 0 && (
          <div className="text-sm text-[var(--ju-text)]">
            <p>Children:</p>
            <ul className="flex flex-col gap-1">
              {children.map((child) => (
                <li key={child.jiraIssueId}>{hierarchyLink(id, child)}</li>
              ))}
            </ul>
          </div>
        )}
      </Card>

      <Card className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold text-[var(--ju-text)]">Change history</h2>
        {changeHistory.length === 0 ? (
          // An issue that has never been edited has no history — that is
          // normal, not a failure, so this is plain text, never ErrorState.
          <p className="text-sm text-[var(--ju-muted)]">No recorded changes</p>
        ) : (
          <Table>
            <THead>
              <TR>
                <TH>Field</TH>
                <TH>From</TH>
                <TH>To</TH>
                <TH>Author</TH>
                <TH>Changed</TH>
              </TR>
            </THead>
            <TBody>
              {changeHistory.map((entry: ChangeHistoryEntry, index: number) => (
                <TR key={`${entry.field}-${entry.changedAt}-${index}`}>
                  <TD>{entry.field}</TD>
                  <TD>{historyValue(entry.fromValue)}</TD>
                  <TD>{historyValue(entry.toValue)}</TD>
                  <TD>{entry.author ?? EMPTY}</TD>
                  <TD>{new Date(entry.changedAt).toLocaleString()}</TD>
                </TR>
              ))}
            </TBody>
          </Table>
        )}
      </Card>
    </div>
  );
}
