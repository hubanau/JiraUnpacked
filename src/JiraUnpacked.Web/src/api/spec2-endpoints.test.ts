import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { ApiError } from './errors';
import { createConnection, listConnections } from './connections';
import { addProject, updateSyncInterval } from './projects';
import { putFieldMappings, putIssueTypes, putStatusSemantics } from './mappings';
import { listSyncRuns, runSync } from './sync';
import { queryKeys } from './queryKeys';

describe('connections api', () => {
  it('lists connections', async () => {
    server.use(http.get('/api/connections', () =>
      HttpResponse.json([{ id: 'c1', baseUrl: 'https://x.atlassian.net', email: 'a@b.c', createdAtUtc: '2026-01-01T00:00:00Z' }])));

    await expect(listConnections()).resolves.toHaveLength(1);
  });

  it('surfaces a 400 from create as an ApiError with the server title', async () => {
    server.use(http.post('/api/connections', () =>
      HttpResponse.json({ title: 'Jira credentials could not be validated.' }, { status: 400 })));

    await expect(createConnection('https://x', 'a@b.c', 'tok'))
      .rejects.toMatchObject({ status: 400, title: 'Jira credentials could not be validated.' });
  });

  it('surfaces a 502 from create as the upstream-Jira message', async () => {
    server.use(http.post('/api/connections', () => new HttpResponse(null, { status: 502 })));

    const err = await createConnection('https://x', 'a@b.c', 'tok').catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(502);
  });
});

describe('projects api', () => {
  it('posts the project key and interval', async () => {
    let body: unknown = null;
    server.use(http.post('/api/connections/c1/projects', async ({ request }) => {
      body = await request.json();
      return HttpResponse.json({
        id: 'p1', jiraConnectionId: 'c1', jiraProjectKey: 'ACME', jiraProjectId: '10001',
        syncIntervalMinutes: 60, createdAtUtc: '2026-01-01T00:00:00Z',
      }, { status: 201 });
    }));

    await addProject('c1', 'ACME', 60);

    expect(body).toEqual({ projectKey: 'ACME', syncIntervalMinutes: 60 });
  });

  it('propagates a 409 duplicate', async () => {
    server.use(http.post('/api/connections/c1/projects', () => new HttpResponse(null, { status: 409 })));
    await expect(addProject('c1', 'ACME', 60)).rejects.toMatchObject({ status: 409 });
  });

  it('PATCHes the interval and tolerates a 204 with no body', async () => {
    server.use(http.patch('/api/projects/p1', () => new HttpResponse(null, { status: 204 })));
    await expect(updateSyncInterval('p1', 120)).resolves.toBeUndefined();
  });
});

describe('mappings api sends replace-all bodies', () => {
  it('wraps field mappings under `mappings`', async () => {
    let body: unknown = null;
    server.use(http.put('/api/projects/p1/field-mappings', async ({ request }) => {
      body = await request.json();
      return new HttpResponse(null, { status: 204 });
    }));

    await putFieldMappings('p1', [{ role: 'Status', jiraFieldId: 'status' }]);

    expect(body).toEqual({ mappings: [{ role: 'Status', jiraFieldId: 'status' }] });
  });

  it('wraps status semantics under `statuses`', async () => {
    let body: unknown = null;
    server.use(http.put('/api/projects/p1/status-semantics', async ({ request }) => {
      body = await request.json();
      return new HttpResponse(null, { status: 204 });
    }));

    await putStatusSemantics('p1', [{ jiraStatusId: '1', statusName: 'To Do', class: 'Active' }]);

    expect(body).toEqual({ statuses: [{ jiraStatusId: '1', statusName: 'To Do', class: 'Active' }] });
  });
});

// Unlike the two replace-all endpoints above, PUT .../issue-types is
// additive: SetAsync mutates only the listed rows and leaves unlisted ones
// alone. It does validate the whole batch before mutating anything, so one
// unknown id rejects the entire request with 400 {error}.
describe('mappings api: issue types (additive, not replace-all)', () => {
  it('wraps issue types under `issueTypes` and surfaces the {error} 400 shape', async () => {
    server.use(http.put('/api/connections/c1/issue-types', () =>
      HttpResponse.json({ error: 'Unknown jiraIssueTypeId: 99' }, { status: 400 })));

    await expect(putIssueTypes('c1', [{ jiraIssueTypeId: '99', class: 'Epic' }]))
      .rejects.toMatchObject({ status: 400, title: 'Unknown jiraIssueTypeId: 99' });
  });
});

describe('sync api', () => {
  it('lists runs', async () => {
    server.use(http.get('/api/projects/p1/sync-runs', () => HttpResponse.json([])));
    await expect(listSyncRuns('p1')).resolves.toEqual([]);
  });

  it('propagates a 409 already-running from a trigger', async () => {
    server.use(http.post('/api/projects/p1/sync', () => new HttpResponse(null, { status: 409 })));
    await expect(runSync('p1')).rejects.toMatchObject({ status: 409 });
  });
});

describe('queryKeys', () => {
  it('nests project-scoped keys under the project so one invalidation clears them', () => {
    expect(queryKeys.syncRuns('p1')).toEqual(['projects', 'p1', 'sync-runs']);
    expect(queryKeys.fieldMappings('p1')).toEqual(['projects', 'p1', 'field-mappings']);
    expect(queryKeys.connectionProjects('c1')).toEqual(['connections', 'c1', 'projects']);
  });
});
