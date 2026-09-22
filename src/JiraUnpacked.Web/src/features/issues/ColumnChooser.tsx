import { ALWAYS_ON, ISSUE_COLUMNS } from './columns';

interface ColumnChooserProps {
  selected: string[];
  onChange: (next: string[]) => void;
}

/**
 * Which columns the table shows. Presentation only — every column is already in the response, so
 * toggling one never costs a round trip.
 *
 * A native <details> rather than a custom popover: it opens, closes and takes focus correctly
 * with no JavaScript and no escape/outside-click handling to get wrong.
 */
export function ColumnChooser({ selected, onChange }: ColumnChooserProps) {
  const toggle = (id: string) => {
    if (id === ALWAYS_ON) return;
    onChange(
      selected.includes(id)
        // Preserve the catalog's order rather than append. Otherwise re-checking a column moves
        // it to the end and the table silently rearranges itself.
        ? selected.filter((c) => c !== id)
        : ISSUE_COLUMNS.filter((c) => c.id === id || selected.includes(c.id)).map((c) => c.id),
    );
  };

  return (
    <details className="relative">
      <summary className="cursor-pointer list-none rounded-[var(--ju-radius)] border border-[var(--ju-border)] px-3 py-2 text-sm">
        Columns
      </summary>
      <div className="absolute z-10 mt-1 flex flex-col gap-1 rounded-[var(--ju-radius)] border border-[var(--ju-border)] bg-[var(--ju-surface)] p-3 shadow-lg">
        {ISSUE_COLUMNS.map((col) => (
          <label key={col.id} className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={selected.includes(col.id)}
              disabled={col.id === ALWAYS_ON}
              onChange={() => toggle(col.id)}
            />
            {col.header}
          </label>
        ))}
      </div>
    </details>
  );
}
