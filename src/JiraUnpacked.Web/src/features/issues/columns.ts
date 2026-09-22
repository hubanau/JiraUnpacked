import type { ReactNode } from 'react';
import type { IssueListItem } from '../../api/issues';

const EMPTY = '—'; // never render the string "null" for a nullable field

export interface IssueColumn {
  id: string;
  header: string;
  /** The cell's text. Key is special-cased in the table because it carries the issue link. */
  render: (item: IssueListItem) => ReactNode;
}

/**
 * The one place a column's id, header and cell live together, so the chooser, the header row and
 * the body cannot disagree about what a column is.
 *
 * The ids are the SAME names the filter catalog and the sort catalog use — jiraKey, createdAt,
 * updatedAt — because all three appear side by side in the URL and two names for one field would
 * be a bug waiting to happen.
 */
export const ISSUE_COLUMNS: IssueColumn[] = [
  { id: 'jiraKey', header: 'Key', render: (i) => i.jiraKey },
  { id: 'summary', header: 'Summary', render: (i) => i.summary ?? EMPTY },
  { id: 'status', header: 'Status', render: (i) => i.status ?? EMPTY },
  { id: 'issueType', header: 'Type', render: (i) => i.issueType ?? EMPTY },
  { id: 'priority', header: 'Priority', render: (i) => i.priority ?? EMPTY },
  { id: 'assignee', header: 'Assignee', render: (i) => i.assignee ?? EMPTY },
  { id: 'sprint', header: 'Sprint', render: (i) => i.sprint ?? EMPTY },
  { id: 'team', header: 'Team', render: (i) => i.team ?? EMPTY },
  { id: 'storyPoints', header: 'Points', render: (i) => i.storyPoints ?? EMPTY },
  { id: 'parent', header: 'Parent', render: (i) => i.parentJiraIssueId ?? EMPTY },
  { id: 'createdAt', header: 'Created', render: (i) => new Date(i.jiraCreatedAt).toLocaleDateString() },
  { id: 'updatedAt', header: 'Updated', render: (i) => new Date(i.jiraUpdatedAt).toLocaleDateString() },
];

/**
 * Key cannot be switched off: it is the row's identity and its only link to the issue. A table
 * of unlabelled rows is not a smaller table, it is a broken one.
 */
export const ALWAYS_ON = 'jiraKey';
