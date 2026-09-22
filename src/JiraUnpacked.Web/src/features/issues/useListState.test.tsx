import { renderHook, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { useListState } from './useListState';

const wrap = (initial: string) =>
  function Wrapper({ children }: { children: React.ReactNode }) {
    return <MemoryRouter initialEntries={[initial]}>{children}</MemoryRouter>;
  };

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
    // A URL is user-editable and chat clients truncate them. A broken one must degrade to
    // "no filter", never to a blank screen.
    const { result } = renderHook(() => useListState(), {
      wrapper: wrap('/projects/p1/issues?filter=%7Bnot-json'),
    });

    expect(result.current.conditions).toEqual([]);
  });

  it('keeps the conditions array stable between renders', () => {
    // IssueList resets paging in a useEffect keyed on conditions. A fresh array identity on
    // every render would make that effect fire forever, so this is load-bearing, not tidiness.
    const { result, rerender } = renderHook(() => useListState(), {
      wrapper: wrap('/projects/p1/issues?filter=' + encodeURIComponent('[{"field":"status","op":"Eq","value":"Backlog"}]')),
    });
    const first = result.current.conditions;

    rerender();

    expect(result.current.conditions).toBe(first);
  });

  it('leaves defaults out of the URL so a shared link carries only deliberate choices', () => {
    const { result } = renderHook(() => useListState(), { wrapper: wrap('/projects/p1/issues?sortBy=summary') });

    act(() => result.current.setSort('updatedAt', true));

    expect(result.current.sortBy).toBe('updatedAt');
  });
});
