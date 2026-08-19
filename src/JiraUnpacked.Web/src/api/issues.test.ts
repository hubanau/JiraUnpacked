import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { getIssueDetail, queryIssues } from './issues';
import { queryKeys } from './queryKeys';

const emptyPage = { items: [], page: 1, pageSize: 50, totalCount: 0 };

describe('queryIssues', () => {
  it('POSTs the request body verbatim', async () => {
    let body: unknown = null;
    server.use(http.post('/api/projects/p1/issues/query', async ({ request }) => {
      body = await request.json();
      return HttpResponse.json(emptyPage);
    }));

    await queryIssues('p1', {
      conditions: [{ field: 'status', op: 'Eq', value: 'Done' }], page: 2, pageSize: 25,
    });

    expect(body).toEqual({
      conditions: [{ field: 'status', op: 'Eq', value: 'Done' }], page: 2, pageSize: 25,
    });
  });

  it('surfaces the server 400 title as an ApiError', async () => {
    server.use(http.post('/api/projects/p1/issues/query', () =>
      HttpResponse.json({ title: "Unknown field 'nope'." }, { status: 400 })));

    await expect(queryIssues('p1', { conditions: [{ field: 'nope', op: 'Eq', value: 'x' }] }))
      .rejects.toMatchObject({ status: 400, title: "Unknown field 'nope'." });
  });
});

describe('getIssueDetail', () => {
  it('encodes the Jira issue id into the path', async () => {
    let seenUrl = '';
    server.use(http.get('/api/projects/p1/issues/:id', ({ request }) => {
      seenUrl = new URL(request.url).pathname;
      return HttpResponse.json({
        issue: { jiraIssueId: 'a b', jiraKey: 'ACME-1', summary: null, status: null,
          issueType: null, priority: null, assignee: null, sprint: null, team: null,
          storyPoints: null, parentJiraIssueId: null,
          jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-01-01T00:00:00Z' },
        parent: null, children: [], changeHistory: [],
      });
    }));

    await getIssueDetail('p1', 'a b');

    expect(seenUrl).toBe('/api/projects/p1/issues/a%20b');
  });
});

describe('issue query keys', () => {
  it('varies by request so a different filter is a different cache entry', () => {
    const a = queryKeys.issues('p1', { page: 1 });
    const b = queryKeys.issues('p1', { page: 2 });
    expect(a).not.toEqual(b);
  });

  it('nests the detail key under the project', () => {
    expect(queryKeys.issueDetail('p1', 'i1')).toEqual(['projects', 'p1', 'issues', 'i1']);
  });
});
