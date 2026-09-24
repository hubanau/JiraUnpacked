import { useEffect, useId, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
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

  const close = () => {
    setOpen(false);
    setMode('list');
    setError(null);
  };
  const switchTo = (next: Mode) => {
    setError(null);
    setMode(next);
  };

  // A click anywhere outside closes it, like every other popover.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent) => {
      if (rootRef.current?.contains(event.target as Node)) return;
      setOpen(false);
      setMode('list');
      setError(null);
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [open]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Escape' || !open) return;
    event.preventDefault();
    close();
    triggerRef.current?.focus();
  };

  /** Puts a saved view into the cached list at once (so the label follows), then asks the server. */
  const store = (saved: SavedView) => {
    queryClient.setQueryData<SavedView[]>(queryKeys.views(projectId), (old) => withView(old ?? [], saved));
    void queryClient.invalidateQueries({ queryKey: queryKeys.views(projectId) });
  };

  // Overwrites with the list as it is now; isDefault left out, so the flag is unchanged.
  const overwrite = useMutation({
    mutationFn: (view: SavedView) => updateView(projectId, view.id, saveRequest(view.name, snapshot)),
    onSuccess: store,
  });

  const create = useMutation({
    mutationFn: (name: string) => createView(projectId, saveRequest(name, snapshot)),
    onSuccess: (saved) => {
      // Cached before the URL names it, so the stale-id check never sees an unknown id.
      store(saved);
      onLoad(saved.id);
      close();
    },
    onError: (e) => setError(viewErrorMessage(e)),
  });

  // Rename and the default flag re-send what the view stores, never the list's current edits.
  const edit = useMutation({
    mutationFn: ({ view, changes }: { view: SavedView; changes: { name?: string; isDefault?: boolean } }) =>
      updateView(projectId, view.id, resaveRequest(view, changes)),
    onSuccess: (saved) => {
      store(saved);
      close();
    },
    onError: (e) => setError(viewErrorMessage(e)),
  });

  // A view whose delete just succeeded, waiting for `loaded` to actually stop naming it before its
  // cache entry is dropped (see the effect below).
  const pendingDeleteId = useRef<string | null>(null);

  const remove = useMutation({
    mutationFn: (view: SavedView) => deleteView(projectId, view.id),
    onSuccess: (_nothing, view) => {
      // `loaded` can go undefined from two independent signals: the URL's view= dropping, or this
      // view leaving the cached list. react-router's setSearchParams navigates asynchronously
      // (it awaits the router internally), so writing the cache here, in the same tick as
      // onLoad(null), would let the two signals settle in either order — a render could show "no
      // matching view" from the cache before the URL has actually moved, tearing them apart (the
      // menu button would already read "Views" while the URL still named the deleted view).
      // Recording the id and letting the effect below react once `loaded` itself has moved on
      // ties the cache write to the URL change having already landed, not to a guess about which
      // async update wins. The list keeps showing what it shows; it is just no longer "that view".
      pendingDeleteId.current = view.id;
      onLoad(null);
      setConfirmingDelete(false);
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
      <div ref={rootRef} className="relative" onKeyDown={onKeyDown}>
        <div className="flex items-center gap-2">
          <button
            ref={triggerRef}
            type="button"
            aria-haspopup="true"
            aria-expanded={open}
            className={TRIGGER_CLASS}
            onClick={() => (open ? close() : setOpen(true))}
          >
            {label} <span aria-hidden="true">▾</span>
          </button>
          {loaded && modified && (
            <Button
              onClick={() => overwrite.mutate(loaded)}
              loading={overwrite.isPending}
              title={`Overwrite “${loaded.name}” with the list as it is now`}
            >
              Save
            </Button>
          )}
        </div>

        {overwrite.error && (
          <p role="alert" className="mt-1 text-sm text-[var(--ju-danger)]">
            {viewErrorMessage(overwrite.error)}
          </p>
        )}

        {open && (
          <div
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
                    <button type="button" className={ITEM_CLASS} onClick={() => switchTo('saveAs')}>
                      Save as new view…
                    </button>
                    {loaded && (
                      <>
                        <button type="button" className={ITEM_CLASS} onClick={() => switchTo('rename')}>
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
                    onCancel={() => switchTo('list')}
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
                    onCancel={() => switchTo('list')}
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
