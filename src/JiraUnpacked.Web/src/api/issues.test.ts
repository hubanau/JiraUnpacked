import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { getHistoryValues, getIssueChart, getIssueDetail, queryIssues } from './issues';
import { chartFixture } from '../test/chartFixture';
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
        issue: { jiraIssueId: 'a/b', jiraKey: 'ACME-1', summary: null, status: null,
          issueType: null, priority: null, assignee: null, sprint: null, team: null,
          storyPoints: null, parentJiraIssueId: null,
          jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-01-01T00:00:00Z' },
        parent: null, children: [], changeHistory: [],
      });
    }));

    // A slash, not a space: the WHATWG URL parser percent-encodes a literal
    // space in a path segment on its own (`new URL('/x/a b', base).pathname`
    // is already `/x/a%20b`), so a space-only fixture would pass here even
    // if `encodeURIComponent` were removed from issues.ts — it only proves
    // something when asserting a rendered `href` attribute, which React
    // Router does not normalise (see IssueDetail.test.tsx).
    // A slash is different: left unencoded it starts a new path segment,
    // which MSW's single-segment `:id` route will not match, so this
    // fixture actually fails if the encode is dropped.
    await getIssueDetail('p1', 'a/b');

    expect(seenUrl).toContain('a%2Fb');
  });
});

describe('getHistoryValues', () => {
  it('encodes a field name with an ampersand, a space and a hash', async () => {
    let seen: URL | undefined;
    server.use(http.get('/api/projects/p1/history-values', ({ request }) => {
      seen = new URL(request.url);
      return HttpResponse.json({ values: [], emptyCount: 0 });
    }));

    await getHistoryValues('p1', 'R&D #1 stage', 'value');

    expect(seen?.hash).toBe('');
    expect(seen?.searchParams.get('field')).toBe('R&D #1 stage');
    expect(seen?.searchParams.get('kind')).toBe('value');
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

describe('getIssueChart', () => {
  it('POSTs the filter and groupBy to the chart endpoint', async () => {
    let body: unknown = null;
    server.use(http.post('/api/projects/p1/issues/chart', async ({ request }) => {
      body = await request.json();
      return HttpResponse.json(chartFixture);
    }));

    const chart = await getIssueChart('p1', {
      filter: { logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] }, groupBy: 'team',
    });

    expect(body).toEqual({
      filter: { logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] }, groupBy: 'team',
    });
    expect(chart.totalIssues).toBe(11);
  });

  it('keys a chart by project, filter and groupBy', () => {
    expect(queryKeys.issueChart('p1', { groupBy: 'team' }))
      .not.toEqual(queryKeys.issueChart('p1', { groupBy: 'status' }));
    expect(queryKeys.issueChart('p1', { groupBy: 'team' })[2]).toBe('chart');
  });
});
