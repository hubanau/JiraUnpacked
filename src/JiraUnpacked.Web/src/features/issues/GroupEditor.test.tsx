import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import type { FilterCatalog } from '../../api/issues';
import { GroupEditor } from './GroupEditor';
import { countEnabled, type EditorCondition, type EditorGroup } from './filterTree';

const summary = (id: string, value = 'x'): EditorCondition => ({ id, field: 'summary', op: 'Contains', value });
const root = (rest: Partial<EditorGroup> = {}): EditorGroup =>
  ({ id: 'r', logic: 'And', conditions: [], groups: [], ...rest });
const nested = (id: string, rest: Partial<EditorGroup> = {}): EditorGroup =>
  ({ id, logic: 'And', conditions: [], groups: [], ...rest });

function renderEditor(initial: EditorGroup, catalog: FilterCatalog = FILTER_CATALOG) {
  // New rows default to status, whose value list is fetched. These tests are about structure.
  server.use(http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })));
  let latest = initial;
  function Harness() {
    const [tree, setTree] = useState(initial);
    return (
      <GroupEditor
        group={tree}
        onChange={(next) => { latest = next; setTree(next); }}
        catalog={catalog}
        projectId="p1"
        counts={countEnabled(tree)}
        depth={1}
        label=""
      />
    );
  }
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Harness />
    </QueryClientProvider>,
  );
  return { tree: () => latest };
}

describe('GroupEditor', () => {
  it('adds a nested group seeded with one blank condition', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({ conditions: [summary('a')] }));

    await user.click(screen.getByRole('button', { name: 'Add group' }));

    const group = screen.getByRole('group', { name: 'Group 2' });
    expect(within(group).getByLabelText('Field for condition 2.1')).toHaveValue('status');
    expect(editor.tree().groups[0].conditions).toHaveLength(1);
  });

  it('offers "Add group" down to the catalog depth and no further', async () => {
    const user = userEvent.setup();
    renderEditor(root({ conditions: [summary('a')] }));

    await user.click(screen.getByRole('button', { name: 'Add group' }));              // depth 2
    await user.click(screen.getByRole('button', { name: 'Add group to group 2' }));   // depth 3

    const deepest = screen.getByRole('group', { name: 'Group 2.2' });
    expect(within(deepest).queryByRole('button', { name: /add group/i })).toBeNull();
  });

  it('duplicates a condition directly after the original, as a separate row', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({ conditions: [summary('a', 'first'), summary('b', 'second')] }));

    await user.click(screen.getByRole('button', { name: 'Duplicate condition 1' }));

    expect(editor.tree().conditions.map((c) => c.value)).toEqual(['first', 'first', 'second']);
    expect(new Set(editor.tree().conditions.map((c) => c.id)).size).toBe(3);
  });

  it('duplicates a whole group, deep, with fresh ids', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({
      conditions: [summary('a')],
      groups: [nested('g', { logic: 'Or', conditions: [summary('b', 'inner')] })],
    }));

    await user.click(screen.getByRole('button', { name: 'Duplicate group 2' }));

    const [original, copy] = editor.tree().groups;
    expect(copy).toMatchObject({ logic: 'Or', conditions: [{ value: 'inner' }] });
    expect(copy.id).not.toBe(original.id);
    expect(copy.conditions[0].id).not.toBe(original.conditions[0].id);
  });

  it('removes a nested group and a condition', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({
      conditions: [summary('a')],
      groups: [nested('g', { conditions: [summary('b')] })],
    }));

    await user.click(screen.getByRole('button', { name: 'Remove group 2' }));
    await user.click(screen.getByRole('button', { name: 'Remove condition 1' }));

    expect(editor.tree().groups).toEqual([]);
    expect(editor.tree().conditions).toEqual([]);
  });

  it('switches a condition and a group off and on, showing the pressed state', async () => {
    const user = userEvent.setup();
    const editor = renderEditor(root({
      conditions: [summary('a')],
      groups: [nested('g', { conditions: [summary('b')] })],
    }));

    await user.click(screen.getByRole('button', { name: 'Disable condition 1' }));
    await user.click(screen.getByRole('button', { name: 'Disable group 2' }));

    expect(screen.getByRole('button', { name: 'Disable condition 1' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Disable group 2' })).toHaveAttribute('aria-pressed', 'true');
    expect(editor.tree().conditions[0].disabled).toBe(true);
    expect(editor.tree().groups[0].disabled).toBe(true);

    await user.click(screen.getByRole('button', { name: 'Disable condition 1' }));
    expect(editor.tree().conditions[0].disabled).toBeUndefined();
  });

  it('stops adding conditions at the limit, and counts only enabled ones', async () => {
    const user = userEvent.setup();
    renderEditor(root({ conditions: [summary('a'), summary('b')] }), { ...FILTER_CATALOG, maxConditions: 2 });

    const add = screen.getByRole('button', { name: 'Add condition' });
    expect(add).toBeDisabled();
    expect(add).toHaveAttribute('title', 'Limit: 2 conditions');
    expect(screen.getByRole('button', { name: 'Duplicate condition 1' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Disable condition 2' }));

    expect(screen.getByRole('button', { name: 'Add condition' })).toBeEnabled();
  });

  it('stops adding and duplicating groups at the group limit', () => {
    renderEditor(
      root({ conditions: [summary('a')], groups: [nested('g', { conditions: [summary('b')] })] }),
      { ...FILTER_CATALOG, maxGroups: 2 },
    );

    const add = screen.getByRole('button', { name: 'Add group' });
    expect(add).toBeDisabled();
    expect(add).toHaveAttribute('title', 'Limit: 2 groups');
    expect(screen.getByRole('button', { name: 'Duplicate group 2' })).toBeDisabled();
  });
});
