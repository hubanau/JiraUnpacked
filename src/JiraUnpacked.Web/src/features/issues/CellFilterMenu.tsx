import { useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import type { CellFilterMode, CellValue } from './cellFilter';

interface CellFilterMenuProps {
  /** The column's header as the table shows it ("Status", "Points"). */
  column: string;
  value: CellValue;
  /** Why "Filter to" cannot be used now (a limit, or an operator the field lacks); undefined = it can. */
  includeHint?: string;
  excludeHint?: string;
  onPick: (mode: CellFilterMode) => void;
}

/** Space between the button and the popover, and the least the popover keeps from the viewport's edges. */
const GAP = 4;
const MARGIN = 4;

const ITEM_CLASS =
  'rounded px-2 py-1 text-left text-sm text-[var(--ju-text)] hover:bg-[var(--ju-surface-2)] disabled:cursor-not-allowed disabled:opacity-50';

/**
 * The ⋯ button in a filterable cell and its two choices. Visible on row hover and on keyboard
 * focus. A popover of plain buttons, not an ARIA menu (no arrow-key model to honour) — the same
 * choice the Views menu makes. Escape closes it (and goes no further, so no other layer closes
 * too) and returns focus to the button, as a pick does; a click elsewhere or focus moving out
 * closes it.
 *
 * The popover is position: fixed, placed from the button's own rectangle (again on every scroll
 * and resize), so the table's overflow-x-auto wrapper cannot clip it on the bottom rows; with no
 * room below it opens above. It stays next to the button in the DOM, so the Tab order holds.
 */
export function CellFilterMenu({ column, value, includeHint, excludeHint, onPick }: CellFilterMenuProps) {
  const [open, setOpen] = useState(false);
  const popoverId = useId();
  const boxRef = useRef<HTMLSpanElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLSpanElement>(null);
  const shown = value === null ? '(Empty)' : String(value);
  const quoted = value === null ? '(Empty)' : `"${shown}"`;

  useEffect(() => {
    if (!open) return undefined;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      setOpen(false);
      triggerRef.current?.focus();
    };
    const onPointer = (e: MouseEvent) => {
      if (!boxRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onPointer);
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('mousedown', onPointer);
    };
  }, [open]);

  // Before paint, so it never shows where it was last. Written to the element itself: a scroll
  // re-places it without re-rendering the row.
  useLayoutEffect(() => {
    if (!open) return undefined;
    const place = () => {
      const trigger = triggerRef.current;
      const popover = popoverRef.current;
      if (!trigger || !popover) return;
      const t = trigger.getBoundingClientRect();
      const p = popover.getBoundingClientRect();
      const below = t.bottom + GAP;
      const above = t.top - GAP - p.height;
      const roomBelow = below + p.height <= window.innerHeight - MARGIN;
      const top = !roomBelow && above >= MARGIN ? above : below;
      const left = Math.max(MARGIN, Math.min(t.left, window.innerWidth - p.width - MARGIN));
      popover.style.top = `${top}px`;
      popover.style.left = `${left}px`;
    };
    place();
    // Capture: the table's own horizontal scroll does not bubble to the window.
    window.addEventListener('scroll', place, true);
    window.addEventListener('resize', place);
    return () => {
      window.removeEventListener('scroll', place, true);
      window.removeEventListener('resize', place);
    };
  }, [open]);

  const pick = (mode: CellFilterMode) => {
    setOpen(false);
    triggerRef.current?.focus();
    onPick(mode);
  };

  return (
    <span
      ref={boxRef}
      className="inline-flex"
      onKeyDown={(e) => {
        if (!open || e.key !== 'Escape') return;
        e.stopPropagation();
        setOpen(false);
        triggerRef.current?.focus();
      }}
      onBlur={(e) => {
        if (open && !boxRef.current?.contains(e.relatedTarget as Node | null)) setOpen(false);
      }}
    >
      <button
        ref={triggerRef}
        type="button"
        aria-label={`Filter by ${column} ${shown}`}
        aria-expanded={open}
        aria-controls={open ? popoverId : undefined}
        className="rounded px-1 text-[var(--ju-muted)] opacity-0 hover:text-[var(--ju-text)] focus-visible:opacity-100 group-hover:opacity-100 aria-expanded:opacity-100"
        onClick={() => setOpen((o) => !o)}
      >
        <span aria-hidden="true">⋯</span>
      </button>
      {open && (
        <span
          ref={popoverRef}
          id={popoverId}
          role="group"
          aria-label={`Filter by ${column}`}
          // Focusable by script only: a click on its padding keeps focus inside, so it stays open.
          tabIndex={-1}
          style={{ position: 'fixed', top: 0, left: 0 }}
          className="z-20 flex min-w-max flex-col rounded-md border border-[var(--ju-border)] bg-[var(--ju-surface)] p-1 shadow-sm outline-none"
        >
          <button
            type="button"
            className={ITEM_CLASS}
            disabled={includeHint !== undefined}
            title={includeHint}
            onClick={() => pick('include')}
          >
            Filter to {quoted}
          </button>
          <button
            type="button"
            className={ITEM_CLASS}
            disabled={excludeHint !== undefined}
            title={excludeHint}
            onClick={() => pick('exclude')}
          >
            Exclude {quoted}
          </button>
        </span>
      )}
    </span>
  );
}
