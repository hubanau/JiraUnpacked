import { renderHook, act } from '@testing-library/react';
import { MemoryRouter, useNavigate, useSearchParams } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { DEFAULT_CHART, useListState, type ListParams } from './useListState';
import { emptyRoot, newId, type EditorGroup } from './filterTree';

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

describe('useListState — fix round 1', () => {
  it('keeps a stable identity for every setter across a URL change', () => {
    // Task 5/7 may put these in an effect's dependency array; a fresh identity on every URL write
    // (react-router hands back a new setSearchParams each time) would make that effect fire
    // forever, so this is load-bearing, not tidiness.
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });
    const first = result.current.state;

    act(() => result.current.state.setSort('storyPoints', false)); // a URL change

    expect(result.current.state.setTree).toBe(first.setTree);
    expect(result.current.state.setSort).toBe(first.setSort);
    expect(result.current.state.setColumns).toBe(first.setColumns);
    expect(result.current.state.setViewId).toBe(first.setViewId);
    expect(result.current.state.applyParams).toBe(first.applyParams);
  });

  it('adopts an applied view\'s tree once — it is not re-parsed again once the URL commits', () => {
    // The tree applyParams builds is this hook's own write, held immediately (see the doc comment
    // on applyParams): the URL commit that follows must be recognised as ITS echo, not treated as
    // a fresh link and re-parsed all over again. Every parse mints fresh ids from a shared counter
    // (filterTree's `newId`), so parsing VIEW_FILTER (one group, one condition -- two ids) exactly
    // once, and not twice, is something a global id-count checkpoint can catch even though the two
    // parses would otherwise be indistinguishable by content alone.
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });
    const idNum = (id: string) => Number(id.slice(1));

    const before = idNum(newId());
    act(() => result.current.state.applyParams(APPLIED, { replace: false }));
    const after = idNum(newId());

    expect(after - before - 1).toBe(2);
  });

  it('does not re-mint the tree when re-applying a view whose filter the URL already holds, under a different sort', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.applyParams(APPLIED, { replace: false }));
    const rootId = result.current.state.tree.id;
    const rowId = result.current.state.tree.conditions[0].id;

    // Re-applying under a different sort: the filter itself is already the URL's, so nothing
    // about the tree should move.
    act(() => result.current.state.applyParams({ ...APPLIED, sortBy: 'summary' }, { replace: false }));

    expect(result.current.state.tree.id).toBe(rootId);
    expect(result.current.state.tree.conditions[0].id).toBe(rowId);
  });

  it('adopts a Forward navigation fresh, rather than a stale record left by a filter-unchanged re-apply', () => {
    // Back and Forward through the SAME filter content (only sortBy differs between the two
    // "applied" entries) never touches held.raw/rawFilter -- the mismatch branch that consults
    // recentWrites is only entered once the filter itself actually moves. So the hazard this test
    // is really after can only show up two navigations later: Back past BOTH applied entries to
    // the bare list (a genuine filter change, null -- nothing to match, adopted fresh) and then
    // Forward again (another genuine filter change, back to VIEW_FILTER). If applyParams's
    // filter-unchanged re-apply had recorded VIEW_FILTER into recentWrites (it must not: nothing
    // about that re-apply ever changes the URL's `filter`, so nothing would ever arrive to consume
    // it), that stale entry would still be sitting there and would wrongly match this Forward nav
    // as "our own echo" -- keeping the bare list's empty tree instead of adopting VIEW_FILTER's.
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.applyParams(APPLIED, { replace: false })); // push V (sortBy=priority)
    act(() => result.current.state.applyParams({ ...APPLIED, sortBy: 'summary' }, { replace: false })); // push V again, filter unchanged

    act(() => result.current.navigate(-1)); // back to V, sortBy=priority (filter still VIEW_FILTER)
    act(() => result.current.navigate(-1)); // back to the bare list (filter genuinely gone)
    act(() => result.current.navigate(1));  // forward to V, sortBy=priority again (filter genuinely back)

    expect(result.current.state.tree.conditions).toEqual([
      expect.objectContaining({ field: 'status', op: 'IsNull' }),
    ]);
  });

  it('writes only the five ListParams keys onto the URL, ignoring any other key on the object', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });
    const withExtra = { ...APPLIED, extra: 'nope' } as ListParams;

    act(() => result.current.state.applyParams(withExtra, { replace: false }));

    expect(result.current.params.has('extra')).toBe(false);
  });

  it('does not push a duplicate history entry when the view applied is already open and unmodified', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.applyParams(APPLIED, { replace: false })); // pushes one entry
    act(() => result.current.state.applyParams(APPLIED, { replace: false })); // identical -- must not push again

    act(() => result.current.navigate(-1));

    // One Back is enough to land back on the bare list -- if the second call had pushed a
    // duplicate entry, this Back would only land on the first (identical) applied entry instead.
    expect(result.current.state.viewId).toBeNull();
    expect(result.current.params.has('filter')).toBe(false);
  });
});

describe('useListState — chart', () => {
  it('is closed on a URL without chart=', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    expect(result.current.state.chart).toBeNull();
  });

  it('round-trips the chart, writing defaults as absent params', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.setChart(DEFAULT_CHART));
    expect(result.current.params.toString()).toBe('chart=status');
    expect(result.current.state.chart).toEqual(DEFAULT_CHART);

    act(() => result.current.state.setChart({ groupBy: 'team', measure: 'points', type: 'donut' }));
    expect(result.current.params.get('chart')).toBe('team');
    expect(result.current.params.get('chartMeasure')).toBe('points');
    expect(result.current.params.get('chartType')).toBe('donut');
    expect(result.current.state.chart).toEqual({ groupBy: 'team', measure: 'points', type: 'donut' });

    act(() => result.current.state.setChart(null));
    expect(result.current.params.toString()).toBe('');
  });

  it('falls back to the defaults for values it does not know', () => {
    const { result } = renderHook(useProbe, {
      wrapper: wrap('/projects/p1/issues?chart=summary&chartMeasure=hours&chartType=radar'),
    });

    expect(result.current.state.chart).toEqual(DEFAULT_CHART);
  });

  it('keeps both a chart write and a filter write made in the same tick', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => {
      result.current.state.setChart({ ...DEFAULT_CHART, groupBy: 'team' });
      result.current.state.setTree(ONE_ROW);
    });

    expect(result.current.params.get('chart')).toBe('team');
    expect(result.current.params.has('filter')).toBe(true);
  });

  it('is not list state: a chart alone still lets a default view open', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?chart=team') });

    expect(result.current.state.hasListState).toBe(false);
  });

  it('survives opening a saved view', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?chart=team&chartType=table') });

    act(() => result.current.state.applyParams(
      { filter: null, sortBy: 'createdAt', sortDesc: null, cols: null, view: 'v1' }, { replace: false }));

    expect(result.current.state.chart).toEqual({ groupBy: 'team', measure: 'issues', type: 'table' });
  });
});
