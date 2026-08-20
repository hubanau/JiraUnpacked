import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { getThroughput } from './metrics';
import { runCheck } from './checks';
import { blockUser, createInviteCode } from './admin';

describe('metrics api', () => {
  it('builds the throughput query string from the options given', async () => {
    let url = '';
    server.use(http.get('/api/projects/p1/metrics/throughput', ({ request }) => {
      url = new URL(request.url).search;
      return HttpResponse.json({ bucket: 'week', from: '', to: '', buckets: [] });
    }));

    await getThroughput('p1', 'month', '2026-01-01', '2026-02-01');

    expect(url).toContain('bucket=month');
    expect(url).toContain('from=2026-01-01');
    expect(url).toContain('to=2026-02-01');
  });

  it('omits the query string entirely when no options are given', async () => {
    let url = 'unset';
    server.use(http.get('/api/projects/p1/metrics/throughput', ({ request }) => {
      url = new URL(request.url).search;
      return HttpResponse.json({ bucket: 'week', from: '', to: '', buckets: [] });
    }));

    await getThroughput('p1');

    expect(url).toBe('');
  });

  it('surfaces the {error} body from an invalid bucket', async () => {
    server.use(http.get('/api/projects/p1/metrics/throughput', () =>
      HttpResponse.json({ error: 'bucket must be one of: day, week, month' }, { status: 400 })));

    await expect(getThroughput('p1')).rejects.toMatchObject({
      status: 400, title: 'bucket must be one of: day, week, month',
    });
  });
});

describe('checks api', () => {
  it('encodes the checkId into the path', async () => {
    let path = '';
    server.use(http.get('/api/projects/p1/checks/:id', ({ request }) => {
      path = new URL(request.url).pathname;
      return HttpResponse.json({
        check: { id: 'a b', name: 'X', description: 'Y' },
        status: 'Ok',
        violations: { items: [], page: 1, pageSize: 50, totalCount: 0 },
      });
    }));

    await runCheck('p1', 'a b');

    expect(path).toContain('a%20b');
  });
});

describe('admin api', () => {
  it('POSTs to create an invite code and returns the code', async () => {
    server.use(http.post('/api/admin/invite-codes', () =>
      HttpResponse.json({ code: 'ABC123', expiresAtUtc: '2026-09-01T00:00:00Z' })));

    await expect(createInviteCode()).resolves.toMatchObject({ code: 'ABC123' });
  });

  it('surfaces a 403 from an admin action rather than swallowing it', async () => {
    server.use(http.post('/api/admin/users/u1/block', () =>
      HttpResponse.json({ title: 'Forbidden' }, { status: 403 })));

    await expect(blockUser('u1')).rejects.toMatchObject({ status: 403 });
  });
});
