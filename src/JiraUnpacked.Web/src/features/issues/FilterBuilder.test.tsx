import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import IssueList from './IssueList';
import { MAX_CONDITIONS, MAX_IN_VALUES } from './filterCatalog';

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
 * mounting IssueList instead of the builder in isolation.
 */
function mockIssuesQuery(): { bodies: Record<string, unknown>[] } {
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
  it('shows a collapsed summary and an Add condition control when there is no filter', async () => {
    mockIssuesQuery();
    renderIssueList();

    expect(await screen.findByText(/no filter — showing all issues/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /add condition/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /clear all/i })).not.toBeInTheDocument();
  });

  it('sends neither value nor values for IsNull', async () => {
    const { bodies } = mockIssuesQuery();
    const user = userEvent.setup();
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
    const user = userEvent.setup();
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
    const user = userEvent.setup();
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
    const user = userEvent.setup();
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
    const user = userEvent.setup();
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
    const user = userEvent.setup();
    renderIssueList();
    await addCondition(user);

    await user.selectOptions(screen.getByLabelText(/field for condition 1/i), 'jiraKey');

    const ops = within(screen.getByLabelText(/operator for condition 1/i))
      .getAllByRole('option')
      .map((o) => o.textContent);
    expect(ops).toEqual(['Eq', 'Contains', 'StartsWith']);
  });

  it('blocks a 21st condition', async () => {
    mockIssuesQuery();
    const user = userEvent.setup();
    renderIssueList();

    for (let i = 0; i < MAX_CONDITIONS; i++) {
      await addCondition(user);
    }

    expect(screen.getByRole('button', { name: /add condition/i })).toBeDisabled();
    expect(screen.getByText(/at most 20 conditions/i)).toBeInTheDocument();
  });

  it('blocks a 101st In value', async () => {
    mockIssuesQuery();
    const user = userEvent.setup();
    renderIssueList();
    await addCondition(user);
    await user.selectOptions(screen.getByLabelText(/operator for condition 1/i), 'In');

    const valueInput = screen.getByLabelText(/value for condition 1/i);
    const addValueButton = screen.getByRole('button', { name: /add value/i });

    for (let i = 0; i < MAX_IN_VALUES; i++) {
      fireEvent.change(valueInput, { target: { value: `v${i}` } });
      fireEvent.click(addValueButton);
    }

    expect(screen.getByRole('button', { name: /add value/i })).toBeDisabled();
  });

  it('still shows a server 400 when the client thought the filter was fine', async () => {
    mockIssuesQuery();
    const user = userEvent.setup();
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
    const user = userEvent.setup();
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
    const user = userEvent.setup();
    renderIssueList();
    await addCondition(user);
    expect(screen.getByRole('button', { name: /clear all/i })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /clear all/i }));

    expect(await screen.findByText(/no filter — showing all issues/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /clear all/i })).not.toBeInTheDocument();
  });
});
