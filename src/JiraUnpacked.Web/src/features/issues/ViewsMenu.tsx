import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { createView, deleteView, updateView, type SavedView } from '../../api/views';
import { queryKeys } from '../../api/queryKeys';
import { Button, ConfirmDialog, Field } from '../../ui';
import { INPUT_CLASS } from './styles';
import { resaveRequest, saveRequest, withView, withoutView, type ListSnapshot } from './viewState';

/** The server's cap on a name. */
const MAX_NAME_LENGTH = 100;

const TRIGGER_CLASS =
  'rounded-[var(--ju-radius)] border border-[var(--ju-border)] px-3 py-2 text-sm text-[var(--ju-text)]';
const ITEM_CLASS =
  'w-full rounded px-2 py-1 text-left text-sm text-[var(--ju-text)] hover:bg-[var(--ju-surface-2)] ' +
  'disabled:cursor-not-allowed disabled:opacity-60';

/** The server's own words for a refusal, except the clash the user fixes by typing: a taken name. */
export function viewErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.status === 409 && /already exists/i.test(error.title)
      ? 'A view with this name already exists'
      : error.title;
  }
  return 'Could not save the view.';
}

export interface ViewsMenuProps {
  projectId: string;
  views: SavedView[] | undefined;
  unavailable: boolean;
  loaded: SavedView | undefined;
  modified: boolean;
  /** The list as it is now — what Save and Save as new write. */
  snapshot: ListSnapshot;
  onApply: (view: SavedView) => void;
  /** Points the URL at a view (after Save as new) or at none (after Delete). */
  onLoad: (viewId: string | null) => void;
}

type Mode = 'list' | 'saveAs' | 'rename';

interface NameFormProps {
  initial: string;
  pending: boolean;
  onSubmit: (name: string) => void;
  onCancel: () => void;
}

/** The name field Save as new and Rename share: required, trimmed, at most 100 characters. */
function NameForm({ initial, pending, onSubmit, onCancel }: NameFormProps) {
  const [name, setName] = useState(initial);
  const id = useId();
  const trimmed = name.trim();

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (trimmed !== '') onSubmit(trimmed);
  };

  return (
    <form className="flex flex-col gap-2" onSubmit={submit}>
      <Field label="View name" id={id}>
        <input
          className={INPUT_CLASS}
          value={name}
          maxLength={MAX_NAME_LENGTH}
          // The field appears because the user just asked to name something.
          autoFocus
          onChange={(e) => setName(e.target.value)}
        />
      </Field>
      <div className="flex gap-2">
        <Button type="submit" size="sm" disabled={trimmed === ''} loading={pending}>
          Save
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  );
}

/**
 * The toolbar's saved views: a button naming the loaded view (and whether the list has moved away
 * from it), Save to overwrite it, and a popover to open, create, rename, star and delete views.
 *
 * A controlled popover rather than a native <details> (ColumnChooser's choice): choosing a view or
 * finishing an action must close it, and a failed save must keep it open with the name as typed.
 */
export function ViewsMenu({
  projectId, views, unavailable, loaded, modified, snapshot, onApply, onLoad,
}: ViewsMenuProps) {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [mode, setMode] = useState<Mode>('list');
  const [error, setError] = useState<string | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const saveAsButtonRef = useRef<HTMLButtonElement>(null);
  const renameButtonRef = useRef<HTMLButtonElement>(null);

  // Which list-mode button Cancel should hand focus back to. Set right before switchTo('list'),
  // read by the effect below once that button has actually remounted (it doesn't exist yet at the
  // moment Cancel is clicked — 'saveAs'/'rename' mode has it unmounted).
  const returnFocusModeRef = useRef<'saveAs' | 'rename' | null>(null);

  // create/edit's onError (below) can fire after the menu has already been closed — the user
  // pressed Escape or clicked outside while the request was still in flight. Read fresh here
  // rather than through a closure that may have captured the `open` of the render the request
  // started in.
  const openRef = useRef(open);
  openRef.current = open;
  const popoverId = useId();

  const openMenu = () => {
    setOpen(true);
    // A failure from a request that resolved while the menu was last closed (see create/edit's
    // onError below) must never surface just because the menu happens to reopen.
    setError(null);
  };

  const close = () => {
    setOpen(false);
    setMode('list');
    setError(null);
    // Whatever inside the popover held focus is about to unmount; hand it back to the one control
    // that's always on screen, rather than letting focus fall to <body>.
    triggerRef.current?.focus();
  };
  const switchTo = (next: Mode) => {
    setError(null);
    setMode(next);
  };

  // Read by the document's Escape listener below, which is attached once per opening.
  const closeRef = useRef(close);
  closeRef.current = close;

  // A click anywhere outside closes it, like every other popover. Deliberately not close(): an
  // outside click has already moved focus to whatever was clicked, and clawing it back to the
  // trigger would fight that.
  //
  // Escape is heard on the document, not on the popover: while a save is pending its Save button
  // is disabled, a disabled control cannot hold focus, and the keydown then starts at <body> —
  // it would never reach a handler on the popover itself.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent) => {
      if (rootRef.current?.contains(event.target as Node)) return;
      setOpen(false);
      setMode('list');
      setError(null);
    };
    const onKeyDown = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      closeRef.current();
    };
    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  // Cancel's target button doesn't exist yet at the moment Cancel is clicked (mode is still
  // 'saveAs'/'rename', so the list-mode buttons are unmounted) — this waits for the render where
  // 'list' mode, and the button, is back before focusing it.
  useEffect(() => {
    if (mode !== 'list') return;
    const target = returnFocusModeRef.current;
    returnFocusModeRef.current = null;
    if (target === 'saveAs') saveAsButtonRef.current?.focus();
    else if (target === 'rename') renameButtonRef.current?.focus();
  }, [mode]);

  /** Puts a saved view into the cached list at once (so the label follows), then asks the server. */
  const store = (saved: SavedView) => {
    queryClient.setQueryData<SavedView[]>(queryKeys.views(projectId), (old) => withView(old ?? [], saved));
    void queryClient.invalidateQueries({ queryKey: queryKeys.views(projectId) });
  };

  // Overwrites with the list as it is now; isDefault left out, so the flag is unchanged.
  const overwrite = useMutation({
    mutationFn: ({ view, list }: { view: SavedView; list: ListSnapshot }) =>
      updateView(projectId, view.id, saveRequest(view.name, list)),
    onSuccess: store,
  });
  // A failed Save speaks only about the list and view it tried to write: once the list is edited
  // (a new snapshot) or another view — or none — is loaded (Back, say), the alert is stale.
  const overwriteError = overwrite.error
    && overwrite.variables?.list === snapshot && overwrite.variables.view.id === loaded?.id
    ? overwrite.error
    : null;

  // Both onError handlers below only show the failure if the menu is still open to show it in — a
  // request that resolves after the user already closed the menu (Escape, click outside) must not
  // leave a stale alert waiting for the next time it opens. openMenu() also clears `error`
  // defensively on open, so the two guard the same thing from either direction.
  const create = useMutation({
    mutationFn: (name: string) => createView(projectId, saveRequest(name, snapshot)),
    onSuccess: (saved) => {
      // Cached before the URL names it, so the stale-id check never sees an unknown id.
      store(saved);
      onLoad(saved.id);
      close();
    },
    onError: (e) => {
      if (openRef.current) setError(viewErrorMessage(e));
    },
  });

  // Rename and the default flag re-send what the view stores, never the list's current edits.
  const edit = useMutation({
    mutationFn: ({ view, changes }: { view: SavedView; changes: { name?: string; isDefault?: boolean } }) =>
      updateView(projectId, view.id, resaveRequest(view, changes)),
    onSuccess: (saved) => {
      store(saved);
      close();
    },
    onError: (e) => {
      if (openRef.current) setError(viewErrorMessage(e));
    },
  });

  // A view whose delete just succeeded, waiting for `loaded` to actually stop naming it before its
  // cache entry is dropped (see the effect below).
  //
  // Why the wait: `loaded` can go undefined from two independent signals — the URL's view=
  // dropping, or this view leaving the cached list — and here in MemoryRouter/BrowserRouter,
  // react-router applies a navigation's new location by calling setState *inside
  // React.startTransition* (confirmed by reading react-router's own source: MemoryRouter's
  // `history.listen(setState)` wraps `setStateImpl` in `startTransition` unless a route
  // explicitly opts out with `useTransitions={false}`, which this app does not). A transition is a
  // LOW-PRIORITY update: React commits it in a later render than any plain, synchronous update
  // requested in the same tick. react-query's cache notifications are exactly that — a plain
  // update delivered through useSyncExternalStore — so writing the cache here, in the same tick as
  // onLoad(null), would commit *before* the URL's transition does, even though onLoad ran first in
  // program order. That render shows "no matching view" from the cache while the URL still names
  // the deleted view — the menu button already reads "Views" before view= has actually left the
  // address bar. Recording the id and updating the cache only once `loaded` itself has moved on (in
  // the effect below) ties the write to the URL change having already landed, rather than to
  // program order, which React's scheduler doesn't preserve across a transition boundary.
  //
  // Do NOT "fix" this by awaiting the navigation instead, or by wrapping the cache write in its own
  // startTransition: setSearchParams exposes no promise for the transition's actual commit, and a
  // useSyncExternalStore-backed update (what react-query uses) cannot be deferred with
  // startTransition regardless of where the *call* to setQueryData sits — React always renders a
  // store subscriber's update synchronously and eagerly, by design, specifically to prevent it from
  // ever showing a torn/stale snapshot of the store. Wrapping the write in startTransition would not
  // change when it commits.
  const pendingDeleteId = useRef<string | null>(null);

  const remove = useMutation({
    mutationFn: (view: SavedView) => deleteView(projectId, view.id),
    onSuccess: (_nothing, view) => {
      // See pendingDeleteId's comment above. The list keeps showing what it shows; it is just no
      // longer "that view".
      pendingDeleteId.current = view.id;
      onLoad(null);
      setConfirmingDelete(false);
      // The confirm dialog (and its focused "Delete view" button) is about to unmount; hand focus
      // back to the one control guaranteed to still be on screen.
      triggerRef.current?.focus();
    },
  });

  useEffect(() => {
    const id = pendingDeleteId.current;
    if (id === null || loaded?.id === id) return;
    pendingDeleteId.current = null;
    queryClient.setQueryData<SavedView[]>(queryKeys.views(projectId), (old) => withoutView(old ?? [], id));
    void queryClient.invalidateQueries({ queryKey: queryKeys.views(projectId) });
  }, [loaded, projectId, queryClient]);

  const label = loaded ? `Views: ${loaded.name}${modified ? ' • modified' : ''}` : 'Views';

  return (
    <>
      <div ref={rootRef} className="relative">
        <div className="flex items-center gap-2">
          <button
            ref={triggerRef}
            type="button"
            // No aria-haspopup: its values promise a menu, listbox, tree, grid or dialog, and this
            // popover is none of them — a group of controls. Expanded + controls say what it does.
            aria-expanded={open}
            aria-controls={open ? popoverId : undefined}
            className={TRIGGER_CLASS}
            onClick={() => (open ? close() : openMenu())}
          >
            {label} <span aria-hidden="true">▾</span>
          </button>
          {loaded && modified && (
            <Button
              onClick={() => overwrite.mutate({ view: loaded, list: snapshot })}
              loading={overwrite.isPending}
              title={`Overwrite “${loaded.name}” with the list as it is now`}
            >
              Save
            </Button>
          )}
        </div>

        {overwriteError && (
          <p role="alert" className="mt-1 text-sm text-[var(--ju-danger)]">
            {viewErrorMessage(overwriteError)}
          </p>
        )}

        {open && (
          <div
            id={popoverId}
            role="group"
            aria-label="Saved views"
            className="absolute right-0 z-20 mt-1 flex w-72 flex-col gap-2 rounded-[var(--ju-radius)] border border-[var(--ju-border)] bg-[var(--ju-surface)] p-3 shadow-lg"
          >
            {unavailable ? (
              <p className="text-sm text-[var(--ju-muted)]">Views unavailable</p>
            ) : (
              <>
                {!views ? (
                  <p className="text-sm text-[var(--ju-muted)]">Loading views…</p>
                ) : views.length === 0 ? (
                  <p className="text-sm text-[var(--ju-muted)]">No saved views yet</p>
                ) : (
                  <ul className="flex flex-col">
                    {views.map((view) => (
                      <li key={view.id}>
                        <button
                          type="button"
                          className={ITEM_CLASS}
                          aria-current={view.id === loaded?.id ? 'true' : undefined}
                          onClick={() => {
                            close();
                            overwrite.reset();
                            onApply(view);
                          }}
                        >
                          {view.isDefault && (
                            <>
                              <span aria-hidden="true">★ </span>
                              <span className="sr-only">Default: </span>
                            </>
                          )}
                          {view.name}
                        </button>
                      </li>
                    ))}
                  </ul>
                )}

                <hr className="border-[var(--ju-border)]" />

                {mode === 'list' && (
                  <div className="flex flex-col">
                    <button
                      ref={saveAsButtonRef}
                      type="button"
                      className={ITEM_CLASS}
                      onClick={() => switchTo('saveAs')}
                    >
                      Save as new view…
                    </button>
                    {loaded && (
                      <>
                        <button
                          ref={renameButtonRef}
                          type="button"
                          className={ITEM_CLASS}
                          onClick={() => switchTo('rename')}
                        >
                          Rename…
                        </button>
                        <button
                          type="button"
                          className={ITEM_CLASS}
                          disabled={edit.isPending}
                          onClick={() => {
                            setError(null);
                            edit.mutate({ view: loaded, changes: { isDefault: !loaded.isDefault } });
                          }}
                        >
                          {loaded.isDefault ? 'Remove default' : 'Set as default'}
                        </button>
                        <button
                          type="button"
                          className={ITEM_CLASS}
                          onClick={() => {
                            close();
                            remove.reset();
                            setConfirmingDelete(true);
                          }}
                        >
                          Delete…
                        </button>
                      </>
                    )}
                  </div>
                )}

                {mode === 'saveAs' && (
                  <NameForm
                    initial=""
                    pending={create.isPending}
                    onSubmit={(name) => {
                      setError(null);
                      create.mutate(name);
                    }}
                    onCancel={() => {
                      returnFocusModeRef.current = 'saveAs';
                      switchTo('list');
                    }}
                  />
                )}

                {mode === 'rename' && loaded && (
                  <NameForm
                    initial={loaded.name}
                    pending={edit.isPending}
                    onSubmit={(name) => {
                      setError(null);
                      edit.mutate({ view: loaded, changes: { name } });
                    }}
                    onCancel={() => {
                      returnFocusModeRef.current = 'rename';
                      switchTo('list');
                    }}
                  />
                )}

                {error && (
                  <p role="alert" className="text-sm text-[var(--ju-danger)]">
                    {error}
                  </p>
                )}
              </>
            )}
          </div>
        )}
      </div>

      {loaded && (
        <ConfirmDialog
          open={confirmingDelete}
          title={`Delete view “${loaded.name}”?`}
          confirmLabel="Delete view"
          onConfirm={() => remove.mutate(loaded)}
          onCancel={() => setConfirmingDelete(false)}
          pending={remove.isPending}
          error={remove.error ? viewErrorMessage(remove.error) : null}
        >
          “{loaded.name}” is removed for good. The list keeps showing what it shows now.
        </ConfirmDialog>
      )}
    </>
  );
}
