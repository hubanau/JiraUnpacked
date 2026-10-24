import { useEffect, useId, useRef, useState } from 'react';
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

const ITEM_CLASS =
  'rounded px-2 py-1 text-left text-sm text-[var(--ju-text)] hover:bg-[var(--ju-surface-2)] disabled:cursor-not-allowed disabled:opacity-50';

/**
 * The ⋯ button in a filterable cell and its two choices. Visible on row hover and on keyboard
 * focus. A popover of plain buttons, not an ARIA menu (no arrow-key model to honour) — the same
 * choice the Views menu makes. Escape closes it and returns focus to the button; a click
 * elsewhere closes it.
 */
export function CellFilterMenu({ column, value, includeHint, excludeHint, onPick }: CellFilterMenuProps) {
  const [open, setOpen] = useState(false);
  const popoverId = useId();
  const boxRef = useRef<HTMLSpanElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
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

  const pick = (mode: CellFilterMode) => {
    setOpen(false);
    onPick(mode);
  };

  return (
    <span ref={boxRef} className="relative inline-flex">
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
          id={popoverId}
          role="group"
          aria-label={`Filter by ${column}`}
          className="absolute left-0 top-full z-10 mt-1 flex min-w-max flex-col rounded-md border border-[var(--ju-border)] bg-[var(--ju-surface)] p-1 shadow-sm"
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
