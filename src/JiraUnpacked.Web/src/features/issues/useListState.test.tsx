import { renderHook, act } from '@testing-library/react';
import { MemoryRouter, useNavigate, useSearchParams } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { useListState } from './useListState';
import { emptyRoot, type EditorGroup } from './filterTree';

const wrap = (initial: string) =>
  function Wrapper({ children }: { children: React.ReactNode }) {
    return <MemoryRouter initialEntries={[initial]}>{children}</MemoryRouter>;
  };

/** The hook plus what it cannot report about itself: the URL it wrote, and a way to navigate. */
const useProbe = () => ({ state: useListState(), params: useSearchParams()[0], navigate: useNavigate() });

const ONE_ROW: EditorGroup = {
  id: 'root-1', logic: 'And', groups: [],
  conditions: [{ id: 'row-1', field: 'summary', op: 'Contains', value: 'l' }],
};

describe('useListState', () => {
  it('writes the tree to the URL as JSON without ids, and drops the legacy logic param', () => {
    const legacy = encodeURIComponent('[{"field":"status","op":"Eq","value":"Backlog"}]');
    const { result } = renderHook(useProbe, { wrapper: wrap(`/projects/p1/issues?filter=${legacy}&logic=Or`) });

    act(() => result.current.state.setTree(ONE_ROW));

    expect(result.current.params.get('logic')).toBeNull();
    expect(JSON.parse(result.current.params.get('filter')!)).toEqual({
      logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'l' }],
    });
  });

  it('keeps the editor’s ids across its own URL writes, so rows are not remounted mid-typing', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.setTree(ONE_ROW));
    act(() => result.current.state.setTree({
      ...ONE_ROW, conditions: [{ ...ONE_ROW.conditions[0], value: 'lo' }],
    }));

    expect(result.current.state.tree.id).toBe('root-1');
    expect(result.current.state.tree.conditions[0].id).toBe('row-1');
    expect(result.current.state.tree.conditions[0].value).toBe('lo');
  });

  it('adopts a filter from a URL it did not write (Back, a pasted link)', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });
    const tree = encodeURIComponent('{"logic":"Or","conditions":[{"field":"status","op":"IsNull"}]}');

    act(() => result.current.navigate(`/projects/p1/issues?filter=${tree}`));

    expect(result.current.state.tree.logic).toBe('Or');
    expect(result.current.state.tree.conditions).toEqual([expect.objectContaining({ field: 'status', op: 'IsNull' })]);
  });

  it('reads a legacy flat link with its logic param', () => {
    const legacy = encodeURIComponent('[{"field":"status","op":"Eq","value":"Backlog"}]');
    const { result } = renderHook(useProbe, { wrapper: wrap(`/projects/p1/issues?filter=${legacy}&logic=Or`) });

    expect(result.current.state.tree.logic).toBe('Or');
    expect(result.current.state.tree.conditions).toEqual([
      expect.objectContaining({ field: 'status', op: 'Eq', value: 'Backlog' }),
    ]);
  });

  it('removes filter= entirely for an empty tree', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.setTree(ONE_ROW));
    act(() => result.current.state.setTree(emptyRoot()));

    expect(result.current.params.has('filter')).toBe(false);
  });

  it('ignores a malformed filter param rather than crashing the page', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?filter=%7Bnot-json') });

    expect(result.current.state.tree.conditions).toEqual([]);
    expect(result.current.state.tree.groups).toEqual([]);
  });

  it('keeps the tree stable between renders', () => {
    // IssueList resets paging in an effect keyed on the tree. A fresh identity on every render
    // would make that effect fire forever, so this is load-bearing, not tidiness.
    const { result, rerender } = renderHook(useProbe, {
      wrapper: wrap('/projects/p1/issues?filter=' + encodeURIComponent('{"logic":"And","conditions":[{"field":"status","op":"IsNull"}]}')),
    });
    const first = result.current.state.tree;

    rerender();

    expect(result.current.state.tree).toBe(first);
  });

  it('reads sort and columns from the URL, and falls back to defaults', () => {
    const { result } = renderHook(useProbe, {
      wrapper: wrap('/projects/p1/issues?sortBy=storyPoints&sortDesc=false&cols=jiraKey,summary'),
    });
    expect(result.current.state.sortBy).toBe('storyPoints');
    expect(result.current.state.sortDescending).toBe(false);
    expect(result.current.state.columns).toEqual(['jiraKey', 'summary']);

    const plain = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });
    expect(plain.result.current.state.sortBy).toBe('updatedAt');
    expect(plain.result.current.state.sortDescending).toBe(true);
  });

  it('leaves defaults out of the URL so a shared link carries only deliberate choices', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?sortBy=summary') });

    act(() => result.current.state.setSort('updatedAt', true));

    expect(result.current.params.has('sortBy')).toBe(false);
  });
});
