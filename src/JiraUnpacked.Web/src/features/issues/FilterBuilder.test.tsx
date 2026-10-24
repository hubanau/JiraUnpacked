import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { delay, http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import IssueList from './IssueList';
import { FilterBuilder } from './FilterBuilder';
import { ConditionRow } from './ConditionRow';
import type { FilterCondition } from '../../api/issues';
import { FILTER_CATALOG as CATALOG } from '../../test/filterCatalogFixture';
import { emptyRoot, type EditorGroup } from './filterTree';
import { noSavedViews } from '../../test/viewsFixture';
import { expandFilter } from '../../test/expandFilter';

/**
 * The conditions the SPA actually sent, whichever envelope carried them.
 *
 * The builder moved from a flat `conditions` list to a `filter` group when All/Any arrived.
 * These tests are about the conditions themselves -- which operator, which value shape -- so
 * they read through this rather than hard-coding the wrapper and having to change again next
 * time it moves.
 */
const sentConditions = (body: Record<string, unknown>): unknown =>
  (body.filter as { conditions?: unknown[] } | undefined)?.conditions ?? body.conditions;

function mockFilterCatalog() {
  server.use(
    http.get('/api/filter-catalog', () => HttpResponse.json(CATALOG)),
    noSavedViews,
    // The default row's field is 'status', a dictionary field, so mounting a condition at all
    // (most tests below do, via addCondition or a filter already in the URL) fires this fetch
    // whether or not the test cares about the picker. Failing it keeps the value control exactly
    // the text input it already was when this request went genuinely unhandled (MSW is
    // configured to error on one) — just without the console noise. The 'value picker' describe
    // below registers its own specific response after this one for the tests that ARE about it,
    // which — being added later — takes precedence.
    http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })),
  );
}

function renderIssueList() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/projects/p1/issues']}>
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/**
 * Registers a handler that always returns an empty page, and records every
 * request body it receives (in arrival order) so a test can inspect the
 * final `conditions` array the builder produced — the whole point of
 * mounting IssueList instead of the builder in isolation. Also registers
 * the filter-catalog handler, since IssueList always mounts FilterBuilder.
 */
function mockIssuesQuery(): { bodies: Record<string, unknown>[] } {
  mockFilterCatalog();
  const bodies: Record<string, unknown>[] = [];
  server.use(
    http.post('/api/projects/p1/issues/query', async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      bodies.push(body);
      return HttpResponse.json({ items: [], page: body.page ?? 1, pageSize: 50, totalCount: 0 });
    }),
  );
  return { bodies };
}

async function addCondition(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: /add condition/i }));
}

describe('FilterBuilder', () => {
  it('shows a spinner while the filter catalog is loading', async () => {
    // Rendered directly (no IssueList/router wrapper needed) with a request
    // that never resolves, so the pending phase is observable rather than
    // racing past it — the same technique the maxConditions/maxInValues
    // tests below use to render FilterBuilder/ConditionRow standalone.
    server.use(
      http.get('/api/filter-catalog', async () => {
        await delay('infinite');
        return HttpResponse.json(CATALOG);
      }),
    );
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <FilterBuilder projectId="p1" tree={emptyRoot()} onChange={() => {}} />
      </QueryClientProvider>,
    );

    expect(await screen.findByRole('status', { name: /loading filter catalog/i })).toBeInTheDocument();
  });

  it('shows the server error when the filter catalog fails to load', async () => {
    server.use(
      http.get('/api/filter-catalog', () =>
        HttpResponse.json(
          { title: 'Could not load the filter catalog right now.' },
          { status: 500 },
        )),
    );
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <FilterBuilder projectId="p1" tree={emptyRoot()} onChange={() => {}} />
      </QueryClientProvider>,
    );

    expect(await screen.findByRole('alert')).toHaveTextContent(
      /could not load the filter catalog right now/i,
    );
  });

  it('shows a collapsed summary and an Add condition control when there is no filter', async () => {
    mockIssuesQuery();
    renderIssueList();

    expect(await screen.findByText(/no filter — showing all issues/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /add condition/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /clear all/i })).not.toBeInTheDocument();
  });

  it('seeds a fresh, enabled root when adding a condition from empty state, even if the old root was disabled', async () => {
    // A root can be left `disabled: true` (e.g. every part was switched off and then removed) and
    // still be the empty tree the "No filter" state renders for. "Add condition" from there must
    // not spread that disabled root onto the new row — the row would be born already dead.
    mockFilterCatalog();
    const user = userEvent.setup({ delay: null });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    let latest: EditorGroup | undefined;
    const disabledEmptyRoot: EditorGroup = { ...emptyRoot(), disabled: true };
    render(
      <QueryClientProvider client={queryClient}>
        <FilterBuilder
          projectId="p1"
          tree={disabledEmptyRoot}
          onChange={(next) => { latest = next; }}
        />
      </QueryClientProvider>,
    );

    await user.click(await screen.findByRole('button', { name: /add condition/i }));

    expect(latest?.disabled).toBeUndefined();
    expect(latest?.conditions).toHaveLength(1);
    expect(latest?.conditions[0].disabled).toBeUndefined();
  });

  it('sends neither value nor values for IsNull', async () => {
    const { bodies } = mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'assignee');
    await user.selectOptions(screen.getByLabelText(/operator for condition 1/i), 'IsNull');

    await waitFor(() => {
      expect(sentConditions(bodies[bodies.length - 1])).toEqual([{ field: 'assignee', op: 'IsNull' }]);
    });
    const lastCondition = (sentConditions(bodies[bodies.length - 1]) as Record<string, unknown>[])[0];
    expect('value' in lastCondition).toBe(false);
    expect('values' in lastCondition).toBe(false);
  });

  it('sends values (not value) for In', async () => {
    const { bodies } = mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    await user.selectOptions(screen.getByLabelText(/operator for condition 1/i), 'In');
    const valueInput = screen.getByLabelText(/value for condition 1/i);
    await user.type(valueInput, 'Done');
    await user.click(screen.getByRole('button', { name: /add value/i }));
    await user.type(valueInput, 'In Progress');
    await user.click(screen.getByRole('button', { name: /add value/i }));

    await waitFor(() => {
      expect(sentConditions(bodies[bodies.length - 1])).toEqual([
        { field: 'status', op: 'In', values: ['Done', 'In Progress'] },
      ]);
    });
    const lastCondition = (sentConditions(bodies[bodies.length - 1]) as Record<string, unknown>[])[0];
    expect('value' in lastCondition).toBe(false);
  });

  it('sends value (not values) for a single-value operator', async () => {
    const { bodies } = mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    // summary's operators are Contains/StartsWith/IsNull, so switching the
    // field to it already resets the row's operator to Contains.
    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'summary');
    const valueInput = screen.getByLabelText(/value for condition 1/i);
    await user.type(valueInput, 'login');

    await waitFor(() => {
      expect(sentConditions(bodies[bodies.length - 1])).toEqual([
        { field: 'summary', op: 'Contains', value: 'login' },
      ]);
    });
    const lastCondition = (sentConditions(bodies[bodies.length - 1]) as Record<string, unknown>[])[0];
    expect('values' in lastCondition).toBe(false);
  });

  it('sends a number for a Decimal field', async () => {
    const { bodies } = mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'storyPoints');
    await user.selectOptions(screen.getByLabelText(/operator for condition 1/i), 'Gte');
    const valueInput = screen.getByLabelText(/value for condition 1/i);
    await user.type(valueInput, '3');

    await waitFor(() => {
      expect(sentConditions(bodies[bodies.length - 1])).toEqual([
        { field: 'storyPoints', op: 'Gte', value: 3 },
      ]);
    });
    const lastCondition = (sentConditions(bodies[bodies.length - 1]) as Record<string, unknown>[])[0];
    expect(typeof lastCondition.value).toBe('number');
  });

  it('resets an operator the new field does not allow', async () => {
    mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'summary');
    // createdAt allows only Lt/Lte/Gt/Gte, so the operator select must not
    // still read Contains (summary's auto-reset operator) after this.
    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'createdAt');

    expect(screen.getByLabelText(/operator for condition 1/i)).toHaveValue('Lt');
  });

  it('offers only the operators the chosen field allows', async () => {
    mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'jiraKey');

    const ops = within(screen.getByLabelText(/operator for condition 1/i))
      .getAllByRole('option')
      .map((o) => o.textContent);
    expect(ops).toEqual(['is', 'is any of', 'is none of', 'contains', 'starts with']);
  });

  it('blocks a 21st condition', async () => {
    // The guard is a pure function of conditions.length, so it is asserted
    // by rendering FilterBuilder directly with CATALOG.maxConditions rows
    // already present — not by driving that many sequential "Add condition"
    // clicks (and that many MSW round trips) through a mounted IssueList.
    // That prior version was the slowest test in the suite by a wide
    // margin, purely from render/network overhead unrelated to the guard
    // itself, and a large timeout on it would have hidden a real
    // performance regression rather than caught one.
    mockFilterCatalog();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <FilterBuilder
          projectId="p1"
          onChange={() => {}}
          tree={{
            id: 'root', logic: 'And', groups: [],
            conditions: Array.from({ length: CATALOG.maxConditions }, (_, i) => ({
              id: `c${i}`, field: 'status', op: 'Eq' as const, value: '',
            })),
          }}
        />
      </QueryClientProvider>,
    );

    expect(await screen.findByRole('button', { name: /add condition/i })).toBeDisabled();
    expect(screen.getByText(/at most 20 conditions/i)).toBeInTheDocument();
  });

  it('blocks a 101st In value', () => {
    // Same rationale as "blocks a 21st condition" above: render ConditionRow
    // directly with CATALOG.maxInValues values already present, rather than
    // driving that many sequential "Add value" interactions through a
    // mounted IssueList (which made this the suite's second-slowest test).
    // ConditionRow takes the catalog as a plain prop, so this needs no
    // filter-catalog fetch — but 'status' is a dictionary field, so ConditionRow
    // still fires its own filter-values fetch regardless of op; failed here to
    // keep MSW quiet, same as mockFilterCatalog does for every other test.
    server.use(http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })));
    const condition: FilterCondition = {
      field: 'status',
      op: 'In',
      values: Array.from({ length: CATALOG.maxInValues }, (_, i) => `v${i}`),
    };
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ConditionRow projectId="p1" condition={condition} catalog={CATALOG} onChange={() => {}} label="1" />
      </QueryClientProvider>,
    );

    expect(screen.getByRole('button', { name: /add value/i })).toBeDisabled();
    expect(screen.getByText(/at most 100 values/i)).toBeInTheDocument();
  });

  it('still shows a server 400 when the client thought the filter was fine', async () => {
    mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    server.use(
      http.post('/api/projects/p1/issues/query', () =>
        HttpResponse.json(
          { title: "Operator 'Contains' is not allowed for field 'createdAt'." },
          { status: 400 },
        )),
    );

    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'summary');
    const valueInput = screen.getByLabelText(/value for condition 1/i);
    await user.type(valueInput, 'login');

    expect(await screen.findByRole('alert')).toHaveTextContent(/not allowed for field 'createdAt'/i);
  });

  it('no longer warns that "is not" matches empty fields, because it does not', async () => {
    mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    await user.selectOptions(screen.getByLabelText(/operator for condition 1/i), 'Neq');

    expect(screen.queryByText(/also matches issues where the field is empty/i)).not.toBeInTheDocument();
  });

  it('Clear all returns to the collapsed no-filter state', async () => {
    mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);
    expect(screen.getByRole('button', { name: /clear all/i })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /clear all/i }));

    expect(await screen.findByText(/no filter — showing all issues/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /clear all/i })).not.toBeInTheDocument();
  });

  it('removes a condition row and excludes it from the next request', async () => {
    const { bodies } = mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);
    await user.type(screen.getByLabelText(/value for condition 1/i), 'Done');
    await addCondition(user);
    await user.selectOptions(screen.getByLabelText(/field for condition 2/i), 'summary');
    await user.type(screen.getByLabelText(/value for condition 2/i), 'login');

    await waitFor(() => {
      expect(sentConditions(bodies[bodies.length - 1])).toEqual([
        { field: 'status', op: 'Eq', value: 'Done' },
        { field: 'summary', op: 'Contains', value: 'login' },
      ]);
    });

    // Remove the first (status/Done) row.
    await user.click(screen.getByRole('button', { name: 'Remove condition 1' }));

    // Only the summary row remains, now shifted up into row 1's position.
    expect(screen.getByLabelText(/field for condition 1/i)).toHaveValue('summary');
    expect(screen.queryByLabelText(/field for condition 2/i)).not.toBeInTheDocument();

    await waitFor(() => {
      expect(sentConditions(bodies[bodies.length - 1])).toEqual([
        { field: 'summary', op: 'Contains', value: 'login' },
      ]);
    });
  });

  it('emits a DateTime value as the ISO date string the date input produces', async () => {
    const { bodies } = mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    // createdAt's operators are Lt/Lte/Gt/Gte, so switching to it already
    // resets the row's operator away from status's default Eq (see
    // "resets an operator the new field does not allow" above).
    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'createdAt');
    // <input type="date"> does not accept keystroke-by-keystroke typing
    // reliably in jsdom; firing the change directly is how a native date
    // picker commits a full value in one step.
    fireEvent.change(screen.getByLabelText(/value for condition 1/i), { target: { value: '2026-01-15' } });

    await waitFor(() => {
      expect(sentConditions(bodies[bodies.length - 1])).toEqual([
        { field: 'createdAt', op: 'Lt', value: '2026-01-15' },
      ]);
    });
    const lastCondition = (sentConditions(bodies[bodies.length - 1]) as Record<string, unknown>[])[0];
    expect(typeof lastCondition.value).toBe('string');
  });

  it('does not bleed a half-typed In draft into a different condition after removing a row', async () => {
    // Regression test for FilterBuilder keying rows by array index while
    // ConditionRow's `inDraft` (the In-editor's uncommitted add-value text)
    // is local useState: removing a row shifts index identity onto a
    // different condition, but the surviving component instance's draft
    // does not reset on a prop change on its own — see ConditionRow.tsx.
    //
    // Three conditions, with the middle one and the later one both using
    // In. Typing into the middle row's In-editor and then removing that
    // row is what demonstrates the leak: the DOM/component instance at
    // that list position survives the removal (same key), so it goes on
    // to render whatever condition shifts up into that slot — here, the
    // later row's data — and, without a reset, would show the stale draft
    // alongside it.
    mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();

    await addCondition(user); // condition 1: status/Eq (untouched control)
    await addCondition(user); // condition 2: middle row -> assignee/In
    await addCondition(user); // condition 3: later row -> priority/In

    await user.selectOptions(screen.getByLabelText(/field for condition 2/i), 'assignee');
    await user.selectOptions(screen.getByLabelText(/operator for condition 2/i), 'In');
    await user.selectOptions(screen.getByLabelText(/field for condition 3/i), 'priority');
    await user.selectOptions(screen.getByLabelText(/operator for condition 3/i), 'In');

    // Type into the middle row's In-editor but never commit it with
    // "Add value" — this is the half-typed draft that must not survive
    // onto a different condition.
    await user.type(screen.getByLabelText(/value for condition 2/i), 'partial-draft');

    // Remove the middle row. Condition 3 (priority/In) shifts up into
    // condition 2's position — the same list slot that held the draft.
    await user.click(screen.getByRole('button', { name: 'Remove condition 2' }));

    // Condition 2 is now what used to be condition 3.
    await waitFor(() => {
      expect(screen.getByLabelText(/field for condition 2/i)).toHaveValue('priority');
    });
    // Its In-editor must be blank, not showing the draft typed against the
    // now-removed assignee condition.
    expect(screen.getByLabelText(/value for condition 2/i)).toHaveValue('');
  });
});

/** Renders IssueList at an arbitrary URL, so a test can start from a filter already in place. */
function renderIssueListAt(url: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const ONE_CONDITION = encodeURIComponent('[{"field":"status","op":"Eq","value":"Backlog"}]');

describe('FilterBuilder logic toggle', () => {
  it('sends a filter group, never a bare conditions list', async () => {
    // The server returns 400 when both are present and cannot carry logic on the flat shape, so
    // the group is the only form that can express Any.
    mockFilterCatalog();
    let sent: unknown = null;
    server.use(http.post('/api/projects/p1/issues/query', async ({ request }) => {
      sent = await request.json();
      return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
    }));

    renderIssueListAt(`/projects/p1/issues?filter=${ONE_CONDITION}`);

    await waitFor(() => {
      expect(sent).toMatchObject({
        filter: { logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Backlog' }] },
      });
    });
    expect(sent).not.toHaveProperty('conditions');
  });

  it('carries Any through to the request when the toggle is switched', async () => {
    mockFilterCatalog();
    const sent: unknown[] = [];
    server.use(http.post('/api/projects/p1/issues/query', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
    }));
    const user = userEvent.setup();

    renderIssueListAt(`/projects/p1/issues?filter=${ONE_CONDITION}`);
    await expandFilter();
    await screen.findByLabelText(/match/i);

    await user.selectOptions(screen.getByLabelText(/match/i), 'Or');

    await waitFor(() => expect(sent.at(-1)).toMatchObject({ filter: { logic: 'Or' } }));
  });

  it('offers no toggle while there is no filter at all', async () => {
    // With no conditions there is nothing for All/Any to describe. It appears from the first
    // condition onward rather than the second, so the control does not pop in and out while
    // the filter is being built.
    mockFilterCatalog();
    server.use(http.post('/api/projects/p1/issues/query', () =>
      HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })));

    renderIssueListAt('/projects/p1/issues');

    await screen.findByRole('button', { name: /add condition/i });
    expect(screen.queryByLabelText(/match/i)).not.toBeInTheDocument();
  });

  it('reads a legacy flat link with its old logic, and writes a tree without it on the next edit', async () => {
    mockFilterCatalog();
    // The link's one condition is field=status, so mounting it fires a filter-values fetch —
    // registered here with the real (counts) response envelope, overriding mockFilterCatalog's
    // default failure for this test specifically. `values` is left empty: ConditionRow's picker
    // still renders each entry as `<option value={v}>{v}</option>` with the whole element, not
    // `.name` — a populated response here crashes the render with "Objects are not valid as a
    // React child". That is a real bug (the actual backend already returns this counts shape;
    // see FilterValueService), not a test artifact, but fixing ConditionRow itself is the
    // value-picker rework's job, not this one — flagged in the fix report instead.
    server.use(http.get('/api/projects/p1/filter-values', () =>
      HttpResponse.json({ field: 'status', values: [], emptyCount: 0 })));
    const sent: Record<string, unknown>[] = [];
    server.use(http.post('/api/projects/p1/issues/query', async ({ request }) => {
      sent.push((await request.json()) as Record<string, unknown>);
      return HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 });
    }));
    let search = '';
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={[`/projects/p1/issues?filter=${ONE_CONDITION}&logic=Or`]}>
          <Routes>
            <Route
              path="/projects/:id/issues"
              element={<><IssueList /><SearchProbe onSearch={(s) => { search = s; }} /></>}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await waitFor(() => expect(sent.at(-1)).toMatchObject({ filter: { logic: 'Or' } }));

    await expandFilter();
    await user.selectOptions(await screen.findByLabelText(/match/i), 'And');

    await waitFor(() => {
      const params = new URLSearchParams(search);
      expect(params.get('logic')).toBeNull();
      expect(JSON.parse(params.get('filter')!)).toEqual({
        logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Backlog' }],
      });
    });
  });

  it('leaves a field the catalog no longer has out of the request', async () => {
    // The issues query is gated on the catalog having settled (see IssueList), so this asserts
    // over every body the query handler ever received, not just the last one: before that gate
    // existed, the very FIRST request — sent while the catalog was still loading and knownFields
    // was undefined — carried 'retired' unfiltered, and only a later refetch dropped it.
    const { bodies } = mockIssuesQuery();
    const tree = {
      logic: 'And',
      conditions: [
        { field: 'retired', op: 'Eq', value: 'x' },
        { field: 'summary', op: 'Contains', value: 'login' },
      ],
    };

    renderIssueListAt(`/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(tree))}`);

    await waitFor(() => expect(bodies.length).toBeGreaterThan(0));
    for (const body of bodies) {
      expect(JSON.stringify(body.filter)).not.toContain('retired');
    }
    expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'login' }],
    });
  });
});

describe('value picker', () => {
  const STATUS_CONDITION = encodeURIComponent('[{"field":"status","op":"Eq","value":"Backlog"}]');
  const SUMMARY_CONDITION = encodeURIComponent('[{"field":"summary","op":"Contains","value":"login"}]');

  function mockValues(values: string[]) {
    server.use(http.get('/api/projects/p1/filter-values', () =>
      HttpResponse.json({ field: 'status', values: values.map((name) => ({ name, count: 1 })), emptyCount: 0 })));
  }

  function mockEmptyPage() {
    server.use(http.post('/api/projects/p1/issues/query', () =>
      HttpResponse.json({ items: [], page: 1, pageSize: 50, totalCount: 0 })));
  }

  it('offers the values from this project as a dropdown for a dictionary field', async () => {
    mockFilterCatalog();
    mockEmptyPage();
    mockValues(['Backlog', 'In Progress']);

    renderIssueListAt(`/projects/p1/issues?filter=${STATUS_CONDITION}`);
    await expandFilter();

    // Re-queried inside waitFor: the control is REPLACED when the list arrives, so a node
    // captured beforehand stays an <input> however long the test waits.
    await waitFor(() =>
      expect(screen.getByLabelText(/value for condition 1/i).tagName).toBe('SELECT'));
    const control = screen.getByLabelText(/value for condition 1/i);
    expect(within(control).getByRole('option', { name: /^In Progress/ })).toBeInTheDocument();
  });

  it('keeps a free text box for a field that has no value list', async () => {
    mockFilterCatalog();
    mockEmptyPage();

    renderIssueListAt(`/projects/p1/issues?filter=${SUMMARY_CONDITION}`);
    await expandFilter();

    const control = await screen.findByLabelText(/value for condition 1/i);
    expect(control.tagName).toBe('INPUT');
  });

  it('falls back to the text box when the value list cannot be loaded', async () => {
    // A broken picker must not make an otherwise working filter unusable: the value can still
    // be typed, which is exactly what the field did before the picker existed.
    mockFilterCatalog();
    mockEmptyPage();
    server.use(http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })));

    renderIssueListAt(`/projects/p1/issues?filter=${STATUS_CONDITION}`);
    await expandFilter();

    const control = await screen.findByLabelText(/value for condition 1/i);
    expect(control.tagName).toBe('INPUT');
  });

  it('does not ask for a value list when the operator takes no value', async () => {
    // IsNull needs nothing to compare against; fetching a list for it is a wasted round trip.
    mockFilterCatalog();
    mockEmptyPage();
    let asked = 0;
    server.use(http.get('/api/projects/p1/filter-values', () => {
      asked += 1;
      return HttpResponse.json({ field: 'status', values: [{ name: 'Backlog', count: 1 }], emptyCount: 0 });
    }));

    const isNull = encodeURIComponent('[{"field":"status","op":"IsNull"}]');
    renderIssueListAt(`/projects/p1/issues?filter=${isNull}`);
    await expandFilter();

    await screen.findByLabelText(/field for condition 1/i);
    expect(asked).toBe(0);
  });
});

describe('nested groups', () => {
  it('sends the tree it shows, minus the parts switched off', async () => {
    const { bodies } = mockIssuesQuery();
    const tree = {
      logic: 'And',
      conditions: [{ field: 'summary', op: 'Contains', value: 'login' }],
      groups: [{
        logic: 'Or',
        conditions: [
          { field: 'storyPoints', op: 'Gte', value: 3 },
          { field: 'storyPoints', op: 'IsNull' },
        ],
      }],
    };
    const user = userEvent.setup({ delay: null });

    renderIssueListAt(`/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(tree))}`);

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual(tree));

    await expandFilter();
    await user.click(await screen.findByRole('button', { name: 'Disable condition 2.1' }));

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual({
      logic: 'And',
      conditions: [{ field: 'summary', op: 'Contains', value: 'login' }],
      groups: [{ logic: 'Or', conditions: [{ field: 'storyPoints', op: 'IsNull' }] }],
    }));
  });
});

/** Reports the router's current query string, so a test can see what the list wrote to the URL. */
function SearchProbe({ onSearch }: { onSearch: (search: string) => void }) {
  onSearch(useLocation().search);
  return null;
}
