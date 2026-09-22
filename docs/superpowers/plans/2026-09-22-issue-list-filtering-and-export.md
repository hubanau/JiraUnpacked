# Issue List: Filtering, Columns, Sorting and Export — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the issue list usable for real work — pick values instead of typing them, combine conditions with OR, choose and sort columns, and take the result away as CSV.

**Architecture:** Two of the five asks need no new query-layer code: `FilterGroup` already supports `And`/`Or`, and `IssueListItem` already projects every column worth showing. Those ship first as pure UI. The remaining three add a project-scoped value-list endpoint, a closed sort catalog mirroring the existing filter catalog, and a streamed CSV export. All list state moves into the URL, making views shareable without touching `localStorage`.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit + WireMock, React 19, react-router-dom, @tanstack/react-query, vitest + Testing Library + msw.

**Spec:** `docs/superpowers/specs/2026-09-22-issue-list-filtering-and-export-design.md`

## Global Constraints

- **Filter AND sort input must never reach a SQL string.** Closed catalogs + Expression trees; `EF.Functions.ILike` with escaped wildcards for text.
- All reads through the injected tenant-scoped `AppDbContext`. No `IgnoreQueryFilters`, no privileged factory, no `FromSql`.
- **NO new tenant-owned table and NO schema change.** `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` stays untouched and green.
- SPA keeps exactly four runtime deps: `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`. CSV is produced server-side.
- `src/ui/` imports neither `src/api/` nor `src/auth/`.
- Only `ju.username` and `ju.theme` may go to `localStorage`. List state goes in the URL.
- No new database indexes.
- Cut a branch from `develop`: `git checkout develop && git pull && git checkout -b feat/issue-list-views`. Do **not** build on `feat/resumable-batched-sync` (PR #116, unmerged).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Web/src/features/issues/useListState.ts` | **new** — read/write filter, logic, sort, columns as URL params |
| `src/JiraUnpacked.Web/src/features/issues/columns.ts` | **new** — the column catalog: id, header, accessor |
| `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.tsx` | **new** — checkbox list over the column catalog |
| `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.tsx` | + All/Any toggle |
| `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` | + value picker for dictionary fields |
| `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx` | state from the URL; dynamic columns; sortable headers; export button |
| `src/JiraUnpacked.Web/src/api/issues.ts` | + `getFilterValues`, `sortBy`/`sortDescending`, `exportUrl` |
| `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs` | + `SortBy`, `SortDescending` on `IssueFilterRequest` |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/SortFields.cs` | **new** — closed sort catalog |
| `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs` | apply sort; add streaming export query |
| `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs` | **new** — distinct values for a dictionary field |
| `src/JiraUnpacked.Infrastructure/Reporting/CsvWriter.cs` | **new** — quoting + formula neutralisation |
| `src/JiraUnpacked.Api/Controllers/IssuesController.cs` | + `filter-values`, + `export.csv` |

---

### Task 1: List state lives in the URL

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/useListState.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/useListState.test.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`

**Interfaces:**
- Produces: `useListState(): { conditions, logic, sortBy, sortDescending, columns, setConditions, setLogic, setSort, setColumns }`, where `logic: 'And' | 'Or'`, `sortBy: string`, `sortDescending: boolean`, `columns: string[]`.

- [ ] **Step 1: Write the failing test**

```ts
import { renderHook, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { useListState } from './useListState';

const wrap = (initial: string) =>
  ({ children }: { children: React.ReactNode }) => (
    <MemoryRouter initialEntries={[initial]}>{children}</MemoryRouter>
  );

describe('useListState', () => {
  it('round-trips a filter through the URL so a view can be shared', () => {
    const { result } = renderHook(() => useListState(), { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.setConditions([{ field: 'status', op: 'Eq', value: 'Backlog' }]));

    expect(result.current.conditions).toEqual([{ field: 'status', op: 'Eq', value: 'Backlog' }]);
  });

  it('reads state from the URL on first render, so a pasted link arrives filtered', () => {
    const url = '/projects/p1/issues?logic=Or&sortBy=storyPoints&sortDesc=false&cols=jiraKey,summary';
    const { result } = renderHook(() => useListState(), { wrapper: wrap(url) });

    expect(result.current.logic).toBe('Or');
    expect(result.current.sortBy).toBe('storyPoints');
    expect(result.current.sortDescending).toBe(false);
    expect(result.current.columns).toEqual(['jiraKey', 'summary']);
  });

  it('falls back to defaults when the URL says nothing', () => {
    const { result } = renderHook(() => useListState(), { wrapper: wrap('/projects/p1/issues') });

    expect(result.current.logic).toBe('And');
    expect(result.current.sortBy).toBe('updatedAt');
    expect(result.current.sortDescending).toBe(true);
    expect(result.current.conditions).toEqual([]);
  });

  it('ignores a malformed filter param rather than crashing the page', () => {
    // A URL is user-editable and gets truncated by chat clients. A broken one must
    // degrade to "no filter", never to a blank screen.
    const { result } = renderHook(() => useListState(), {
      wrapper: wrap('/projects/p1/issues?filter=%7Bnot-json'),
    });

    expect(result.current.conditions).toEqual([]);
  });
});
```

- [ ] **Step 2: Run it and watch it fail**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues/useListState.test.ts`
Expected: FAIL — `useListState` does not exist.

- [ ] **Step 3: Implement the hook**

```ts
import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { FilterCondition, FilterLogic } from '../../api/issues';

export const DEFAULT_SORT = 'updatedAt';
export const DEFAULT_COLUMNS = [
  'jiraKey', 'summary', 'status', 'issueType', 'assignee', 'sprint', 'storyPoints', 'updatedAt',
];

/**
 * The list's whole view — filter, logic, sort, columns — read from and written to the query
 * string.
 *
 * The URL rather than localStorage for two reasons, in order. Only ju.username and ju.theme may
 * be persisted (see the SPA's storage rule), and this is not a reason to widen that. And a URL is
 * shareable: "PLAT bugs this sprint by points" becomes a link, which is most of the value of
 * building a filter at all. It also survives reload and Back, which useState does not.
 */
export function useListState() {
  const [params, setParams] = useSearchParams();

  const conditions = useMemo<FilterCondition[]>(() => {
    const raw = params.get('filter');
    if (!raw) return [];
    try {
      const parsed = JSON.parse(raw);
      return Array.isArray(parsed) ? (parsed as FilterCondition[]) : [];
    } catch {
      // A URL is user-editable and chat clients truncate them. A broken filter degrades to
      // "no filter"; it must never blank the page.
      return [];
    }
  }, [params]);

  const patch = useCallback((next: Record<string, string | null>) => {
    setParams((prev) => {
      const out = new URLSearchParams(prev);
      for (const [k, v] of Object.entries(next)) {
        if (v === null) out.delete(k);
        else out.set(k, v);
      }
      return out;
    }, { replace: true });
  }, [setParams]);

  return {
    conditions,
    logic: (params.get('logic') === 'Or' ? 'Or' : 'And') as FilterLogic,
    sortBy: params.get('sortBy') ?? DEFAULT_SORT,
    sortDescending: params.get('sortDesc') !== 'false',
    columns: params.get('cols')?.split(',').filter(Boolean) ?? DEFAULT_COLUMNS,

    setConditions: (next: FilterCondition[]) =>
      patch({ filter: next.length === 0 ? null : JSON.stringify(next) }),
    setLogic: (next: FilterLogic) => patch({ logic: next === 'Or' ? 'Or' : null }),
    setSort: (by: string, desc: boolean) =>
      patch({ sortBy: by === DEFAULT_SORT ? null : by, sortDesc: desc ? null : 'false' }),
    setColumns: (next: string[]) =>
      patch({ cols: next.join(',') === DEFAULT_COLUMNS.join(',') ? null : next.join(',') }),
  };
}
```

Defaults are written as *absent* params, so an unmodified list has a clean URL and a shared link carries only what was deliberately chosen.

- [ ] **Step 4: Point IssueList at the hook**

In `IssueList.tsx`, replace `const [conditions, setConditions] = useState<FilterCondition[]>([])` with `const { conditions, setConditions } = useListState();`. Leave everything else for later tasks.

- [ ] **Step 5: Run the tests**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Web
git commit -m "feat(web): list state lives in the URL, making a view shareable"
```

---

### Task 2: Combine conditions with OR

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Test: `src/JiraUnpacked.Web/src/features/issues/FilterBuilder.test.tsx`
- Test: `tests/JiraUnpacked.Tests/Integration/IssueFilterOrTests.cs`

**Interfaces:**
- Consumes: `useListState().logic / setLogic`.
- Produces: the SPA sends `{ filter: { logic, conditions } }` instead of `{ conditions }`.

The server needs **no change**: `IssueFilterRequest` already accepts `FilterGroup? Filter`, and `FilterGroup(FilterLogic Logic, List<FilterCondition>? Conditions, List<FilterGroup>? Groups)` already supports `And`/`Or`. The client types `FilterLogic` and `FilterGroup` already exist in `api/issues.ts`.

- [ ] **Step 1: Write the failing server test**

This proves the semantics the toggle promises, against seeded rows — a 200 would pass on a server that silently ANDed.

```csharp
[Fact]
public async Task Any_returns_the_union_where_All_returns_the_intersection()
{
    var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
    var ctx = await MetricsTestData.SeedProjectAsync(tenant);
    await IssueFilterOrTestData.SeedAsync(tenant, ctx.ProjectId);   // BUG/Backlog, BUG/Done, STORY/Backlog

    var or = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query", new
    {
        filter = new
        {
            logic = "Or",
            conditions = new object[]
            {
                new { field = "issueType", op = "Eq", value = "Bug" },
                new { field = "status",    op = "Eq", value = "Backlog" },
            },
        },
    });
    var and = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query", new
    {
        filter = new
        {
            logic = "And",
            conditions = new object[]
            {
                new { field = "issueType", op = "Eq", value = "Bug" },
                new { field = "status",    op = "Eq", value = "Backlog" },
            },
        },
    });

    Assert.Equal(3, (await or.Content.ReadFromJsonAsync<PagedResult<IssueListItem>>())!.TotalCount);
    Assert.Equal(1, (await and.Content.ReadFromJsonAsync<PagedResult<IssueListItem>>())!.TotalCount);
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueFilterOrTests" -v q`
Expected: PASS without server changes. **If it fails, stop** — the premise that OR already works is wrong and the plan needs revisiting.

- [ ] **Step 3: Write the failing UI test**

```tsx
it('sends a filter group whose logic follows the All/Any toggle', async () => {
  let sent: unknown = null;
  server.use(http.post('/api/projects/p1/issues/query', async ({ request }) => {
    sent = await request.json();
    return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
  }));
  const user = userEvent.setup();
  renderList();

  await user.click(await screen.findByRole('button', { name: /add condition/i }));
  await user.selectOptions(screen.getByLabelText(/match/i), 'Or');

  await waitFor(() => {
    expect(sent).toMatchObject({ filter: { logic: 'Or' } });
    expect(sent).not.toHaveProperty('conditions');   // never both: the server 400s on that
  });
});
```

- [ ] **Step 4: Add the toggle**

In `FilterBuilder.tsx`, above the condition rows:

```tsx
<Field label="Match" id="filter-logic" hint="All: every condition must hold. Any: at least one.">
  <Select id="filter-logic" value={logic} onChange={(e) => onLogicChange(e.target.value as FilterLogic)}>
    <option value="And">All conditions</option>
    <option value="Any">Any condition</option>
  </Select>
</Field>
```

Use `value="Or"` for the Any option — the label is prose, the value is the wire format.

In `IssueList.tsx`, build the request as a group:

```tsx
// `filter`, never `conditions`: the server rejects both together rather than merging, and the
// group is the only shape that can carry the logic.
const request = conditions.length === 0
  ? { page, pageSize }
  : { filter: { logic, conditions }, page, pageSize };
```

- [ ] **Step 5: Run both suites**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues`
Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueFilterOr" -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat(web): combine filter conditions with Any as well as All"
```

---

### Task 3: Choose which columns to show

**Files:**
- Create: `src/JiraUnpacked.Web/src/features/issues/columns.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.tsx`
- Create: `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`

**Interfaces:**
- Consumes: `useListState().columns / setColumns`.
- Produces: `ISSUE_COLUMNS: { id: string; header: string; render: (i: IssueListItem) => ReactNode }[]`, `ALWAYS_ON = 'jiraKey'`.

- [ ] **Step 1: Write the failing test**

```tsx
it('offers the four columns the list already fetches but never shows', async () => {
  renderList();
  await user.click(await screen.findByRole('button', { name: /columns/i }));

  // Priority, Team, Parent and Created are already in every response and thrown away.
  for (const name of [/priority/i, /team/i, /parent/i, /created/i]) {
    expect(screen.getByRole('checkbox', { name })).toBeInTheDocument();
  }
});

it('will not let Key be switched off', async () => {
  // Key is the row's identity and its link to the issue. A table of unlabelled rows is not a
  // smaller table, it is a broken one.
  renderList();
  await user.click(await screen.findByRole('button', { name: /columns/i }));

  expect(screen.getByRole('checkbox', { name: /key/i })).toBeDisabled();
  expect(screen.getByRole('checkbox', { name: /key/i })).toBeChecked();
});

it('hides a column from the table without changing the request', async () => {
  let requests = 0;
  server.use(http.post('/api/projects/p1/issues/query', () => {
    requests += 1;
    return HttpResponse.json({ items: [ITEM], page: 1, pageSize: 50, totalCount: 1 });
  }));
  renderList();
  await screen.findByRole('columnheader', { name: /sprint/i });
  const before = requests;

  await user.click(screen.getByRole('button', { name: /columns/i }));
  await user.click(screen.getByRole('checkbox', { name: /sprint/i }));

  expect(screen.queryByRole('columnheader', { name: /sprint/i })).not.toBeInTheDocument();
  expect(requests).toBe(before);   // presentation only — the server is not re-asked
});
```

- [ ] **Step 2: Run it and watch it fail**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues/ColumnChooser.test.tsx`
Expected: FAIL — no Columns button.

- [ ] **Step 3: Define the column catalog**

```ts
import type { IssueListItem } from '../../api/issues';

/** The one place a column's id, header and cell live together, so the chooser, the header row
 *  and the body cannot disagree about what a column is. */
export const ISSUE_COLUMNS = [
  { id: 'jiraKey',     header: 'Key',      render: (i: IssueListItem) => i.jiraKey },
  { id: 'summary',     header: 'Summary',  render: (i: IssueListItem) => i.summary ?? '—' },
  { id: 'status',      header: 'Status',   render: (i: IssueListItem) => i.status ?? '—' },
  { id: 'issueType',   header: 'Type',     render: (i: IssueListItem) => i.issueType ?? '—' },
  { id: 'priority',    header: 'Priority', render: (i: IssueListItem) => i.priority ?? '—' },
  { id: 'assignee',    header: 'Assignee', render: (i: IssueListItem) => i.assignee ?? '—' },
  { id: 'sprint',      header: 'Sprint',   render: (i: IssueListItem) => i.sprint ?? '—' },
  { id: 'team',        header: 'Team',     render: (i: IssueListItem) => i.team ?? '—' },
  { id: 'storyPoints', header: 'Points',   render: (i: IssueListItem) => i.storyPoints ?? '—' },
  { id: 'parent',      header: 'Parent',   render: (i: IssueListItem) => i.parentJiraIssueId ?? '—' },
  { id: 'createdAt',   header: 'Created',  render: (i: IssueListItem) => new Date(i.jiraCreatedAt).toLocaleDateString() },
  { id: 'updatedAt',   header: 'Updated',  render: (i: IssueListItem) => new Date(i.jiraUpdatedAt).toLocaleDateString() },
] as const;

/** Key is the row's identity and its link to the issue; it cannot be switched off. */
export const ALWAYS_ON = 'jiraKey';
```

Ids match the filter and sort vocabulary (`jiraKey`, `createdAt`, `updatedAt`) so one name means one field everywhere, including in the URL.

- [ ] **Step 4: Build the chooser and render from it**

`ColumnChooser.tsx` renders a `<details>`-based popover with a checkbox per `ISSUE_COLUMNS` entry, `disabled` and `checked` for `ALWAYS_ON`. In `IssueList.tsx`, derive the header row and each body row from `ISSUE_COLUMNS.filter((c) => columns.includes(c.id))` rather than hard-coded `<TH>`/`<TD>` markup, keeping the existing `<Link>` on the Key cell.

- [ ] **Step 5: Run the tests**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Web
git commit -m "feat(web): choose which issue columns to show"
```

---

### Task 4: Value list endpoint

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs`
- Create: `src/JiraUnpacked.Domain/Abstractions/IFilterValueService.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/FilterValuesTests.cs`

**Interfaces:**
- Produces: `GET /api/projects/{projectId:guid}/filter-values?field={name}` → `{ field, values: string[] }`; `IFilterValueService.GetAsync(Guid projectId, string field, CancellationToken)` → `FilterValuesResult(IReadOnlyList<string>? Values, string? Error)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Lists_only_the_values_that_occur_in_this_project()
{
    // The dictionary is site-wide; the useful question is "what can I actually pick HERE".
    // Offering a status no issue has is a dead end the reader cannot tell from a wrong guess.
    var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
    var mine  = await MetricsTestData.SeedProjectAsync(tenant);
    var other = await MetricsTestData.SeedProjectAsync(tenant);
    await FilterValuesTestData.SeedStatusAsync(tenant, mine.ProjectId, "Backlog");
    await FilterValuesTestData.SeedStatusAsync(tenant, other.ProjectId, "Only In The Other Project");

    var resp = await client.GetAsync($"/api/projects/{mine.ProjectId}/filter-values?field=status");
    var body = await resp.Content.ReadFromJsonAsync<FilterValuesResponse>();

    Assert.Equal(new[] { "Backlog" }, body!.Values);
}

[Fact]
public async Task Another_tenants_values_are_never_listed()
{
    var (clientA, tenantA) = await MetricsTestData.AuthedTenantAsync(_factory);
    var (_,       tenantB) = await MetricsTestData.AuthedTenantAsync(_factory);
    var a = await MetricsTestData.SeedProjectAsync(tenantA);
    var b = await MetricsTestData.SeedProjectAsync(tenantB);
    await FilterValuesTestData.SeedStatusAsync(tenantA, a.ProjectId, "Mine");
    await FilterValuesTestData.SeedStatusAsync(tenantB, b.ProjectId, "Theirs");

    var body = await (await clientA.GetAsync($"/api/projects/{a.ProjectId}/filter-values?field=status"))
        .Content.ReadFromJsonAsync<FilterValuesResponse>();

    Assert.DoesNotContain("Theirs", body!.Values);
}

[Theory]
[InlineData("summary")]      // Text
[InlineData("storyPoints")]  // Decimal
[InlineData("createdAt")]    // DateTime
[InlineData("parent")]       // ParentRef
[InlineData("nonsense")]     // not a field at all
public async Task A_field_with_no_value_list_is_a_400_naming_it(string field)
{
    // Not an empty array: "this field has no picker" and "this project has no values" are
    // different answers, and a client that cannot tell them apart shows the wrong empty state.
    var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
    var ctx = await MetricsTestData.SeedProjectAsync(tenant);

    var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/filter-values?field={field}");

    Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    Assert.Contains(field, await resp.Content.ReadAsStringAsync());
}

[Fact]
public async Task An_unknown_project_is_404_whatever_the_field()
{
    var (client, _) = await MetricsTestData.AuthedTenantAsync(_factory);

    var resp = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/filter-values?field=status");

    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~FilterValuesTests" -v q`
Expected: FAIL — 404 on the route.

- [ ] **Step 3: Implement the service**

```csharp
/// <summary>
/// The selectable values for one dictionary field, drawn from THIS project's issues.
///
/// Deliberately not the dictionary table wholesale: a Jira site's status list is site-wide, and
/// a value no issue here carries is a filter that returns nothing for a reason the reader cannot
/// see. Reads go through the tenant-scoped context, so another tenant's values are not merely
/// filtered out — they are unreachable.
/// </summary>
public sealed class FilterValueService : IFilterValueService
{
    private readonly AppDbContext _db;
    public FilterValueService(AppDbContext db) => _db = db;

    public async Task<FilterValuesResult> GetAsync(Guid projectId, string field, CancellationToken ct = default)
    {
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct))
            return new FilterValuesResult(null, null);   // null/null => 404

        var issues = _db.Issues.Where(i => i.JiraProjectId == projectId);

        IQueryable<string?>? names = field switch
        {
            "status"    => issues.Select(i => i.Status!.Name),
            "issueType" => issues.Select(i => i.IssueType!.Name),
            "priority"  => issues.Select(i => i.Priority!.Name),
            "assignee"  => issues.Select(i => i.Assignee!.DisplayName),
            "sprint"    => issues.Select(i => i.Sprint!.Name),
            "team"      => issues.Select(i => i.Team!.Name),
            _           => null,
        };

        if (names is null)
            return new FilterValuesResult(
                null, $"'{field}' has no value list. Only dictionary fields (status, issueType, "
                    + "priority, assignee, sprint, team) can be picked from.");

        var values = await names
            .Where(n => n != null)
            .Distinct()
            .OrderBy(n => n)
            .Take(1000)
            .ToListAsync(ct);

        return new FilterValuesResult(values!, null);
    }
}
```

The `switch` over literal field names **is** the closed catalog: no caller-supplied string reaches a query. `Take(1000)` bounds a pathological dictionary; a field with more distinct values than that is not usefully a dropdown.

Verify the navigation property names against `src/JiraUnpacked.Domain/Entities/Issue.cs` before writing the switch, and correct them if they differ.

- [ ] **Step 4: Add the endpoint**

```csharp
[HttpGet("api/projects/{projectId:guid}/filter-values")]
public async Task<IActionResult> FilterValues(Guid projectId, [FromQuery] string field, CancellationToken ct)
{
    var result = await _filterValues.GetAsync(projectId, field ?? "", ct);
    if (result.Error is not null) return BadRequest(new { error = result.Error, field });
    if (result.Values is null) return NotFound();
    return Ok(new { field, values = result.Values });
}
```

Register `IFilterValueService` → `FilterValueService` as scoped in `DependencyInjection.cs`, beside the other reporting services.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~FilterValuesTests" -v q`
Expected: PASS.

- [ ] **Step 6: Mutation check**

Change `issues.Select(...)` to read the dictionary table directly (`_db.Statuses.Select(s => s.Name)`). `Lists_only_the_values_that_occur_in_this_project` must fail and nothing else. Restore.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(filters): list the values a dictionary field actually takes in this project"
```

---

### Task 5: Pick a value instead of typing it

**Files:**
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts`
- Modify: `src/JiraUnpacked.Web/src/api/queryKeys.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx`
- Test: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`

**Interfaces:**
- Consumes: `GET /api/projects/{id}/filter-values?field=`, `isDictionaryField(catalog, field)` from `filterCatalogHelpers.ts`.
- Produces: `getFilterValues(projectId: string, field: string): Promise<{ field: string; values: string[] }>`; `queryKeys.filterValues(projectId, field)`.

- [ ] **Step 1: Write the failing tests**

```tsx
it('offers the project\'s values as a dropdown for a dictionary field', async () => {
  server.use(http.get('/api/projects/p1/filter-values', () =>
    HttpResponse.json({ field: 'status', values: ['Backlog', 'In Progress'] })));
  renderRow({ field: 'status', op: 'Eq' });

  const select = await screen.findByLabelText(/value for condition 1/i);
  expect(within(select).getByRole('option', { name: 'Backlog' })).toBeInTheDocument();
});

it('keeps a free text box for fields that have no value list', async () => {
  renderRow({ field: 'summary', op: 'Contains' });

  const input = await screen.findByLabelText(/value for condition 1/i);
  expect(input).toHaveAttribute('type', 'text');
});

it('falls back to the text box when the value list cannot be loaded', async () => {
  // A broken picker must not make an otherwise working filter unusable.
  server.use(http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })));
  renderRow({ field: 'status', op: 'Eq' });

  await waitFor(() => expect(screen.getByLabelText(/value for condition 1/i)).toHaveAttribute('type', 'text'));
});
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues/ConditionRow.test.tsx`
Expected: FAIL — the row always renders an input.

- [ ] **Step 3: Add the client call**

```ts
export const getFilterValues = (projectId: string, field: string) =>
  apiFetch<{ field: string; values: string[] }>(
    `/api/projects/${projectId}/filter-values?field=${encodeURIComponent(field)}`);
```

- [ ] **Step 4: Render a Select when there is a list**

In `ConditionRow.tsx`, fetch only for dictionary fields and only when the operator takes a value (`IsNull` takes none):

```tsx
const wantsList = isDictionaryField(catalog, condition.field) && condition.op !== 'IsNull';
const values = useQuery({
  queryKey: queryKeys.filterValues(projectId, condition.field),
  queryFn: () => getFilterValues(projectId, condition.field),
  enabled: wantsList,
  staleTime: 5 * 60 * 1000,   // a project's value set changes only on sync
});

// The list is an affordance, not a gate: if it is missing or failed, the text box still works.
const options = wantsList && values.data ? values.data.values : null;
```

Render a `<Select>` when `options` is non-null, the existing `<input type="text">` otherwise. Keep the same `id` and label either way so the field keeps its accessible name.

- [ ] **Step 5: Run the tests**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Web
git commit -m "feat(web): pick filter values from a list instead of typing them"
```

---

### Task 6: Sorting

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/SortFields.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/IssueSortTests.cs`

**Interfaces:**
- Produces: `IssueFilterRequest` gains `string? SortBy = null, bool SortDescending = true`; `SortFields.TryGet(string name, out LambdaExpression selector)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task An_unknown_sort_name_is_rejected_rather_than_quietly_ignored()
{
    // A silent fallback to the default would show plausible rows in the wrong order, which is
    // worse than an error: nothing on screen says the sort did not happen.
    var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
    var ctx = await MetricsTestData.SeedProjectAsync(tenant);

    var resp = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query",
        new { sortBy = "; DROP TABLE Issues--" });

    Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
}

[Fact]
public async Task Sorts_by_the_named_column_in_both_directions()
{
    var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
    var ctx = await MetricsTestData.SeedProjectAsync(tenant);
    await IssueSortTestData.SeedPointsAsync(tenant, ctx.ProjectId, 1, 5, 3);

    var asc = await Points(client, ctx.ProjectId, descending: false);
    var desc = await Points(client, ctx.ProjectId, descending: true);

    Assert.Equal(new decimal?[] { 1, 3, 5 }, asc);
    Assert.Equal(new decimal?[] { 5, 3, 1 }, desc);
}

[Fact]
public async Task Ties_are_broken_by_key_so_pages_do_not_overlap()
{
    // Without a total order Postgres may return a tied row on both page 1 and page 2, and another
    // on neither. The reader sees a duplicate and a silent omission.
    var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
    var ctx = await MetricsTestData.SeedProjectAsync(tenant);
    await IssueSortTestData.SeedSamePointsAsync(tenant, ctx.ProjectId, count: 10, points: 3);

    var p1 = await KeysOnPage(client, ctx.ProjectId, page: 1, pageSize: 5);
    var p2 = await KeysOnPage(client, ctx.ProjectId, page: 2, pageSize: 5);

    Assert.Empty(p1.Intersect(p2));
    Assert.Equal(10, p1.Concat(p2).Distinct().Count());
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueSortTests" -v q`
Expected: FAIL — `sortBy` is ignored.

- [ ] **Step 3: Add the closed sort catalog**

```csharp
/// <summary>
/// The columns a caller may sort by, and the selector for each.
///
/// A closed catalog for the same reason FilterFields is one: the name arrives from a URL, and a
/// name that reached SQL as a string would be an injection point. Nothing here is built from
/// caller input — the name only ever selects a pre-compiled expression.
///
/// The names match FilterFields exactly, so one vocabulary covers filtering and sorting. Two
/// names for one field would be a bug waiting to happen in the URL, where both appear together.
/// </summary>
internal static class SortFields
{
    internal const string Default = "updatedAt";

    private static readonly Dictionary<string, Expression<Func<Issue, object?>>> All = new(StringComparer.Ordinal)
    {
        ["jiraKey"]     = i => i.JiraKey,
        ["summary"]     = i => i.Summary,
        ["status"]      = i => i.Status!.Name,
        ["issueType"]   = i => i.IssueType!.Name,
        ["priority"]    = i => i.Priority!.Name,
        ["assignee"]    = i => i.Assignee!.DisplayName,
        ["sprint"]      = i => i.Sprint!.Name,
        ["team"]        = i => i.Team!.Name,
        ["storyPoints"] = i => i.StoryPoints,
        ["createdAt"]   = i => i.JiraCreatedAt,
        ["updatedAt"]   = i => i.JiraUpdatedAt,
    };

    internal static bool TryGet(string? name, out Expression<Func<Issue, object?>> selector) =>
        All.TryGetValue(name ?? Default, out selector!);

    internal static IReadOnlyCollection<string> Names => All.Keys;
}
```

- [ ] **Step 4: Apply it in the query service**

Replace the hard-coded `.OrderByDescending(i => i.JiraUpdatedAt).ThenBy(i => i.JiraKey)` in `QueryAsync`:

```csharp
if (!SortFields.TryGet(request.SortBy, out var sortSelector))
    return IssueQueryResult.BadRequest(
        $"'{request.SortBy}' is not a sortable column. Sortable: {string.Join(", ", SortFields.Names)}.");

// jiraKey last, always. Sorting by a column with duplicates is not a total order, and without
// one Postgres may place a tied row on two pages and another on none.
q = request.SortDescending
    ? q.OrderByDescending(sortSelector).ThenBy(i => i.JiraKey)
    : q.OrderBy(sortSelector).ThenBy(i => i.JiraKey);
```

Match `IssueQueryResult`'s existing error shape — read how the compiler's validation error becomes a 400 in `IssuesController.Query` and use the same channel rather than inventing a second one.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~IssueSortTests" -v q`
Expected: PASS.

- [ ] **Step 6: Mutation check**

Delete `.ThenBy(i => i.JiraKey)`. `Ties_are_broken_by_key_so_pages_do_not_overlap` must fail and nothing else. Restore.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat(issues): sort by any catalogued column, with a stable tiebreaker"
```

---

### Task 7: Sortable column headers

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts`
- Test: `src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx`

**Interfaces:**
- Consumes: `useListState().sortBy / sortDescending / setSort`, `ISSUE_COLUMNS`.
- Produces: the query request carries `sortBy` and `sortDescending`.

- [ ] **Step 1: Write the failing tests**

```tsx
it('sorts by a column when its header is clicked, and flips on a second click', async () => {
  const sent: unknown[] = [];
  server.use(http.post('/api/projects/p1/issues/query', async ({ request }) => {
    sent.push(await request.json());
    return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
  }));
  renderList();

  await user.click(await screen.findByRole('button', { name: /sort by points/i }));
  await waitFor(() => expect(sent.at(-1)).toMatchObject({ sortBy: 'storyPoints', sortDescending: true }));

  await user.click(screen.getByRole('button', { name: /sort by points/i }));
  await waitFor(() => expect(sent.at(-1)).toMatchObject({ sortBy: 'storyPoints', sortDescending: false }));
});

it('tells assistive tech which column is sorted and which way', async () => {
  renderList('/projects/p1/issues?sortBy=storyPoints&sortDesc=false');

  const header = await screen.findByRole('columnheader', { name: /points/i });
  expect(header).toHaveAttribute('aria-sort', 'ascending');
});
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues/IssueList.test.tsx`
Expected: FAIL — headers are static text.

- [ ] **Step 3: Make the headers buttons**

```tsx
<TH aria-sort={sortBy === col.id ? (sortDescending ? 'descending' : 'ascending') : 'none'}>
  <button
    type="button"
    onClick={() => setSort(col.id, sortBy === col.id ? !sortDescending : true)}
    className="inline-flex items-center gap-1"
  >
    {col.header}
    {/* Decorative: aria-sort on the header already carries this for assistive tech. */}
    {sortBy === col.id && <span aria-hidden="true">{sortDescending ? '↓' : '↑'}</span>}
  </button>
</TH>
```

Give the button an accessible name of `Sort by {col.header}` via `aria-label` so the test's `/sort by points/i` matches and the control reads as an action rather than a noun. Add `sortBy` and `sortDescending` to the request built in `IssueList`.

- [ ] **Step 4: Run the tests**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/JiraUnpacked.Web
git commit -m "feat(web): sort the issue list by clicking a column header"
```

---

### Task 8: CSV export

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/CsvWriter.cs`
- Create: `tests/JiraUnpacked.Tests/Reporting/CsvWriterTests.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/IssueExportTests.cs`

**Interfaces:**
- Consumes: the same filter compilation and `SortFields` as Task 6.
- Produces: `GET /api/projects/{projectId:guid}/issues/export.csv`; `CsvWriter.Escape(string? value)`; `IIssueQueryService.StreamAsync(Guid projectId, IssueFilterRequest, CancellationToken)` → `IAsyncEnumerable<IssueListItem>`.

- [ ] **Step 1: Write the failing CsvWriter tests**

```csharp
[Theory]
[InlineData("=1+1",              "\"'=1+1\"")]
[InlineData("+1",                "\"'+1\"")]
[InlineData("-1",                "\"'-1\"")]
[InlineData("@SUM(A1)",          "\"'@SUM(A1)\"")]
[InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
public void A_leading_formula_character_is_neutralised(string input, string expected)
{
    // Excel and Sheets execute a cell beginning with these. A Jira summary is text a stranger
    // can write, so an export of someone else's issues would run their formula on the machine of
    // whoever opens it. Quoting alone does not stop this — the prefix is a separate defence.
    Assert.Equal(expected, CsvWriter.Escape(input));
}

[Theory]
[InlineData("plain",            "plain")]
[InlineData("has,comma",        "\"has,comma\"")]
[InlineData("has\"quote",       "\"has\"\"quote\"")]
[InlineData("has\nnewline",     "\"has\nnewline\"")]
[InlineData(null,               "")]
public void Ordinary_values_follow_RFC_4180(string? input, string expected)
    => Assert.Equal(expected, CsvWriter.Escape(input));
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~CsvWriterTests" -v q`
Expected: FAIL — `CsvWriter` does not exist.

- [ ] **Step 3: Implement CsvWriter**

```csharp
/// <summary>RFC 4180 quoting, plus neutralisation of values a spreadsheet would execute.</summary>
public static class CsvWriter
{
    private static readonly char[] Dangerous = { '=', '+', '-', '@', '\t', '\r' };

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // The prefix goes on BEFORE quoting, so it lands inside the quotes where the spreadsheet
        // reads it as the first character of the cell's text.
        var v = Dangerous.Contains(value[0]) ? "'" + value : value;

        return v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r') || v != value
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}
```

- [ ] **Step 4: Write the failing export tests**

```csharp
[Fact]
public async Task Exports_exactly_the_rows_the_list_would_show()
{
    var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
    var ctx = await MetricsTestData.SeedProjectAsync(tenant);
    await IssueExportTestData.SeedAsync(tenant, ctx.ProjectId, bugs: 3, stories: 2);

    var listed = (await (await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query",
        new { filter = new { logic = "And", conditions = new[] { new { field = "issueType", op = "Eq", value = "Bug" } } } }))
        .Content.ReadFromJsonAsync<PagedResult<IssueListItem>>())!.TotalCount;

    var csv = await client.GetStringAsync(
        $"/api/projects/{ctx.ProjectId}/issues/export.csv?filter={Uri.EscapeDataString("[{\"field\":\"issueType\",\"op\":\"Eq\",\"value\":\"Bug\"}]")}");

    Assert.Equal(listed + 1, csv.TrimEnd('\n').Split('\n').Length);   // + header
}

[Fact]
public async Task A_summary_that_looks_like_a_formula_is_neutralised_end_to_end()
{
    var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
    var ctx = await MetricsTestData.SeedProjectAsync(tenant);
    await IssueExportTestData.SeedSummaryAsync(tenant, ctx.ProjectId, "=1+1");

    var csv = await client.GetStringAsync($"/api/projects/{ctx.ProjectId}/issues/export.csv");

    Assert.Contains("\"'=1+1\"", csv);
    Assert.DoesNotContain(",=1+1", csv);
}

[Fact]
public async Task Refuses_a_set_too_large_to_stream_and_says_how_large()
{
    var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
    var ctx = await MetricsTestData.SeedProjectAsync(tenant);
    await IssueExportTestData.SeedManyAsync(tenant, ctx.ProjectId, count: 50_001);

    var resp = await client.GetAsync($"/api/projects/{ctx.ProjectId}/issues/export.csv");

    Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    Assert.Contains("50001", await resp.Content.ReadAsStringAsync());
}
```

- [ ] **Step 5: Implement the streaming endpoint**

`StreamAsync` mirrors `QueryAsync`'s filter and sort but ends in `.AsAsyncEnumerable()` with **no** `ToListAsync` and no paging. The controller counts first, refuses over 50,000, then writes the header row and each record straight to `Response.Body`:

```csharp
[HttpGet("api/projects/{projectId:guid}/issues/export.csv")]
public async Task<IActionResult> Export(Guid projectId, [FromQuery] string? filter,
    [FromQuery] string? sortBy, [FromQuery] bool sortDescending = true,
    [FromQuery] string? cols = null, CancellationToken ct = default)
{
    // ... resolve project (404), parse filter (400), count (400 over the cap) ...

    Response.ContentType = "text/csv; charset=utf-8";
    Response.Headers.ContentDisposition =
        $"attachment; filename=\"{projectKey}-issues-{DateTime.UtcNow:yyyyMMdd}.csv\"";

    await using var writer = new StreamWriter(Response.Body);
    await writer.WriteLineAsync(string.Join(",", selected.Select(c => CsvWriter.Escape(c.Header))));

    // Written as they arrive. Materialising the set to build a string is the mistake removed from
    // derivation this week, and it is worse here: the response is held open for the download.
    await foreach (var item in _issues.StreamAsync(projectId, request, ct))
        await writer.WriteLineAsync(string.Join(",", selected.Select(c => CsvWriter.Escape(c.Value(item)))));

    return new EmptyResult();
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~CsvWriterTests|FullyQualifiedName~IssueExportTests" -v q`
Expected: PASS.

- [ ] **Step 7: Mutation check**

Remove the `'` prefix from `CsvWriter.Escape`. `A_leading_formula_character_is_neutralised` and the end-to-end formula test must fail, and nothing else. Restore.

- [ ] **Step 8: Commit**

```bash
git add src tests
git commit -m "feat(issues): stream the filtered list as CSV, with formulas neutralised"
```

---

### Task 9: Export button

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts`
- Test: `src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx`

**Interfaces:**
- Consumes: the export route from Task 8, `useListState()`.
- Produces: `exportUrl(projectId, { conditions, logic, sortBy, sortDescending, columns }): string`.

- [ ] **Step 1: Write the failing test**

```tsx
it('offers a download carrying the filter, sort and columns on screen', async () => {
  renderList('/projects/p1/issues?sortBy=storyPoints&cols=jiraKey,summary');

  const link = await screen.findByRole('link', { name: /export csv/i });
  const href = link.getAttribute('href')!;

  expect(href).toContain('/api/projects/p1/issues/export.csv');
  expect(href).toContain('sortBy=storyPoints');
  expect(href).toContain('cols=jiraKey%2Csummary');
});
```

- [ ] **Step 2: Run it and watch it fail**

Run: `cd src/JiraUnpacked.Web && npx vitest run src/features/issues/IssueList.test.tsx`
Expected: FAIL — no export control.

- [ ] **Step 3: Add the link**

An anchor, not a fetch: the browser's own download handling streams to disk, where `apiFetch` would buffer the whole file in memory — the same mistake the server side is avoiding.

```tsx
<a href={exportUrl(id, { conditions, logic, sortBy, sortDescending, columns })} download>
  Export CSV
</a>
```

- [ ] **Step 4: Run the tests**

Run: `cd src/JiraUnpacked.Web && npm run -s test`
Expected: PASS.

- [ ] **Step 5: Run everything**

```bash
dotnet test --nologo
cd src/JiraUnpacked.Web && npm run -s lint && npx tsc --noEmit && npm run -s test
git diff --stat develop -- tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs   # must be empty
```

- [ ] **Step 6: Commit**

```bash
git add src/JiraUnpacked.Web
git commit -m "feat(web): export the filtered issue list as CSV"
```

---

## Self-Review

**Spec coverage.** Value picker → T4 (endpoint), T5 (UI). OR → T2. Column chooser → T3. Sorting → T6 (backend), T7 (UI). CSV → T8 (backend), T9 (UI). URL as state → T1. Export cap, formula escaping and streaming → T8. Sort-name vocabulary → T6, matching T3's column ids and the filter catalog.

**Type consistency.** One vocabulary throughout: `jiraKey`, `summary`, `status`, `issueType`, `priority`, `assignee`, `sprint`, `team`, `storyPoints`, `createdAt`, `updatedAt` — used by `ISSUE_COLUMNS.id` (T3), `SortFields` keys (T6), the filter catalog, and the `cols`/`sortBy` URL params. `parent` is a column id and a filter field but **not** sortable, which is deliberate: it holds a Jira id, and sorting by it orders by an opaque string nobody reads.

**Known gaps, both deliberate.** The export's `cols` parameter means the controller needs a server-side column catalog mirroring `ISSUE_COLUMNS`; T8 introduces it as `selected` and the two must be kept in step — a divergence shows up as a missing CSV column, not a crash. And T4's navigation-property names (`i.Status!.Name`, `i.Assignee!.DisplayName`) are written from the entity shapes as read; the task says to verify them against `Issue.cs` before writing the switch, because a wrong one fails at query translation rather than compile time.
