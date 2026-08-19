# Web UI Spec 3 (Explore) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the synced data visible — a paged issue list, a filter builder over FR-E4's closed field catalog, and issue detail with hierarchy and change history.

**Architecture:** One React-free API module over Spec 1's `apiFetch`, plus a **pure `filterCatalog` module** that mirrors the server's closed allow-list so the UI can offer only valid field/operator pairs. Every read goes through `POST /issues/query` — it subsumes the plain GET, since FR-E4 guarantees empty conditions return the unfiltered page. The catalog mirror is a convenience only: the server stays authoritative and its 400s always render.

**Tech Stack:** React 19.2 · TypeScript strict · Vite 6 · Tailwind v4 · React Router 7 · TanStack Query 5 · Vitest + React Testing Library + MSW. No new dependencies.

## Global Constraints

- **NO backend change at all.** All three endpoints exist and are tested. If a screen appears to need a new endpoint, a sort parameter, or a new field, **re-scope the screen and report it** — never widen the API.
- **NO new runtime dependency.** Exactly `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`. No table, form, date, or virtualisation library.
- **No new UI primitive** unless a screen genuinely cannot be built from the existing eleven in `src/ui/`.
- **Layering:** `src/ui/` imports neither `src/api/` nor `src/auth/`; `src/api/client.ts` imports neither React nor `../auth` (the `AuthBridge` seam).
- **Do not modify** `src/auth/session.ts` — `refreshOnce()`'s single-flight logic and every `hits`/`refreshHits` assertion stay untouched.
- Access token never persisted; **no token, password, or request body logged**.
- `QueryClient` keeps `retry: false` and `refetchOnWindowFocus: false`.
- **All TanStack query keys come from `src/api/queryKeys.ts`** — never inline a key array.
- **Every hook call goes above any conditional `return`**, paired with optional chaining (`data?.`) since query data is `undefined` while pending. A hook after an early return throws "Rendered more hooks than during the previous render". This cost a fix round in Spec 2.
- **The catalog mirror is a convenience; the server is authoritative.** A server 400 must always render verbatim and must **never** be suppressed because client validation passed.
- **No sort UI.** The API fixes the order at `JiraUpdatedAt DESC, JiraKey` and exposes no sort parameter; a sort control would be a lie.
- Tests target logic, **no snapshot tests**. No test may pass if the behaviour it names is broken.
- The .NET suite (**278**) stays green and untouched; the FR-B2 gate is not touched.
- Every task ends with a commit. Never push, open a PR, or merge.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/api/issues.ts` | `queryIssues` / `getIssueDetail` wrappers + all Spec 3 types |
| `src/api/queryKeys.ts` | *(modify)* add `issues` and `issueDetail` keys |
| `src/features/issues/filterCatalog.ts` | **Pure** mirror of the server catalog + lookup helpers |
| `src/features/issues/ConditionRow.tsx` | One field → operator → value row |
| `src/features/issues/FilterBuilder.tsx` | Condition list, add/remove, guards, collapsed summary |
| `src/features/issues/IssueList.tsx` | Table + pager + the two distinct empty states |
| `src/features/issues/IssueDetail.tsx` | Header + hierarchy + change history |
| `src/features/projects/ProjectDetail.tsx` | *(modify)* "Browse issues" link |
| `src/routes/Home.tsx` | *(modify)* project rows link to their issue list |
| `src/App.tsx` | *(modify)* two routes inside the guarded shell |

**Why the catalog is its own pure module:** the field → (type, operators) matrix and the operator-reset rule are the logic most likely to drift from the server, and they are fully testable without rendering. Keeping them out of the components means the drift test is fast and unambiguous.

---

## Task 1 (EX1): API layer and the mirrored field catalog

**Files:**
- Create: `src/JiraUnpacked.Web/src/api/issues.ts`, `src/JiraUnpacked.Web/src/features/issues/filterCatalog.ts`
- Modify: `src/JiraUnpacked.Web/src/api/queryKeys.ts`
- Test: `src/JiraUnpacked.Web/src/features/issues/filterCatalog.test.ts`, `src/JiraUnpacked.Web/src/api/issues.test.ts`

**Interfaces:**
- Consumes: `apiFetch<T>` from `./client`, `ApiError` from `./errors`.
- Produces: everything the later tasks import — listed in the code below.

- [ ] **Step 1: Write `src/api/issues.ts`**

```ts
import { apiFetch } from './client';

export type FilterOperator =
  | 'Eq' | 'Neq' | 'In' | 'Lt' | 'Lte' | 'Gt' | 'Gte' | 'Contains' | 'StartsWith' | 'IsNull';

/**
 * Mirrors the API's FilterCondition. `value` and `values` are mutually
 * exclusive: single-value operators send `value`, `In` sends `values`, and
 * `IsNull` sends neither. Sending the wrong one is accepted by TypeScript
 * and rejected by the server at runtime, so the builder must get it right.
 */
export interface FilterCondition {
  field: string;
  op: FilterOperator;
  value?: string | number;
  values?: (string | number)[];
}

export interface IssueFilterRequest {
  conditions?: FilterCondition[];
  page?: number;
  pageSize?: number;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** Dictionary fields arrive as resolved display names, not ids. Most are nullable. */
export interface IssueListItem {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
  status: string | null;
  issueType: string | null;
  priority: string | null;
  assignee: string | null;
  sprint: string | null;
  team: string | null;
  storyPoints: number | null;
  parentJiraIssueId: string | null;
  jiraCreatedAt: string;
  jiraUpdatedAt: string;
}

export interface IssueRef {
  jiraIssueId: string;
  jiraKey: string;
  summary: string | null;
}

export interface ChangeHistoryEntry {
  field: string;
  fromValue: string | null;
  toValue: string | null;
  author: string | null;
  changedAt: string;
}

export interface IssueDetail {
  issue: IssueListItem;
  parent: IssueRef | null;
  children: IssueRef[];
  changeHistory: ChangeHistoryEntry[];
}

export const MAX_PAGE_SIZE = 100;
export const DEFAULT_PAGE_SIZE = 50;

/**
 * The only list read this spec uses. POST /issues/query subsumes GET /issues:
 * FR-E4 guarantees empty/null conditions return the unfiltered page with the
 * same ordering and paging, so a second GET code path would add surface
 * without adding behaviour.
 */
export const queryIssues = (projectId: string, request: IssueFilterRequest) =>
  apiFetch<PagedResult<IssueListItem>>(`/api/projects/${projectId}/issues/query`, {
    method: 'POST', body: request,
  });

export const getIssueDetail = (projectId: string, jiraIssueId: string) =>
  apiFetch<IssueDetail>(`/api/projects/${projectId}/issues/${encodeURIComponent(jiraIssueId)}`);
```

Note `encodeURIComponent` on `jiraIssueId`: it is a Jira-supplied string in a path segment, so an unencoded value would break the URL.

- [ ] **Step 2: Add the two query keys to `src/api/queryKeys.ts`**

Add these entries to the existing `queryKeys` object (keep every existing entry unchanged):

```ts
  // The request is part of the key so a different filter or page is a
  // different cache entry rather than silently reusing the previous results.
  issues: (projectId: string, request: unknown) =>
    ['projects', projectId, 'issues', JSON.stringify(request ?? {})] as const,
  issueDetail: (projectId: string, jiraIssueId: string) =>
    ['projects', projectId, 'issues', jiraIssueId] as const,
```

- [ ] **Step 3: Write the failing catalog test**

`src/features/issues/filterCatalog.test.ts`:
```ts
import { describe, expect, it } from 'vitest';
import {
  FIELD_CATALOG, FILTER_FIELDS, MAX_CONDITIONS, MAX_IN_VALUES,
  isOperatorAllowed, operatorsFor, resetOperatorIfDisallowed, typeFor,
} from './filterCatalog';

// This matrix is a mirror of FilterFields.All in the .NET Infrastructure
// project. Asserting it field by field means server-side drift fails here
// loudly instead of surfacing as a 400 in production.
const EXPECTED: Record<string, { type: string; ops: string[] }> = {
  status:      { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  issueType:   { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  priority:    { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  assignee:    { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  sprint:      { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  team:        { type: 'DictName',  ops: ['Eq', 'Neq', 'In', 'IsNull'] },
  storyPoints: { type: 'Decimal',   ops: ['Eq', 'Neq', 'Lt', 'Lte', 'Gt', 'Gte', 'IsNull'] },
  createdAt:   { type: 'DateTime',  ops: ['Lt', 'Lte', 'Gt', 'Gte'] },
  updatedAt:   { type: 'DateTime',  ops: ['Lt', 'Lte', 'Gt', 'Gte'] },
  summary:     { type: 'Text',      ops: ['Contains', 'StartsWith', 'IsNull'] },
  jiraKey:     { type: 'Text',      ops: ['Eq', 'Contains', 'StartsWith'] },
  parent:      { type: 'ParentRef', ops: ['Eq', 'IsNull'] },
};

describe('FIELD_CATALOG mirrors the server allow-list', () => {
  it('offers exactly the twelve catalog fields and no others', () => {
    expect([...FILTER_FIELDS].sort()).toEqual(Object.keys(EXPECTED).sort());
  });

  it.each(Object.keys(EXPECTED))('matches the server type and operators for %s', (field) => {
    expect(typeFor(field)).toBe(EXPECTED[field].type);
    expect([...operatorsFor(field)]).toEqual(EXPECTED[field].ops);
  });

  it('exposes the FR-E4 guard limits', () => {
    expect(MAX_CONDITIONS).toBe(20);
    expect(MAX_IN_VALUES).toBe(100);
  });

  it('reports an unknown field as having no operators', () => {
    expect(operatorsFor('nope')).toEqual([]);
    expect(isOperatorAllowed('nope', 'Eq')).toBe(false);
  });
});

describe('resetOperatorIfDisallowed', () => {
  it('keeps an operator the new field still allows', () => {
    // Both status and issueType allow Eq.
    expect(resetOperatorIfDisallowed('issueType', 'Eq')).toBe('Eq');
  });

  it('falls back to the new field’s first operator when the old one is invalid', () => {
    // createdAt does not allow Contains; its first operator is Lt.
    expect(resetOperatorIfDisallowed('createdAt', 'Contains')).toBe('Lt');
  });

  it('falls back for an unknown field rather than throwing', () => {
    expect(resetOperatorIfDisallowed('nope', 'Eq')).toBe('Eq');
  });
});

describe('catalog entry shape', () => {
  it('gives every field a non-empty operator list', () => {
    for (const field of FILTER_FIELDS) {
      expect(operatorsFor(field).length).toBeGreaterThan(0);
      expect(FIELD_CATALOG[field]).toBeDefined();
    }
  });
});
```

- [ ] **Step 4: Run to verify it fails**

Run from `src/JiraUnpacked.Web/`: `npm run test -- filterCatalog`
Expected: FAIL — cannot resolve `./filterCatalog`.

- [ ] **Step 5: Implement `src/features/issues/filterCatalog.ts`**

```ts
import type { FilterOperator } from '../../api/issues';

/**
 * A mirror of FilterFields.All in src/JiraUnpacked.Infrastructure/Reporting/
 * Filtering/FilterField.cs. No endpoint exposes that catalog, so the UI keeps
 * its own copy to render valid operators and the right value input.
 *
 * This mirror is a CONVENIENCE, never an authority. The server validates every
 * request and its 400 must always reach the user verbatim. If a field is added
 * server-side and not here it is merely missing from the UI; if one is removed
 * or renamed, the UI offers a field the server rejects — visible and harmless.
 * filterCatalog.test.ts asserts the matrix so drift fails there first.
 */
export type FieldType = 'DictName' | 'Decimal' | 'DateTime' | 'Text' | 'ParentRef';

export interface CatalogEntry {
  type: FieldType;
  operators: readonly FilterOperator[];
}

const dict: CatalogEntry = { type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] };

export const FIELD_CATALOG: Record<string, CatalogEntry> = {
  status: dict,
  issueType: dict,
  priority: dict,
  assignee: dict,
  sprint: dict,
  team: dict,
  storyPoints: { type: 'Decimal', operators: ['Eq', 'Neq', 'Lt', 'Lte', 'Gt', 'Gte', 'IsNull'] },
  createdAt: { type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
  updatedAt: { type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
  summary: { type: 'Text', operators: ['Contains', 'StartsWith', 'IsNull'] },
  jiraKey: { type: 'Text', operators: ['Eq', 'Contains', 'StartsWith'] },
  parent: { type: 'ParentRef', operators: ['Eq', 'IsNull'] },
};

/** Canonical field names, in the order the field picker lists them. */
export const FILTER_FIELDS: readonly string[] = Object.keys(FIELD_CATALOG);

/** FR-E4's guards, mirrored so the UI can block before the server has to. */
export const MAX_CONDITIONS = 20;
export const MAX_IN_VALUES = 100;

export const operatorsFor = (field: string): readonly FilterOperator[] =>
  FIELD_CATALOG[field]?.operators ?? [];

export const typeFor = (field: string): FieldType | undefined => FIELD_CATALOG[field]?.type;

export const isOperatorAllowed = (field: string, op: FilterOperator): boolean =>
  operatorsFor(field).includes(op);

/**
 * When the user changes a row's field, the previously chosen operator may not
 * be valid for the new field. Returning the new field's first operator keeps
 * the row in a submittable state instead of silently sending an invalid pair.
 */
export const resetOperatorIfDisallowed = (field: string, op: FilterOperator): FilterOperator =>
  isOperatorAllowed(field, op) ? op : (operatorsFor(field)[0] ?? 'Eq');
```

- [ ] **Step 6: Run to verify it passes**

Run: `npm run test -- filterCatalog`
Expected: PASS (17 tests — the `it.each` expands to 12).

- [ ] **Step 7: Write the API-wrapper test**

`src/api/issues.test.ts`:
```ts
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
```

- [ ] **Step 8: Run, then commit**

Run: `npm run test && npm run typecheck && npm run lint` — all green (94 prior + 21 new = 115).

```bash
git add src/JiraUnpacked.Web/src/api src/JiraUnpacked.Web/src/features/issues
git commit -m "feat(web): issue query API wrappers and the mirrored filter catalog"
```

---

## Task 2 (EX2): Issue list

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Modify: `src/JiraUnpacked.Web/src/App.tsx` (add `/projects/:id/issues`)
- Test: `src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx`

**Interfaces:**
- Consumes (EX1): `queryIssues`, `IssueListItem`, `PagedResult`, `FilterCondition`, `DEFAULT_PAGE_SIZE`, `MAX_PAGE_SIZE`, `queryKeys.issues`.
- Produces: route `/projects/:id/issues`; `IssueList` owns the `conditions` state that EX3's `FilterBuilder` will drive via `onChange`.

- [ ] **Step 1: Implement `IssueList.tsx`**

Structure — note **every hook sits above the conditional returns**:

```tsx
// `initialConditions` exists so EX2's tests can render a filtered state before
// EX3's builder lands. It defaults to [] and production never passes it.
export default function IssueList({ initialConditions = [] }: { initialConditions?: FilterCondition[] } = {}) {
  const { id = '' } = useParams();
  const [conditions, setConditions] = useState<FilterCondition[]>(initialConditions);
  const [page, setPage] = useState(1);

  const request = { conditions, page, pageSize: DEFAULT_PAGE_SIZE };
  const { data, isPending, error } = useQuery({
    queryKey: queryKeys.issues(id, request),
    queryFn: () => queryIssues(id, request),
    // Keep the previous page/filter on screen while the next loads, so a 400
    // or a slow page does not blank the table the user is reading.
    placeholderData: (prev) => prev,
  });
  …
}
```

Then, after the hooks:
- `isPending && !data` → `<Spinner aria-label="Loading issues" />`.
- `error` → render `error instanceof ApiError ? error.title : 'Could not load issues.'` in a `role="alert"` **above** the table, and keep `data` rendered beneath it if present. A 400 from a bad filter must not blank the results.
- `data.items.length === 0` → **two distinct empty states**:
  - `conditions.length === 0` → `<EmptyState title="No issues yet" description="Run a sync to pull this project's issues from Jira." action={<Link to={`/projects/${id}`}>Go to sync</Link>} />`
  - otherwise → `<EmptyState title="No issues match this filter" description="Try removing or loosening a condition." />`
  These must be genuinely different — one tells the user to sync a project that may be working fine, the other to change their filter. A single shared empty state is a defect.
- Otherwise a `Table` with columns Key, Summary, Status, Type, Assignee, Sprint, Points, Updated. `jiraKey` links to `/projects/${id}/issues/${encodeURIComponent(item.jiraIssueId)}`. Nullable columns render `'—'` when null (never the string "null").
- A pager below: **Previous** / **Next** buttons and a line reading `Page {data.page} of {totalPages} · {data.totalCount} issues`, where `totalPages = Math.max(1, Math.ceil(totalCount / pageSize))`. Previous is disabled on page 1; Next is disabled on the last page.

Leave a clearly commented slot above the table where EX3 mounts `<FilterBuilder conditions={conditions} onChange={…} />` — for now render nothing there.

**Resetting the page:** whenever `conditions` change, reset `page` to 1 — otherwise a narrower filter can leave the user stranded on a page that no longer exists, showing an empty table that looks like "no matches".

- [ ] **Step 2: Write the failing tests**

`IssueList.test.tsx`:
```tsx
it('tells an unfiltered empty project to run a sync', async () => {
  server.use(http.post('/api/projects/p1/issues/query', () =>
    HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })));

  renderList();

  expect(await screen.findByText(/no issues yet/i)).toBeInTheDocument();
  expect(screen.getByRole('link', { name: /go to sync/i })).toBeInTheDocument();
  expect(screen.queryByText(/no issues match/i)).not.toBeInTheDocument();
});

it('tells a filtered empty result to change the filter, not to sync', async () => {
  server.use(http.post('/api/projects/p1/issues/query', () =>
    HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })));

  // Render with a condition already present, so the empty result is a
  // filtered one. EX3's builder produces this state interactively.
  renderList({ initialConditions: [{ field: 'status', op: 'Eq', value: 'Done' }] });

  expect(await screen.findByText(/no issues match this filter/i)).toBeInTheDocument();
  expect(screen.queryByText(/no issues yet/i)).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: /go to sync/i })).not.toBeInTheDocument();
});

it('requests page 2 when Next is clicked', async () => {
  const pages: number[] = [];
  server.use(http.post('/api/projects/p1/issues/query', async ({ request }) => {
    const body = (await request.json()) as { page: number };
    pages.push(body.page);
    return HttpResponse.json({ items: [row('ACME-1')], page: body.page, pageSize: 50, totalCount: 120 });
  }));
  renderList();
  await screen.findByText('ACME-1');

  await userEvent.click(screen.getByRole('button', { name: /next/i }));

  await waitFor(() => expect(pages).toContain(2));
});

it('never requests a pageSize above the API maximum', async () => {
  let sent = 0;
  server.use(http.post('/api/projects/p1/issues/query', async ({ request }) => {
    sent = ((await request.json()) as { pageSize: number }).pageSize;
    return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
  }));
  renderList();
  await waitFor(() => expect(sent).toBeLessThanOrEqual(MAX_PAGE_SIZE));
});

it('keeps the previous results on screen when the server rejects a filter', async () => {
  // First response succeeds with one row; second returns 400.
  // Assert the row is STILL rendered alongside the alert.
  expect(await screen.findByRole('alert')).toHaveTextContent(/unknown field/i);
  expect(screen.getByText('ACME-1')).toBeInTheDocument();
});
```

- [ ] **Step 3: Wire the route in `App.tsx`**

Inside the existing `<Route element={<RequireAuth />}><Route element={<AppShell />}>` block:
```tsx
<Route path="/projects/:id/issues" element={<IssueList />} />
```
Leave the `QueryClient` options untouched.

- [ ] **Step 4: Run and commit**

Run: `npm run test && npm run typecheck && npm run lint` — all green; report the count.

```bash
git add src/JiraUnpacked.Web/src/features/issues src/JiraUnpacked.Web/src/App.tsx
git commit -m "feat(web): paged issue list with distinct empty states"
```

---

## Task 3 (EX3): Filter builder

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx`, `FilterBuilder.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx` (mount the builder in its slot)
- Test: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`

**Interfaces:**
- Consumes (EX1): `FIELD_CATALOG`, `FILTER_FIELDS`, `operatorsFor`, `typeFor`, `resetOperatorIfDisallowed`, `MAX_CONDITIONS`, `MAX_IN_VALUES`, `FilterCondition`, `FilterOperator`.
- Produces: `<FilterBuilder conditions={FilterCondition[]} onChange={(next: FilterCondition[]) => void} />`.

- [ ] **Step 1: Implement `ConditionRow.tsx`**

Props: `{ condition: FilterCondition; onChange: (next: FilterCondition) => void; onRemove: () => void; index: number }`.

Three controls plus a Remove `Button`:
1. **Field** `Select` over `FILTER_FIELDS`. On change, emit `{ field: next, op: resetOperatorIfDisallowed(next, condition.op), …cleared value }` — dropping the old value too, since a value valid for one type rarely is for another.
2. **Operator** `Select` over `operatorsFor(condition.field)`.
3. **Value**, switched on `typeFor(condition.field)` **and** the operator:
   - `op === 'IsNull'` → render **no input at all**, and the emitted condition carries **neither** `value` nor `values`.
   - `op === 'In'` → a multi-value editor: a text input plus **Add**, showing added values as removable `Badge`s. Emits `values: string[]`. Adding is blocked at `MAX_IN_VALUES` with a message.
   - `Decimal` → `<input type="number">`, emitting a **number** in `value`.
   - `DateTime` → `<input type="date">`, emitting the ISO date string in `value`.
   - otherwise → `<input type="text">`, emitting a string in `value`.

Every input is wrapped in a `Field` with a label naming the row, e.g. `Value for condition ${index + 1}`, so tests and screen readers can address it.

- [ ] **Step 2: Implement `FilterBuilder.tsx`**

- When `conditions.length === 0`: a collapsed summary line — "No filter — showing all issues" plus an **Add condition** `Button`.
- Otherwise: one `ConditionRow` per condition, an **Add condition** `Button`, and a **Clear all** `Button`.
- **Add condition** appends `{ field: 'status', op: 'Eq', value: '' }` and is **disabled at `MAX_CONDITIONS`**, with a message naming the limit.
- All changes flow up through `onChange` — the builder holds no server state of its own.
- **When any row uses `Neq` on a dictionary field**, render an inline note beneath the rows:
  *"`Neq` on status, type, priority, assignee, sprint, or team also matches issues where the field is empty."*
  FR-E4 chose that behaviour deliberately (`status ≠ Done` returns issues with no status at all) and the
  opposite reading is equally plausible, so leaving it unsaid would mislead. Spec §4 requires this.

- [ ] **Step 3: Write the failing tests — the request shape is the centrepiece**

`FilterBuilder.test.tsx` mounts `IssueList` (so the built conditions reach a real request) and inspects the POST body:

```tsx
it('sends neither value nor values for IsNull', async () => {
  // add a condition, pick field=assignee, operator=IsNull, apply
  expect(body.conditions).toEqual([{ field: 'assignee', op: 'IsNull' }]);
  expect('value' in body.conditions[0]).toBe(false);
  expect('values' in body.conditions[0]).toBe(false);
});

it('sends values (not value) for In', async () => {
  // field=status, op=In, add "Done" and "In Progress"
  expect(body.conditions).toEqual([
    { field: 'status', op: 'In', values: ['Done', 'In Progress'] },
  ]);
  expect('value' in body.conditions[0]).toBe(false);
});

it('sends value (not values) for a single-value operator', async () => {
  // field=summary, op=Contains, value="login"
  expect(body.conditions).toEqual([{ field: 'summary', op: 'Contains', value: 'login' }]);
  expect('values' in body.conditions[0]).toBe(false);
});

it('sends a number for a Decimal field', async () => {
  // field=storyPoints, op=Gte, value=3
  expect(body.conditions).toEqual([{ field: 'storyPoints', op: 'Gte', value: 3 }]);
  expect(typeof body.conditions[0].value).toBe('number');
});

it('resets an operator the new field does not allow', async () => {
  // field=summary op=Contains, then switch field to createdAt
  // createdAt allows only Lt/Lte/Gt/Gte, so the operator select must not still read Contains
  expect(screen.getByLabelText(/operator for condition 1/i)).toHaveValue('Lt');
});

it('offers only the operators the chosen field allows', async () => {
  // field=jiraKey -> Eq, Contains, StartsWith and nothing else
  const ops = within(screen.getByLabelText(/operator for condition 1/i))
    .getAllByRole('option').map((o) => o.textContent);
  expect(ops).toEqual(['Eq', 'Contains', 'StartsWith']);
});

it('blocks a 21st condition', async () => {
  // add MAX_CONDITIONS conditions
  expect(screen.getByRole('button', { name: /add condition/i })).toBeDisabled();
  expect(screen.getByText(/at most 20 conditions/i)).toBeInTheDocument();
});

it('blocks a 101st In value', async () => {
  // field=status op=In, add MAX_IN_VALUES values
  expect(screen.getByRole('button', { name: /add value/i })).toBeDisabled();
});

it('still shows a server 400 when the client thought the filter was fine', async () => {
  server.use(http.post('/api/projects/p1/issues/query', () =>
    HttpResponse.json({ title: "Operator 'Contains' is not allowed for field 'createdAt'." },
      { status: 400 })));
  // build any valid-looking filter and let it run
  expect(await screen.findByRole('alert'))
    .toHaveTextContent(/not allowed for field 'createdAt'/i);
});
```

The last test is the one that matters most: the client catalog is a mirror, not an authority, and a screen that swallowed server validation because its own check passed would hide real drift.

- [ ] **Step 4: Mount the builder in `IssueList`'s slot, run, and commit**

Run: `npm run test && npm run typecheck && npm run lint` — all green; report the count.

```bash
git add src/JiraUnpacked.Web/src/features/issues
git commit -m "feat(web): FR-E4 filter builder with catalog-driven operators and guards"
```

---

## Task 4 (EX4): Issue detail

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/IssueDetail.tsx`
- Modify: `src/JiraUnpacked.Web/src/App.tsx` (add `/projects/:id/issues/:jiraIssueId`)
- Test: `src/JiraUnpacked.Web/src/features/issues/IssueDetail.test.tsx`

**Interfaces:**
- Consumes (EX1): `getIssueDetail`, `IssueDetail` (type), `IssueRef`, `ChangeHistoryEntry`, `queryKeys.issueDetail`.
- Produces: route `/projects/:id/issues/:jiraIssueId`.

- [ ] **Step 1: Implement `IssueDetail.tsx`**

**Name collision to avoid:** this file's default export is a component called `IssueDetail`, and `api/issues.ts`
exports a *type* with the same name. Import the type under an alias so the two never shadow each other:
```ts
import type { IssueDetail as IssueDetailData, IssueRef, ChangeHistoryEntry } from '../../api/issues';
```

`useQuery({ queryKey: queryKeys.issueDetail(id, jiraIssueId), queryFn: () => getIssueDetail(id, jiraIssueId) })` — the hook above any conditional return, with `data?.` access.

- Loading → `<Spinner aria-label="Loading issue" />`. Error → `ErrorState` with `error instanceof ApiError ? error.title : 'Could not load the issue.'` (a 404 means the issue is not in this project).
- **Header:** `jiraKey` as the heading, `summary ?? '—'` beneath, and `Badge`s for the non-null dictionary values (status, type, priority, assignee, sprint, team) plus story points. Null values are omitted rather than rendered as empty badges.
- **Hierarchy:** if `parent`, a link `/projects/${id}/issues/${encodeURIComponent(parent.jiraIssueId)}` labelled `${parent.jiraKey} — ${parent.summary ?? ''}`. If `children.length`, the same per child. If neither, "No parent or children".
- **Change history:** a `Table` of Field, From, To, Author, Changed. `fromValue`/`toValue`/`author` render `'—'` when null — **never the string "null"**, which is what naive interpolation produces and reads as real data. The API returns history newest-first; render it in the order received.
- Empty history → the text **"No recorded changes"**, not an `ErrorState`. An issue with no edits is normal, not a failure.
- A back link to `/projects/${id}/issues`.

- [ ] **Step 2: Write the failing tests**

```tsx
const detail = (over: Partial<IssueDetail> = {}): IssueDetail => ({
  issue: { jiraIssueId: 'i1', jiraKey: 'ACME-1', summary: 'Login is broken', status: 'Done',
    issueType: 'Bug', priority: null, assignee: 'Sam', sprint: null, team: null,
    storyPoints: 3, parentJiraIssueId: 'i0',
    jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-02-01T00:00:00Z' },
  parent: { jiraIssueId: 'i0', jiraKey: 'ACME-0', summary: 'Epic' },
  children: [{ jiraIssueId: 'i2', jiraKey: 'ACME-2', summary: 'Sub' }],
  changeHistory: [],
  ...over,
});

it('links the parent and each child to their own detail route', async () => {
  server.use(http.get('/api/projects/p1/issues/i1', () => HttpResponse.json(detail())));
  renderDetail();

  expect(await screen.findByRole('link', { name: /ACME-0/ }))
    .toHaveAttribute('href', '/projects/p1/issues/i0');
  expect(screen.getByRole('link', { name: /ACME-2/ }))
    .toHaveAttribute('href', '/projects/p1/issues/i2');
});

it('says there are no recorded changes rather than showing an error', async () => {
  server.use(http.get('/api/projects/p1/issues/i1', () =>
    HttpResponse.json(detail({ changeHistory: [] }))));
  renderDetail();

  expect(await screen.findByText(/no recorded changes/i)).toBeInTheDocument();
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
});

it('renders a null fromValue as a dash, never the string "null"', async () => {
  server.use(http.get('/api/projects/p1/issues/i1', () => HttpResponse.json(detail({
    changeHistory: [{ field: 'status', fromValue: null, toValue: 'Done',
      author: null, changedAt: '2026-01-15T00:00:00Z' }],
  }))));
  renderDetail();

  await screen.findByText('Done');
  expect(screen.queryByText('null')).not.toBeInTheDocument();
  expect(screen.getAllByText('—').length).toBeGreaterThanOrEqual(2); // fromValue and author
});

it('omits badges for null dictionary values', async () => {
  server.use(http.get('/api/projects/p1/issues/i1', () => HttpResponse.json(detail())));
  renderDetail();

  expect(await screen.findByText('Done')).toBeInTheDocument();   // status present
  expect(screen.queryByText('null')).not.toBeInTheDocument();     // priority/sprint/team are null
});
```

- [ ] **Step 3: Wire the route, run, and commit**

In `App.tsx`, inside the guarded shell — **after** the list route so the more specific path is registered alongside it:
```tsx
<Route path="/projects/:id/issues/:jiraIssueId" element={<IssueDetail />} />
```

Run: `npm run test && npm run typecheck && npm run lint` — all green; report the count.

```bash
git add src/JiraUnpacked.Web/src/features/issues src/JiraUnpacked.Web/src/App.tsx
git commit -m "feat(web): issue detail with hierarchy and change history"
```

---

## Task 5 (EX5): Navigation and full verification

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/projects/ProjectDetail.tsx`, `src/JiraUnpacked.Web/src/routes/Home.tsx`
- Test: `src/JiraUnpacked.Web/src/features/projects/ProjectDetail.test.tsx` (extend), `src/JiraUnpacked.Web/src/routes/Home.test.tsx` (extend)

**Interfaces:**
- Consumes: the routes added in EX2 and EX4.
- Produces: the finished navigation path from "I just synced" to "show me the data".

- [ ] **Step 1: Add the ProjectDetail link**

In `ProjectDetail`'s header, beside the existing project key and interval controls, add:
```tsx
<Link to={`/projects/${id}/issues`}>Browse issues</Link>
```
Do not disturb the readiness checklist, the tab strip, or the sync controls.

- [ ] **Step 2: Link Home's project rows to their issue lists**

In `Home.tsx`'s projects table, make each project row link to `/projects/${p.id}/issues` **in addition to** the existing link to `/projects/${p.id}`. Label them distinctly — e.g. the key links to the project (configuration) and a separate "Issues" cell links to the list — so the two destinations are not ambiguous.

- [ ] **Step 3: Extend the existing tests**

Add to `ProjectDetail.test.tsx`:
```tsx
it('offers a link to the project’s issue list', async () => {
  renderProjectDetail();
  expect(await screen.findByRole('link', { name: /browse issues/i }))
    .toHaveAttribute('href', '/projects/p1/issues');
});
```

Add to `Home.test.tsx`:
```tsx
it('links each project to its issue list', async () => {
  renderHome();   // existing fixture: one connection with project p1
  expect(await screen.findByRole('link', { name: /issues/i }))
    .toHaveAttribute('href', '/projects/p1/issues');
});
```

- [ ] **Step 4: Full verification**

From `src/JiraUnpacked.Web/`: `npm run test && npm run typecheck && npm run lint && npm run build` — all green; report the count.
From the repo root: `dotnet test JiraUnpacked.sln --configuration Release` — must be **278 passed**, unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): navigation from projects to the issue list"
```

---

## Final verification (controller, after all tasks)

- [ ] From `src/JiraUnpacked.Web/`: `npm run typecheck && npm run lint && npm run test && npm run build` → all green.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → **278 passed**, unchanged.
- [ ] `git diff develop..HEAD --stat -- src/JiraUnpacked.Api src/JiraUnpacked.Domain src/JiraUnpacked.Infrastructure tests/` → **empty**. Any backend file is a Critical defect.
- [ ] `git diff develop..HEAD -- src/JiraUnpacked.Web/package.json` → no dependency added.
- [ ] `grep -rn "from '\.\./api\|from '\.\./auth" src/JiraUnpacked.Web/src/ui/` → no matches.
- [ ] `git diff develop..HEAD -- src/JiraUnpacked.Web/src/auth/session.ts` → empty.
- [ ] `grep -rn "issues/query\|/issues/" src/JiraUnpacked.Web/src/features/issues/` shows requests built only through `api/issues.ts` — no ad-hoc `fetch`.
- [ ] Confirm `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` is unmodified.
- [ ] Push `feat/web-ui-explore`; PR → develop; CI green on **both** jobs; squash-merge. The `develop → main` release is a separate, user-approved step.

## Notes for execution

- **The request-shape tests are the most valuable in this plan.** `value` vs `values` vs neither is invisible to TypeScript (both are optional) and produces a runtime 400. Assert the exact `conditions` array, including that the wrong key is *absent*.
- **The catalog is a mirror, never an authority.** Client validation exists to make invalid filters hard to express; it must never suppress a server 400. There is an explicit test for that.
- **The two empty states must stay distinct.** "No issues yet" sends the user to sync; "no matches" sends them to their filter. Collapsing them sends people to re-sync a healthy project.
- **Reset the page when conditions change**, or a narrowed filter strands the user on a page that no longer exists and looks like "no matches".
- **Every hook above every conditional return** — this cost a fix round in Spec 2.
- Deferred to Spec 4: metrics, gap-checks, admin. Worth a backend spec later: a filter-catalog endpoint (removes this mirror), saved filters, a sort parameter, cross-field OR.
