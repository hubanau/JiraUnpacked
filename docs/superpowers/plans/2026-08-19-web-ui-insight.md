# Web UI Spec 4 (Insight) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the reason the warehouse exists — the four curated metrics, the gap-checks, and the admin panel — closing the Web UI epic.

**Architecture:** Two React-free API modules over Spec 1's `apiFetch`, a small **pure formatting module** holding the display rules that must not lie (null average, null status label, `NotConfigured`), and feature folders for metrics, checks, and admin. Charts are CSS bars with an accompanying data table — no charting library.

**Tech Stack:** React 19.2 · TypeScript strict · Vite 6 · Tailwind v4 · React Router 7 · TanStack Query 5 · Vitest + React Testing Library + MSW. **No new dependencies.**

## Global Constraints

- **NO backend change at all.** Every endpoint exists and is tested. If a screen appears to need one that does not — **an admin user list is the obvious temptation, and there is no such endpoint** — re-scope the screen and report it. Never widen the API.
- **NO new runtime dependency.** Exactly `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`. **Specifically no charting library** — Spec 1 anticipated Recharts and Spec 4 deliberately does not use it (all four metrics are bar-shaped; CSS bars plus a table are accurate, accessible and printable at no cost).
- **No new UI primitive** unless a screen genuinely cannot be built from the existing eleven in `src/ui/`.
- **Layering:** `src/ui/` imports neither `src/api/` nor `src/auth/`; `src/api/client.ts` imports neither React nor `../auth`.
- **Do not modify** `src/auth/session.ts` — `refreshOnce()`'s single-flight logic and its `hits` assertions stay untouched.
- Access token never persisted; **no token, password, invite code, or request body logged**.
- `QueryClient` keeps `retry: false` and `refetchOnWindowFocus: false`.
- **All TanStack query keys come from `src/api/queryKeys.ts`** — never inline a key array.
- **Every hook call goes above any conditional `return`**, paired with optional chaining. A hook after an early return throws "Rendered more hooks than during the previous render". This cost a fix round in Spec 2.
- **`NotConfigured` must never render as success.** Both it and a clean run return zero violations and they mean opposite things.
- **`averageDays: null` must never render as `0`.**
- **Admin route gating is UX, not security** — the API enforces the policy; a client check must never be treated as sufficient.
- Tests target logic, **no snapshot tests**. No test may pass if the behaviour it names is broken.
- The .NET suite (**278**) stays green and untouched; the FR-B2 gate is not touched.
- Every task ends with a commit. Never push, open a PR, or merge. **Do not run `dotnet test`** — the controller runs it (two agents have been killed by the harness watchdog waiting on it).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/api/metrics.ts` | Four metric wrappers + their types |
| `src/api/checks.ts` | Gap-check catalog + run wrappers + types |
| `src/api/admin.ts` | Invite code, block/unblock/delete |
| `src/api/queryKeys.ts` | *(modify)* metric, check, and catalog keys |
| `src/features/insight/format.ts` | **Pure** display rules that must not lie |
| `src/features/insight/BarRow.tsx` | One CSS bar + label (no dependency) |
| `src/features/insight/MetricsPage.tsx` | Four independently-loaded sections |
| `src/features/insight/ChecksPage.tsx` | Catalog + result, incl. the `NotConfigured` warning |
| `src/features/admin/AdminPage.tsx` | Invite codes + user actions |
| `src/features/projects/ProjectDetail.tsx` | *(modify)* Metrics + Checks links |
| `src/shell/SideNav.tsx` | *(modify)* Admin entry, admins only |
| `src/App.tsx` | *(modify)* three routes |

**Why `format.ts` is pure and separate:** the three rules most likely to be quietly broken — a null average becoming `0`, a null status becoming `"null"`, and `NotConfigured` reading as success — are all pure functions of their input. Keeping them out of components makes them exhaustively testable without rendering, and makes a regression fail one fast test rather than an obscure assertion inside a page test.

---

## Task 1 (IN1): API layer, query keys, and the pure format rules

**Files:**
- Create: `src/JiraUnpacked.Web/src/api/metrics.ts`, `checks.ts`, `admin.ts`, `src/features/insight/format.ts`
- Modify: `src/JiraUnpacked.Web/src/api/queryKeys.ts`
- Test: `src/JiraUnpacked.Web/src/features/insight/format.test.ts`, `src/JiraUnpacked.Web/src/api/insight-endpoints.test.ts`

**Interfaces:**
- Consumes: `apiFetch<T>` from `./client`, `ApiError` from `./errors`, `PagedResult<T>` from `./issues`.
- Produces: everything the later tasks import — listed below.

- [ ] **Step 1: Write `src/api/metrics.ts`**

```ts
import { apiFetch } from './client';
import type { PagedResult } from './issues';

/** `status` is null for issues with no status FK — a real group, not an error. */
export interface StatusCount {
  status: string | null;
  class: string;
  count: number;
  storyPoints: number;
}

export interface StoryCountsReport {
  byStatus: StatusCount[];
  totalIssues: number;
  totalStoryPoints: number;
}

/** `averageDays` is null (NOT 0) when no issue qualifies, so the number is never a lie. */
export interface TimeInProgressReport {
  averageDays: number | null;
  completedIssuesConsidered: number;
  issuesExcluded: number;
}

export type ThroughputBucketSize = 'day' | 'week' | 'month';

export interface ThroughputBucket { periodStart: string; completed: number }

export interface ThroughputReport {
  bucket: string;
  from: string;
  to: string;
  buckets: ThroughputBucket[];
}

export interface AgingItem {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
  currentStatus: string | null;
  class: string;
  ageDays: number;
  enteredAt: string;
}

export interface AgingBand { band: string; count: number }

export interface StatusAgingReport {
  items: PagedResult<AgingItem>;
  histogram: AgingBand[];
}

export const getStoryCounts = (projectId: string) =>
  apiFetch<StoryCountsReport>(`/api/projects/${projectId}/metrics/story-counts`);

export const getTimeInProgress = (projectId: string) =>
  apiFetch<TimeInProgressReport>(`/api/projects/${projectId}/metrics/time-in-progress`);

export const getThroughput = (
  projectId: string,
  bucket?: ThroughputBucketSize,
  from?: string,
  to?: string,
) => {
  const qs = new URLSearchParams();
  if (bucket) qs.set('bucket', bucket);
  if (from) qs.set('from', from);
  if (to) qs.set('to', to);
  const suffix = qs.toString() ? `?${qs}` : '';
  return apiFetch<ThroughputReport>(`/api/projects/${projectId}/metrics/throughput${suffix}`);
};

export const getStatusAging = (projectId: string, page = 1, pageSize = 50) =>
  apiFetch<StatusAgingReport>(
    `/api/projects/${projectId}/metrics/status-aging?page=${page}&pageSize=${pageSize}`);
```

- [ ] **Step 2: Write `src/api/checks.ts`**

```ts
import { apiFetch } from './client';
import type { PagedResult } from './issues';

export interface GapCheckInfo { id: string; name: string; description: string }

export interface GapCheckChild {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
  status: string | null;
  class: string;
}

export interface GapCheckViolation {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
  status: string | null;
  class: string;
  issueType: string | null;
  children: GapCheckChild[];
}

/**
 * `status` is 'Ok' | 'NotConfigured'. BOTH can carry zero violations and they
 * mean opposite things: Ok+zero is good news, NotConfigured+zero means the
 * check could not run at all. Never render them the same way.
 */
export interface GapCheckResult {
  check: GapCheckInfo;
  status: string;
  violations: PagedResult<GapCheckViolation>;
}

export const listChecks = (projectId: string) =>
  apiFetch<GapCheckInfo[]>(`/api/projects/${projectId}/checks`);

export const runCheck = (projectId: string, checkId: string, page = 1, pageSize = 50) =>
  apiFetch<GapCheckResult>(
    `/api/projects/${projectId}/checks/${encodeURIComponent(checkId)}?page=${page}&pageSize=${pageSize}`);
```

- [ ] **Step 3: Write `src/api/admin.ts`**

```ts
import { apiFetch } from './client';

export interface InviteCodeResponse { code: string; expiresAtUtc: string }

export const createInviteCode = () =>
  apiFetch<InviteCodeResponse>('/api/admin/invite-codes', { method: 'POST' });

export const blockUser = (userId: string) =>
  apiFetch<void>(`/api/admin/users/${userId}/block`, { method: 'POST' });

export const unblockUser = (userId: string) =>
  apiFetch<void>(`/api/admin/users/${userId}/unblock`, { method: 'POST' });

/** Irreversible: cascades across every tenant table. Gate behind a typed confirmation. */
export const deleteUser = (userId: string) =>
  apiFetch<void>(`/api/admin/users/${userId}`, { method: 'DELETE' });
```

- [ ] **Step 4: Add the query keys to `src/api/queryKeys.ts`**

Add these to the existing object, leaving every existing entry byte-for-byte unchanged:

```ts
  storyCounts: (projectId: string) => ['projects', projectId, 'metrics', 'story-counts'] as const,
  timeInProgress: (projectId: string) => ['projects', projectId, 'metrics', 'time-in-progress'] as const,
  throughput: (projectId: string, bucket: string, from: string, to: string) =>
    ['projects', projectId, 'metrics', 'throughput', bucket, from, to] as const,
  statusAging: (projectId: string, page: number) =>
    ['projects', projectId, 'metrics', 'status-aging', page] as const,
  checks: (projectId: string) => ['projects', projectId, 'checks'] as const,
  checkResult: (projectId: string, checkId: string, page: number) =>
    ['projects', projectId, 'checks', checkId, page] as const,
```

- [ ] **Step 5: Write the failing format test**

`src/features/insight/format.test.ts`:
```ts
import { describe, expect, it } from 'vitest';
import {
  NO_STATUS_LABEL, NOT_ENOUGH_DATA, formatAverageDays, formatStatusLabel,
  isCheckRunnable, barPercent,
} from './format';

describe('formatAverageDays — a null average must never read as zero', () => {
  it('renders null as "not enough data", not 0', () => {
    expect(formatAverageDays(null)).toBe(NOT_ENOUGH_DATA);
    expect(formatAverageDays(null)).not.toContain('0');
  });

  // The distinguishing case: a genuine zero is data, and must still be a number.
  it('renders a real zero as a number', () => {
    expect(formatAverageDays(0)).toBe('0 days');
  });

  it('rounds to one decimal', () => {
    expect(formatAverageDays(3.14159)).toBe('3.1 days');
    expect(formatAverageDays(1)).toBe('1 day');
  });
});

describe('formatStatusLabel — a null status is a real group', () => {
  it('renders null as an explicit label, never "null" or blank', () => {
    expect(formatStatusLabel(null)).toBe(NO_STATUS_LABEL);
    expect(formatStatusLabel(null)).not.toBe('null');
    expect(formatStatusLabel(null).trim()).not.toBe('');
  });

  it('passes a real status through unchanged', () => {
    expect(formatStatusLabel('In Progress')).toBe('In Progress');
  });
});

describe('isCheckRunnable — NotConfigured is not success', () => {
  it('treats Ok as runnable', () => {
    expect(isCheckRunnable('Ok')).toBe(true);
  });

  it('treats NotConfigured as NOT runnable, so zero violations cannot read as clean', () => {
    expect(isCheckRunnable('NotConfigured')).toBe(false);
  });

  it('treats an unknown status as not runnable rather than assuming success', () => {
    expect(isCheckRunnable('Whatever')).toBe(false);
  });
});

describe('barPercent', () => {
  it('scales against the maximum', () => {
    expect(barPercent(5, 10)).toBe(50);
    expect(barPercent(10, 10)).toBe(100);
  });

  it('returns 0 when the maximum is 0 rather than dividing by zero', () => {
    expect(barPercent(0, 0)).toBe(0);
  });
});
```

- [ ] **Step 6: Run to verify it fails**

Run from `src/JiraUnpacked.Web/`: `npm run test -- format`
Expected: FAIL — cannot resolve `./format`.

- [ ] **Step 7: Implement `src/features/insight/format.ts`**

```ts
/**
 * The display rules that must not lie. Pure on purpose: each of these is a
 * rule the API went out of its way to make unambiguous, and the UI's job is
 * not to undo that.
 */

/** The API returns null (not 0) when no issue qualifies. Zero would claim instant cycle time. */
export const NOT_ENOUGH_DATA = 'Not enough data';

/** A null status is a real group of issues with no status FK — not a status named "null". */
export const NO_STATUS_LABEL = '(no status)';

export function formatAverageDays(averageDays: number | null): string {
  if (averageDays === null) return NOT_ENOUGH_DATA;
  const rounded = Math.round(averageDays * 10) / 10;
  return `${rounded} ${rounded === 1 ? 'day' : 'days'}`;
}

export function formatStatusLabel(status: string | null): string {
  return status ?? NO_STATUS_LABEL;
}

/**
 * Only 'Ok' means the check actually ran. 'NotConfigured' — and anything
 * unrecognised — must fall through to the warning path, because a check that
 * did not run returns zero violations exactly like a clean one.
 */
export function isCheckRunnable(status: string): boolean {
  return status === 'Ok';
}

/** Bar width as a percentage of the largest value in the series. */
export function barPercent(value: number, max: number): number {
  if (max <= 0) return 0;
  return Math.round((value / max) * 100);
}
```

- [ ] **Step 8: Run to verify it passes**

Run: `npm run test -- format`
Expected: PASS (11 tests).

- [ ] **Step 9: Write the endpoint test**

`src/api/insight-endpoints.test.ts` — assert the request shapes, especially the throughput query string and both `{error}` validation bodies:
```ts
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
```

- [ ] **Step 10: Run the full suite and commit**

Run: `npm run test && npm run typecheck && npm run lint` — all green; report the total (147 + your new tests).

```bash
git add src/JiraUnpacked.Web/src/api src/JiraUnpacked.Web/src/features/insight
git commit -m "feat(web): insight API wrappers, query keys, and the pure display rules"
```

---

## Task 2 (IN2): The metrics page

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/insight/BarRow.tsx`, `MetricsPage.tsx`
- Modify: `src/JiraUnpacked.Web/src/App.tsx` (add `/projects/:id/metrics`)
- Test: `src/JiraUnpacked.Web/src/features/insight/MetricsPage.test.tsx`

**Interfaces:**
- Consumes (IN1): all four metric wrappers and their types, `formatAverageDays`, `formatStatusLabel`, `barPercent`, `NOT_ENOUGH_DATA`, `NO_STATUS_LABEL`, and the four metric query keys.
- Produces: route `/projects/:id/metrics`.

- [ ] **Step 1: Implement `BarRow.tsx`**

No charting library — a CSS bar plus its label and value:

```tsx
export function BarRow({ label, value, max, suffix }: {
  label: string; value: number; max: number; suffix?: string;
}) {
  const pct = barPercent(value, max);
  return (
    <div className="flex items-center gap-3">
      <span className="w-40 shrink-0 truncate text-sm">{label}</span>
      {/* The bar conveys shape; the number beside it is the actual value, so
          nothing depends on reading a pixel width. */}
      <div className="h-4 flex-1 rounded bg-[var(--ju-surface)]" aria-hidden="true">
        <div className="h-4 rounded bg-[var(--ju-primary)]" style={{ width: `${pct}%` }} />
      </div>
      <span className="w-24 shrink-0 text-right text-sm tabular-nums">
        {value}{suffix ? ` ${suffix}` : ''}
      </span>
    </div>
  );
}
```

The bar track is `aria-hidden` because the number carries the information; a screen reader hears the label and the value, not a decorative div.

- [ ] **Step 2: Implement `MetricsPage.tsx` — four independent sections**

Each metric gets **its own `useQuery`**, so a slow or failing one does not blank the others. All hooks go above every conditional return.

- **Story counts:** `max = Math.max(...byStatus.map(s => s.count), 0)`; a `BarRow` per status using `formatStatusLabel(s.status)`; below the bars a `Table` of status / class / count / story points; then `totalIssues` and `totalStoryPoints`.
- **Time in progress:** `formatAverageDays(data.averageDays)` as the headline, with `completedIssuesConsidered` and `issuesExcluded` beneath — an average over 12 issues with 300 excluded deserves visible doubt.
- **Throughput:** a bucket `Select` (day/week/month, default `week`) and two optional date inputs. Hold `from`/`to` as `''` when unset, not `undefined` — `queryKeys.throughput(projectId, bucket, from, to)` takes strings, and an `undefined` segment would make two different cache entries collapse into a key containing a hole. Pass `undefined` to `getThroughput` only when the string is empty, so the query string stays absent; a `BarRow` per bucket labelled by `periodStart`. Render bars even when every `completed` is 0 — "nothing was completed" is a finding, not missing data. Both 400s render `error.title` verbatim (Spec 1's `toApiError` lifts the `{error}` body into `title`).
- **Status aging:** the `histogram` bands as `BarRow`s, then the paged `items` table (key, summary, current status, class, age days, entered at) with Previous/Next driven by `items.totalCount`.

Nullable fields render `'—'`, never the string `"null"`.

- [ ] **Step 3: Write the failing tests**

```tsx
it('renders "not enough data" for a null average and never a zero', async () => {
  mockMetrics({ timeInProgress: { averageDays: null, completedIssuesConsidered: 0, issuesExcluded: 4 } });
  renderMetrics();

  expect(await screen.findByText(NOT_ENOUGH_DATA)).toBeInTheDocument();
  expect(screen.queryByText('0 days')).not.toBeInTheDocument();
});

it('renders a genuine zero average as a number, distinguishing it from no data', async () => {
  mockMetrics({ timeInProgress: { averageDays: 0, completedIssuesConsidered: 7, issuesExcluded: 0 } });
  renderMetrics();

  expect(await screen.findByText('0 days')).toBeInTheDocument();
  expect(screen.queryByText(NOT_ENOUGH_DATA)).not.toBeInTheDocument();
});

it('labels a null status group explicitly and never as "null"', async () => {
  mockMetrics({ storyCounts: {
    byStatus: [{ status: null, class: 'Other', count: 3, storyPoints: 5 }],
    totalIssues: 3, totalStoryPoints: 5,
  }});
  renderMetrics();

  expect(await screen.findByText(NO_STATUS_LABEL)).toBeInTheDocument();
  expect(screen.queryByText('null')).not.toBeInTheDocument();
});

it('renders throughput bars even when every bucket is zero', async () => {
  mockMetrics({ throughput: { bucket: 'week', from: '', to: '', buckets: [
    { periodStart: '2026-01-05', completed: 0 }, { periodStart: '2026-01-12', completed: 0 },
  ]}});
  renderMetrics();

  expect(await screen.findByText('2026-01-05')).toBeInTheDocument();
  expect(screen.queryByText(/no data/i)).not.toBeInTheDocument();
});

it('shows the server message verbatim for an invalid bucket', async () => {
  mockMetrics({ throughputStatus: 400, throughputBody: { error: 'bucket must be one of: day, week, month' } });
  renderMetrics();

  expect(await screen.findByText(/bucket must be one of/i)).toBeInTheDocument();
});

// Section isolation: one failure must not blank the page.
it('renders the other sections when one metric fails', async () => {
  mockMetrics({ storyCountsStatus: 500 });
  renderMetrics();

  expect(await screen.findByText(NOT_ENOUGH_DATA)).toBeInTheDocument();  // time-in-progress still rendered
  expect(screen.getByRole('alert')).toBeInTheDocument();                  // story-counts error shown
});
```

- [ ] **Step 4: Wire the route in `App.tsx`**

Inside the guarded shell: `<Route path="/projects/:id/metrics" element={<MetricsPage />} />`. Leave the `QueryClient` options untouched.

- [ ] **Step 5: Run and commit**

Run: `npm run test && npm run typecheck && npm run lint` — all green; report the count.

```bash
git add src/JiraUnpacked.Web/src/features/insight src/JiraUnpacked.Web/src/App.tsx
git commit -m "feat(web): metrics page with CSS bars and honest empty values"
```

---

## Task 3 (IN3): Gap-checks

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/insight/ChecksPage.tsx`
- Modify: `src/JiraUnpacked.Web/src/App.tsx` (add `/projects/:id/checks`)
- Test: `src/JiraUnpacked.Web/src/features/insight/ChecksPage.test.tsx`

**Interfaces:**
- Consumes (IN1): `listChecks`, `runCheck`, `GapCheckInfo`, `GapCheckResult`, `GapCheckViolation`, `isCheckRunnable`, `queryKeys.checks`, `queryKeys.checkResult`. Consumes (Spec 2): the connection's issue-type editor route, for the `NotConfigured` link.
- Produces: route `/projects/:id/checks`.

- [ ] **Step 1: Implement `ChecksPage.tsx`**

Catalog from `listChecks`; selecting one runs `runCheck` and renders the result. Three mutually exclusive result states:

1. **`NotConfigured`** (`!isCheckRunnable(result.status)`) — a **warning**, not a result:
   > **This check could not run.** No issue type on this connection is classified as an Epic, so there was nothing to check. Classify one to enable it. *(link to the connection's issue types)*

   It must render **no** success wording and **no** violation count. A user who reads "0 violations" and concludes their epics are healthy — when nothing was checked — is the worst outcome this screen can produce.

2. **`Ok` with zero violations** — an explicit success: **"No violations found"**, visibly distinct from state 1.

3. **`Ok` with violations** — a `Table` of violations (key, summary, status, class, issue type), each row expandable to its `children` (key, summary, status, class). Each key links to Spec 3's issue detail at `/projects/${id}/issues/${encodeURIComponent(jiraIssueId)}`. Paging driven by `violations.totalCount`.

The project's `jiraConnectionId` is needed for the `NotConfigured` link; fetch the project (`queryKeys.project(id)`, `getProject`) as Spec 2's screens do, and hoist that hook above the conditional returns like the rest.

- [ ] **Step 2: Write the failing tests — the `NotConfigured` pair is the centrepiece**

```tsx
const okEmpty = (over = {}) => ({
  check: { id: 'epic-gaps', name: 'Epic gaps', description: 'd' },
  status: 'Ok',
  violations: { items: [], page: 1, pageSize: 50, totalCount: 0 },
  ...over,
});

it('renders a clean run as an explicit success', async () => {
  mockCheck(okEmpty());
  await selectFirstCheck();

  expect(await screen.findByText(/no violations found/i)).toBeInTheDocument();
});

// The centrepiece: identical zero-violation payload, opposite meaning.
it('renders NotConfigured as a warning that shares no copy with the clean-run success', async () => {
  mockCheck(okEmpty({ status: 'NotConfigured' }));
  await selectFirstCheck();

  expect(await screen.findByText(/could not run/i)).toBeInTheDocument();
  expect(screen.getByRole('link', { name: /issue type/i })).toBeInTheDocument();

  // Must NOT read as a clean result.
  expect(screen.queryByText(/no violations found/i)).not.toBeInTheDocument();
  expect(screen.queryByText(/0 violations/i)).not.toBeInTheDocument();
});

it('lists violations and links each to its issue detail', async () => {
  mockCheck(okEmpty({ violations: { items: [{
    jiraIssueId: 'i1', jiraKey: 'ACME-1', summary: 'Epic', status: 'Done',
    class: 'Done', issueType: 'Epic', children: [],
  }], page: 1, pageSize: 50, totalCount: 1 }}));
  await selectFirstCheck();

  expect(await screen.findByRole('link', { name: /ACME-1/ }))
    .toHaveAttribute('href', '/projects/p1/issues/i1');
});

it('expands a violation to show its offending children', async () => {
  mockCheck(okEmpty({ violations: { items: [{
    jiraIssueId: 'i1', jiraKey: 'ACME-1', summary: 'Epic', status: 'Done',
    class: 'Done', issueType: 'Epic',
    children: [
      { jiraIssueId: 'c1', jiraKey: 'ACME-2', summary: 'Open child', status: 'To Do', class: 'Active' },
      { jiraIssueId: 'c2', jiraKey: 'ACME-3', summary: 'Another', status: 'To Do', class: 'Active' },
    ],
  }], page: 1, pageSize: 50, totalCount: 1 }}));
  await selectFirstCheck();
  await screen.findByText('ACME-1');

  // Children are the evidence for the violation, so they must be reachable.
  expect(screen.queryByText('ACME-2')).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: /show children|expand/i }));

  expect(await screen.findByText('ACME-2')).toBeInTheDocument();
  expect(screen.getByText('ACME-3')).toBeInTheDocument();
});
```

- [ ] **Step 3: Wire the route, run, and commit**

`<Route path="/projects/:id/checks" element={<ChecksPage />} />` inside the guarded shell.

Run: `npm run test && npm run typecheck && npm run lint` — all green; report the count.

```bash
git add src/JiraUnpacked.Web/src/features/insight src/JiraUnpacked.Web/src/App.tsx
git commit -m "feat(web): gap-checks with an unmistakable not-configured state"
```

---

## Task 4 (IN4): Admin panel

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/admin/AdminPage.tsx`
- Modify: `src/JiraUnpacked.Web/src/App.tsx` (add `/admin`)
- Test: `src/JiraUnpacked.Web/src/features/admin/AdminPage.test.tsx`

**Interfaces:**
- Consumes (IN1): `createInviteCode`, `blockUser`, `unblockUser`, `deleteUser`, `InviteCodeResponse`. Consumes (Spec 1): `useAuth()` for `role`, and the `Modal` typed-confirmation pattern from `UserMenu`.
- Produces: route `/admin`.

- [ ] **Step 1: Implement `AdminPage.tsx`**

**Invite codes** — a **Generate invite code** `Button` calling `createInviteCode`. On success show the `code` and `expiresAtUtc`, with a copy affordance, and state plainly that it is single-use and **cannot be retrieved again**. Do **not** `console.log` the code.

**User actions** — there is **no endpoint that lists users**, so the UI takes a user id in a `Field` rather than pretending to offer a roster. The copy says where an admin gets the id (from the user directly, or the database) instead of implying a directory exists. Three actions on that id:
- **Block** → `blockUser`, **Unblock** → `unblockUser` — each reporting success or the server's `error.title`.
- **Delete** → irreversible and cascades across every tenant table, so it reuses Spec 1's typed-confirmation `Modal`: the confirm button stays disabled until the admin types `DELETE`. **Mount the dialog conditionally** (`{open && <Dialog …/>}`) so its state resets on close — Spec 1 shipped a bug where a lingering dialog reopened pre-filled with the confirm button already enabled.

**Gating:** render nothing (or a "not available" note) unless `useAuth().role === 'Admin'`. State in a comment that this is a UX affordance only — the API enforces the `Admin` policy — so a **403** must still surface if it ever occurs.

- [ ] **Step 2: Write the failing tests**

```tsx
it('shows the generated code and warns it cannot be retrieved again', async () => {
  server.use(http.post('/api/admin/invite-codes', () =>
    HttpResponse.json({ code: 'ABC123', expiresAtUtc: '2026-09-01T00:00:00Z' })));
  renderAdmin({ role: 'Admin' });

  await userEvent.click(screen.getByRole('button', { name: /generate/i }));

  expect(await screen.findByText('ABC123')).toBeInTheDocument();
  expect(screen.getByText(/cannot be (retrieved|shown) again/i)).toBeInTheDocument();
});

it('keeps delete disabled until DELETE is typed', async () => {
  renderAdmin({ role: 'Admin' });
  await userEvent.type(screen.getByLabelText(/user id/i), 'u1');
  await userEvent.click(screen.getByRole('button', { name: /delete user/i }));

  const confirm = await screen.findByRole('button', { name: /^delete$/i });
  expect(confirm).toBeDisabled();

  await userEvent.type(screen.getByLabelText(/type delete/i), 'DELETE');
  expect(confirm).toBeEnabled();
});

it('resets the confirmation when the dialog is reopened', async () => {
  const user = userEvent.setup();
  renderAdmin({ role: 'Admin' });
  await user.type(screen.getByLabelText(/user id/i), 'u1');

  await user.click(screen.getByRole('button', { name: /delete user/i }));
  await user.type(await screen.findByLabelText(/type delete/i), 'DELETE');
  expect(screen.getByRole('button', { name: /^delete$/i })).toBeEnabled();

  await user.keyboard('{Escape}');
  await user.click(screen.getByRole('button', { name: /delete user/i }));

  // Spec 1 shipped exactly this bug: a lingering dialog reopened pre-filled
  // with the confirm button already enabled, so one stray click could erase
  // an account. Conditional mounting is what makes this pass.
  expect(await screen.findByLabelText(/type delete/i)).toHaveValue('');
  expect(screen.getByRole('button', { name: /^delete$/i })).toBeDisabled();
});

it('surfaces a 403 rather than assuming the client role check was sufficient', async () => {
  server.use(http.post('/api/admin/users/u1/block', () =>
    HttpResponse.json({ title: 'Forbidden' }, { status: 403 })));
  renderAdmin({ role: 'Admin' });

  await userEvent.type(screen.getByLabelText(/user id/i), 'u1');
  await userEvent.click(screen.getByRole('button', { name: /block/i }));

  expect(await screen.findByRole('alert')).toHaveTextContent(/forbidden/i);
});

it('does not render admin actions for a non-admin', () => {
  renderAdmin({ role: 'EndUser' });
  expect(screen.queryByRole('button', { name: /generate/i })).not.toBeInTheDocument();
});
```

- [ ] **Step 3: Wire the route, run, and commit**

`<Route path="/admin" element={<AdminPage />} />` inside the guarded shell.

Run: `npm run test && npm run typecheck && npm run lint` — all green; report the count.

```bash
git add src/JiraUnpacked.Web/src/features/admin src/JiraUnpacked.Web/src/App.tsx
git commit -m "feat(web): admin panel for invite codes and user actions"
```

---

## Task 5 (IN5): Navigation and full verification

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/projects/ProjectDetail.tsx`, `src/JiraUnpacked.Web/src/shell/SideNav.tsx`
- Test: extend `ProjectDetail.test.tsx`; create `src/JiraUnpacked.Web/src/shell/SideNav.test.tsx`

**Interfaces:**
- Consumes: the three routes from IN2–IN4, `useAuth()` for `role`.
- Produces: the finished navigation.

- [ ] **Step 1: Add the ProjectDetail links**

Beside the existing **Browse issues** link in the header, add **Metrics** (`/projects/${id}/metrics`) and **Checks** (`/projects/${id}/checks`). Do not disturb the readiness checklist, the tab strip, or the sync controls.

- [ ] **Step 2: Add the admin nav entry**

`SideNav` gains an **Admin** `NavLink` to `/admin`, rendered only when `useAuth().role === 'Admin'`. Everything else in the nav stays as it is.

- [ ] **Step 3: Write the failing tests**

```tsx
// ProjectDetail.test.tsx
it('links to the project’s metrics and checks', async () => {
  renderProjectDetail();
  expect(await screen.findByRole('link', { name: /metrics/i }))
    .toHaveAttribute('href', '/projects/p1/metrics');
  expect(screen.getByRole('link', { name: /checks/i }))
    .toHaveAttribute('href', '/projects/p1/checks');
});

// SideNav.test.tsx
it('shows Admin to an admin', () => {
  renderNav({ role: 'Admin' });
  expect(screen.getByRole('link', { name: /admin/i })).toHaveAttribute('href', '/admin');
});

it('hides Admin from a non-admin', () => {
  renderNav({ role: 'EndUser' });
  expect(screen.queryByRole('link', { name: /admin/i })).not.toBeInTheDocument();
});
```

- [ ] **Step 4: Verify and commit**

From `src/JiraUnpacked.Web/`: `npm run test && npm run typecheck && npm run lint && npm run build` — all green. **Run the test suite twice** and report both counts so nothing is flaky. **Do not run `dotnet test`** — the controller runs it.

```bash
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): navigation to metrics, checks, and the admin panel"
```

---

## Final verification (controller, after all tasks)

- [ ] From `src/JiraUnpacked.Web/`: `npm run typecheck && npm run lint && npm run test && npm run build` → all green, twice.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → **278 passed**, unchanged (controller runs this).
- [ ] `git diff develop..HEAD --stat -- src/JiraUnpacked.Api src/JiraUnpacked.Domain src/JiraUnpacked.Infrastructure tests/` → **empty**. Any backend file is a Critical defect.
- [ ] `git diff develop..HEAD -- src/JiraUnpacked.Web/package.json` → no dependency added — **especially no charting library**.
- [ ] `grep -rn "from '\.\./api\|from '\.\./auth" src/JiraUnpacked.Web/src/ui/` → no matches.
- [ ] `git diff develop..HEAD -- src/JiraUnpacked.Web/src/auth/session.ts` → empty.
- [ ] `grep -rniE "console\.(log|debug|info)" src/JiraUnpacked.Web/src/features/admin/` → no matches (an invite code must never be logged).
- [ ] Confirm `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is unmodified.
- [ ] Push `feat/web-ui-insight`; PR → develop; CI green on **both** jobs; squash-merge. The `develop → main` release is a separate, user-approved step.

## Notes for execution

- **`NotConfigured` is the single most important thing in this plan.** It and a clean run both return zero violations. If the two renderings ever share copy, a user will conclude their epics are healthy when nothing was checked. There is a test asserting they share none.
- **A null average is not zero, and a real zero is not "no data".** Both directions are tested, because collapsing either way misinforms.
- **No charting library.** Spec 1 anticipated one; with the data in view all four metrics are bar-shaped and CSS bars plus a table are more accurate, more accessible, and free. If a later report needs multi-series or scatter, revisit then — not now.
- **There is no admin user-list endpoint.** Take the id as input; do not invent a roster, and do not add an endpoint.
- **Every hook above every conditional return** — this cost a fix round in Spec 2.
- **Do not run `dotnet test` in a subagent** — two agents have been killed by the harness watchdog waiting on it.
- This spec **closes the Web UI epic**. Remaining work is backend-side: 502-not-401 for rejected Jira credentials, an admin user-list endpoint, a filter-catalog endpoint, saved filters, cross-field OR, and httpOnly-cookie auth.
