import { renderHook, act } from '@testing-library/react';
import { MemoryRouter, useNavigate, useSearchParams } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { useListState, type ListParams } from './useListState';
import { emptyRoot, type EditorGroup } from './filterTree';

const wrap = (initial: string) =>
  function Wrapper({ children }: { children: React.ReactNode }) {
    return <MemoryRouter initialEntries={[initial]}>{children}</MemoryRouter>;
  };

/** Several history entries, starting on the last one — so a test can tell push from replace. */
const wrapAt = (entries: string[]) =>
  function Wrapper({ children }: { children: React.ReactNode }) {
    return <MemoryRouter initialEntries={entries} initialIndex={entries.length - 1}>{children}</MemoryRouter>;
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

  it('adopts a URL that matches an old write whose echo already landed, rather than keeping a later one forever', () => {
    // Regression for a bug where a remembered write was never forgotten once its own echo had
    // already arrived: add X, Clear all, build Y, then navigate to a URL with no filter (the same
    // '' that Clear All once wrote) used to be misread as Clear All's echo *again* and silently
    // kept Y, because '' stayed in the remembered-writes window forever.
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.setTree(ONE_ROW));     // add X -- echo lands and is consumed
    act(() => result.current.state.setTree(emptyRoot())); // Clear all -- echo lands and is consumed
    act(() => result.current.state.setTree(ONE_ROW));     // build Y -- echo lands and is consumed

    // A fresh navigation carrying no filter -- not this hook's own echo of "Clear all", which
    // already landed two writes ago -- must be adopted, not mistaken for a stale match.
    act(() => result.current.navigate('/projects/p1/issues'));

    expect(result.current.state.tree.conditions).toEqual([]);
  });

  it('keeps both changes when a filter write is followed immediately by a sort write, before either has committed', () => {
    // Regression for a bug where react-router's setSearchParams updater is handed the
    // searchParams of the last COMMITTED render, not of a sibling call earlier in the same tick:
    // two patches back to back (e.g. a filter edit immediately followed by clicking a sort
    // header) would each build on the same stale base, so the second silently discarded the
    // first instead of both landing in the URL.
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => {
      result.current.state.setTree(ONE_ROW);
      result.current.state.setSort('storyPoints', false);
    });

    expect(JSON.parse(result.current.params.get('filter')!)).toEqual({
      logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'l' }],
    });
    expect(result.current.params.get('sortBy')).toBe('storyPoints');
    expect(result.current.params.get('sortDesc')).toBe('false');
  });

  it('does not keep re-recording a no-op write, so a later Back can still adopt the URL', () => {
    // Regression: typing "2." into storyPoints parses to the same value as "2" already in the
    // URL -- the write is a no-op, the URL never changes. If that no-op is remembered as if it
    // were a real write, it never gets consumed (nothing ever changes the URL to produce its
    // echo), so it sits in the window forever and a later Back to that same URL is misread as
    // this hook's own echo and ignored -- the tree stays whatever it was, not what the URL holds.
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });
    const storyPoints2 = (rowId: string): EditorGroup => ({
      id: 'root-1', logic: 'And', groups: [],
      conditions: [{ id: rowId, field: 'storyPoints', op: 'Eq', value: 2 }],
    });

    act(() => result.current.state.setTree(storyPoints2('row-1')));   // "2" -- writes filter=...
    act(() => result.current.state.setTree(storyPoints2('row-2')));   // "2." parses to the same 2 -- no-op
    act(() => result.current.navigate('/projects/p1/issues2'));       // away
    act(() => result.current.navigate(-1));                           // Back

    expect(result.current.state.tree.conditions).toEqual([
      expect.objectContaining({ field: 'storyPoints', op: 'Eq', value: 2 }),
    ]);
  });

  it('follows the URL to a different IssueList page while the hook stays mounted', () => {
    const first = encodeURIComponent('{"logic":"And","conditions":[{"field":"status","op":"IsNull"}]}');
    const second = encodeURIComponent('{"logic":"Or","conditions":[{"field":"status","op":"IsNotNull"}]}');
    const { result } = renderHook(useProbe, { wrapper: wrap(`/projects/p1/issues?filter=${first}`) });

    act(() => result.current.navigate(`/projects/p2/issues?filter=${second}`));

    expect(result.current.state.tree.logic).toBe('Or');
    expect(result.current.state.tree.conditions).toEqual([
      expect.objectContaining({ field: 'status', op: 'IsNotNull' }),
    ]);
  });

  it('still recognises an echo of an earlier write once a later one is already held, without losing the later one', () => {
    // Two writes issued before either's own URL confirmation has had a chance to land -- what
    // happens whenever the tree changes twice in quick succession. Whichever of the two the URL
    // settles on first, the held tree must end on the LATEST write: an echo of the first must
    // never regress it back, and must still be recognised as this hook's own (not adopted fresh,
    // which would mint new ids and drop the second edit entirely).
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });
    const later: EditorGroup = {
      id: 'root-2', logic: 'And', groups: [],
      conditions: [{ id: 'row-2', field: 'summary', op: 'Contains', value: 'lo' }],
    };

    act(() => {
      result.current.state.setTree(ONE_ROW);
      result.current.state.setTree(later);
    });

    expect(result.current.state.tree.id).toBe('root-2');
    expect(result.current.state.tree.conditions[0].id).toBe('row-2');
    expect(result.current.state.tree.conditions[0].value).toBe('lo');
  });
});

const VIEW_FILTER = '{"logic":"Or","conditions":[{"field":"status","op":"IsNull"}]}';
const APPLIED: ListParams = {
  filter: VIEW_FILTER, sortBy: 'priority', sortDesc: 'false', cols: 'jiraKey,summary', view: 'v1',
};

describe('useListState — opening a saved view', () => {
  it('replaces the whole view in one history entry, and Back returns to what the list showed', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?sortBy=summary&logic=Or') });

    act(() => result.current.state.applyParams(APPLIED, { replace: false }));

    // Every list param is the view's; the stale legacy `logic` is gone with the rest.
    expect(Object.fromEntries(result.current.params)).toEqual({
      filter: VIEW_FILTER, sortBy: 'priority', sortDesc: 'false', cols: 'jiraKey,summary', view: 'v1',
    });
    expect(result.current.state.viewId).toBe('v1');
    expect(result.current.state.tree.logic).toBe('Or');
    expect(result.current.state.sortBy).toBe('priority');
    expect(result.current.state.sortDescending).toBe(false);
    expect(result.current.state.columns).toEqual(['jiraKey', 'summary']);

    act(() => result.current.navigate(-1));

    expect(result.current.params.get('sortBy')).toBe('summary');
    expect(result.current.state.viewId).toBeNull();
    expect(result.current.state.tree.conditions).toEqual([]);
  });

  it('with replace, leaves no entry behind: Back skips the list it stood in for', () => {
    const { result } = renderHook(useProbe, {
      wrapper: wrapAt(['/elsewhere?from=before', '/projects/p1/issues']),
    });

    act(() => result.current.state.applyParams(APPLIED, { replace: true }));
    act(() => result.current.navigate(-1));

    expect(result.current.params.get('from')).toBe('before');
  });

  it('an applied view wins over an edit queued in the same tick', () => {
    // The URL already holds the view's filter, so it "does not move" — only a tree held at once
    // by applyParams (not one waited for from the URL) can undo the queued edit.
    const { result } = renderHook(useProbe, {
      wrapper: wrap(`/projects/p1/issues?filter=${encodeURIComponent(VIEW_FILTER)}`),
    });

    act(() => {
      result.current.state.setTree(ONE_ROW);
      result.current.state.applyParams(APPLIED, { replace: false });
    });

    expect(result.current.params.get('filter')).toBe(VIEW_FILTER);
    expect(result.current.state.tree.logic).toBe('Or');
    expect(result.current.state.tree.conditions).toEqual([
      expect.objectContaining({ field: 'status', op: 'IsNull' }),
    ]);
  });

  it('an edit right after applying builds on the applied URL, keeping view= and the view’s sort', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => {
      result.current.state.applyParams(APPLIED, { replace: false });
      result.current.state.setTree(ONE_ROW);
    });

    expect(result.current.params.get('view')).toBe('v1');
    expect(result.current.params.get('sortBy')).toBe('priority');
    expect(JSON.parse(result.current.params.get('filter')!)).toEqual({
      logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'l' }],
    });
    expect(result.current.state.tree.conditions[0].id).toBe('row-1');
  });

  it('Clear all keeps view=, so the list can show the view as modified', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.applyParams(APPLIED, { replace: false }));
    act(() => result.current.state.setTree(emptyRoot()));

    expect(result.current.params.has('filter')).toBe(false);
    expect(result.current.params.get('view')).toBe('v1');
  });

  it('setViewId adds or drops view= and nothing else', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?sortBy=summary&view=gone') });

    act(() => result.current.state.setViewId(null));
    expect(result.current.params.has('view')).toBe(false);
    expect(result.current.params.get('sortBy')).toBe('summary');

    act(() => result.current.state.setViewId('v2'));
    expect(result.current.state.viewId).toBe('v2');
  });

  it.each([
    ['/projects/p1/issues', false],
    ['/projects/p1/issues?unrelated=1', false],
    ['/projects/p1/issues?view=v1', true],
    ['/projects/p1/issues?cols=jiraKey', true],
    ['/projects/p1/issues?sortDesc=false', true],
    ['/projects/p1/issues?logic=Or', true],
  ])('knows whether %s carries any list state', (url, expected) => {
    const { result } = renderHook(useProbe, { wrapper: wrap(url) });
    expect(result.current.state.hasListState).toBe(expected);
  });
});
