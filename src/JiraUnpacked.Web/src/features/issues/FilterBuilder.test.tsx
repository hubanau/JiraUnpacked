import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { delay, http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import IssueList from './IssueList';
import { FilterBuilder } from './FilterBuilder';
import { ConditionRow } from './ConditionRow';
import type { FilterCatalog, FilterCondition } from '../../api/issues';

/**
 * The server's filter catalog (mirrors FilterFields.All and
 * IssueFilterTranslator's guard consts on the .NET side) used to mock
 * GET /api/filter-catalog. FilterBuilder now fetches this instead of
 * importing a hand-maintained copy, so every test that mounts it must
 * register this handler first via mockFilterCatalog().
 */
const CATALOG: FilterCatalog = {
  fields: [
    { name: 'status', type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] },
    { name: 'issueType', type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] },
    { name: 'priority', type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] },
    { name: 'assignee', type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] },
    { name: 'sprint', type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] },
    { name: 'team', type: 'DictName', operators: ['Eq', 'Neq', 'In', 'IsNull'] },
    { name: 'storyPoints', type: 'Decimal', operators: ['Eq', 'Neq', 'Lt', 'Lte', 'Gt', 'Gte', 'IsNull'] },
    { name: 'createdAt', type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
    { name: 'updatedAt', type: 'DateTime', operators: ['Lt', 'Lte', 'Gt', 'Gte'] },
    { name: 'summary', type: 'Text', operators: ['Contains', 'StartsWith', 'IsNull'] },
    { name: 'jiraKey', type: 'Text', operators: ['Eq', 'Contains', 'StartsWith'] },
    { name: 'parent', type: 'ParentRef', operators: ['Eq', 'IsNull'] },
  ],
  maxConditions: 20,
  maxInValues: 100,
  maxGroups: 10,
  maxDepth: 3,
};

function mockFilterCatalog() {
  server.use(http.get('/api/filter-catalog', () => HttpResponse.json(CATALOG)));
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
        <FilterBuilder conditions={[]} onChange={() => {}} />
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
        <FilterBuilder conditions={[]} onChange={() => {}} />
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

  it('sends neither value nor values for IsNull', async () => {
    const { bodies } = mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'assignee');
    await user.selectOptions(screen.getByLabelText(/operator for condition 1/i), 'IsNull');

    await waitFor(() => {
      expect(bodies[bodies.length - 1].conditions).toEqual([{ field: 'assignee', op: 'IsNull' }]);
    });
    const lastCondition = (bodies[bodies.length - 1].conditions as Record<string, unknown>[])[0];
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
      expect(bodies[bodies.length - 1].conditions).toEqual([
        { field: 'status', op: 'In', values: ['Done', 'In Progress'] },
      ]);
    });
    const lastCondition = (bodies[bodies.length - 1].conditions as Record<string, unknown>[])[0];
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
      expect(bodies[bodies.length - 1].conditions).toEqual([
        { field: 'summary', op: 'Contains', value: 'login' },
      ]);
    });
    const lastCondition = (bodies[bodies.length - 1].conditions as Record<string, unknown>[])[0];
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
      expect(bodies[bodies.length - 1].conditions).toEqual([
        { field: 'storyPoints', op: 'Gte', value: 3 },
      ]);
    });
    const lastCondition = (bodies[bodies.length - 1].conditions as Record<string, unknown>[])[0];
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
    expect(ops).toEqual(['Eq', 'Contains', 'StartsWith']);
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
          conditions={Array.from(
            { length: CATALOG.maxConditions },
            () => ({ field: 'status', op: 'Eq' as const, value: '' }),
          )}
          onChange={() => {}}
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
    // ConditionRow takes the catalog as a plain prop (no fetch of its own),
    // so this needs no QueryClientProvider or MSW handler.
    const condition: FilterCondition = {
      field: 'status',
      op: 'In',
      values: Array.from({ length: CATALOG.maxInValues }, (_, i) => `v${i}`),
    };
    render(
      <ConditionRow condition={condition} catalog={CATALOG} onChange={() => {}} onRemove={() => {}} index={0} />,
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

  it('shows the Neq-on-dictionary-field note only while a row uses it', async () => {
    mockIssuesQuery();
    const user = userEvent.setup({ delay: null });
    renderIssueList();
    await addCondition(user);

    expect(
      screen.queryByText(/also matches issues where the field is empty/i),
    ).not.toBeInTheDocument();

    // The default added condition is field=status (a dictionary field);
    // switching its operator to Neq should surface the note.
    await user.selectOptions(screen.getByLabelText(/operator for condition 1/i), 'Neq');

    expect(
      await screen.findByText(/also matches issues where the field is empty/i),
    ).toBeInTheDocument();
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
      expect(bodies[bodies.length - 1].conditions).toEqual([
        { field: 'status', op: 'Eq', value: 'Done' },
        { field: 'summary', op: 'Contains', value: 'login' },
      ]);
    });

    // Remove the first (status/Done) row.
    await user.click(screen.getAllByRole('button', { name: /^remove$/i })[0]);

    // Only the summary row remains, now shifted up into row 1's position.
    expect(screen.getByLabelText(/field for condition 1/i)).toHaveValue('summary');
    expect(screen.queryByLabelText(/field for condition 2/i)).not.toBeInTheDocument();

    await waitFor(() => {
      expect(bodies[bodies.length - 1].conditions).toEqual([
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
      expect(bodies[bodies.length - 1].conditions).toEqual([
        { field: 'createdAt', op: 'Lt', value: '2026-01-15' },
      ]);
    });
    const lastCondition = (bodies[bodies.length - 1].conditions as Record<string, unknown>[])[0];
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
    await user.click(screen.getAllByRole('button', { name: /^remove$/i })[1]);

    // Condition 2 is now what used to be condition 3.
    await waitFor(() => {
      expect(screen.getByLabelText(/field for condition 2/i)).toHaveValue('priority');
    });
    // Its In-editor must be blank, not showing the draft typed against the
    // now-removed assignee condition.
    expect(screen.getByLabelText(/value for condition 2/i)).toHaveValue('');
  });
});
