import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { updateView, type SavedView } from '../../api/views';
import { queryKeys } from '../../api/queryKeys';
import { Button } from '../../ui';
import { saveRequest, withView, type ListSnapshot } from './viewState';

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
  /** The list as it is now — what Save writes. */
  snapshot: ListSnapshot;
  onApply: (view: SavedView) => void;
}

/**
 * The toolbar's saved views: a button naming the loaded view (and whether the list has moved away
 * from it), Save to overwrite it, and a popover listing the project's views.
 *
 * A controlled popover rather than a native <details> (ColumnChooser's choice): choosing a view must
 * close it, and later a failed save must keep it open — both are state this component owns.
 */
export function ViewsMenu({ projectId, views, unavailable, loaded, modified, snapshot, onApply }: ViewsMenuProps) {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  // A click anywhere outside closes it, like every other popover.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [open]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Escape' || !open) return;
    event.preventDefault();
    setOpen(false);
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

  const label = loaded ? `Views: ${loaded.name}${modified ? ' • modified' : ''}` : 'Views';

  return (
    <div ref={rootRef} className="relative" onKeyDown={onKeyDown}>
      <div className="flex items-center gap-2">
        <button
          ref={triggerRef}
          type="button"
          aria-haspopup="true"
          aria-expanded={open}
          className={TRIGGER_CLASS}
          onClick={() => setOpen((o) => !o)}
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
          ) : !views ? (
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
                      setOpen(false);
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
        </div>
      )}
    </div>
  );
}
