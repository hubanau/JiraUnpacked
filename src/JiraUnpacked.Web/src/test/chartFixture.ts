import type { IssueChart } from '../api/issues';

/** A small status chart: ranked by issues, an (Empty) group, one group with no points. */
export const chartFixture: IssueChart = {
  groupBy: 'status',
  totalIssues: 11,
  totalStoryPoints: 21,
  issuesWithoutPoints: 3,
  groups: [
    { key: 'To Do', issues: 5, storyPoints: 8 },
    { key: 'In Progress', issues: 3, storyPoints: 13 },
    { key: 'Done', issues: 2, storyPoints: 0 },
    { key: null, issues: 1, storyPoints: 0 },
  ],
  truncated: false,
  other: null,
};
