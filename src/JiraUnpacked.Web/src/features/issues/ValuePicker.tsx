import { useRef, useState, type KeyboardEvent, type SelectHTMLAttributes } from 'react';
import type { FilterValueOption, ListValue, SmartValue } from '../../api/issues';
import { Badge, Select } from '../../ui';
import { INPUT_CLASS } from './styles';
import { smartLabel } from './valueShapes';

/** How (Empty) reads wherever a value is shown. */
export const EMPTY_LABEL = '(Empty)';

// Option values are prefixed so that no real name — Jira allows almost any string — can collide
// with the (Empty) entry or the "choose" placeholder.
const VALUE_PREFIX = 'v:';
const EMPTY_VALUE = 'e:';

type SingleValueSelectProps = Omit<SelectHTMLAttributes<HTMLSelectElement>, 'value' | 'onChange'> & {
  /** The chosen name; '' = nothing chosen yet. */
  value: string;
  options: FilterValueOption[];
  emptyCount: number;
  onPick: (name: string) => void;
  /** (Empty) is not a value: the caller turns "is (Empty)" into "is empty". */
  onPickEmpty: () => void;
};

/** "is" / "is not" on a dictionary field: one value, (Empty) first, every entry with its count. */
export function SingleValueSelect({
  value, options, emptyCount, onPick, onPickEmpty, ...rest
}: SingleValueSelectProps) {
  // A value from a link that this project no longer has is still shown as what it is, not
  // replaced on screen by "— choose —" while the filter silently keeps it.
  const stale = value !== '' && !options.some((o) => o.name === value);
  return (
    <Select
      {...rest}
      value={value === '' ? '' : VALUE_PREFIX + value}
      onChange={(e) => {
        const v = e.target.value;
        if (v === EMPTY_VALUE) onPickEmpty();
        else if (v.startsWith(VALUE_PREFIX)) onPick(v.slice(VALUE_PREFIX.length));
      }}
    >
      <option value="">— choose —</option>
      <option value={EMPTY_VALUE}>{`${EMPTY_LABEL} (${emptyCount})`}</option>
      {stale && <option value={VALUE_PREFIX + value}>{`${value} (0)`}</option>}
      {options.map((o) => (
        <option key={o.name} value={VALUE_PREFIX + o.name}>{`${o.name} (${o.count})`}</option>
      ))}
    </Select>
  );
}

interface MultiValuePickerProps {
  /** Accessible name of the whole control, e.g. "Values for condition 1". */
  label: string;
  options: FilterValueOption[];
  emptyCount: number;
  /** null = (Empty); { smart } = a smart sprint value. */
  selected: ListValue[];
  onChange: (next: ListValue[]) => void;
  max: number;
}

type Choice = string | null | SmartValue;

/** What a value MEANS, as a key: two equal smart objects are one choice, whatever their identity. */
const keyOf = (v: Choice) => (v === null ? 'empty' : typeof v === 'string' ? `v:${v}` : `s:${v.smart}`);
const labelOf = (v: Choice) => (v === null ? EMPTY_LABEL : typeof v === 'string' ? v : smartLabel(v));

/**
 * "is any of" / "is none of" on a dictionary field: chips for what is chosen, and a popover with
 * a search box and a checkbox per value — (Empty) first — each with its count. A checkbox list
 * makes choosing the same value twice impossible, which free text never could.
 *
 * A native <details> for the popover, like ColumnChooser: it opens, closes and takes focus with
 * no JavaScript and no outside-click handling to get wrong.
 */
export function MultiValuePicker({ label, options, emptyCount, selected, onChange, max }: MultiValuePickerProps) {
  const [query, setQuery] = useState('');
  const detailsRef = useRef<HTMLDetailsElement>(null);
  const summaryRef = useRef<HTMLElement>(null);

  // <details> opens and closes natively, but does not close on Escape the way any other popover
  // does — without this, focus would be trapped inside an open list with no keyboard way out.
  const handleKeyDown = (event: KeyboardEvent<HTMLDetailsElement>) => {
    if (event.key !== 'Escape' || !detailsRef.current?.open) return;
    event.preventDefault();
    detailsRef.current.open = false;
    summaryRef.current?.focus();
  };

  // De-duplicated on the way in, by meaning, so even a hand-edited link cannot show a value twice.
  const chosen: Choice[] = [];
  const chosenKeys = new Set<string>();
  for (const raw of selected) {
    const v: Choice = typeof raw === 'number' ? String(raw) : raw;
    if (!chosenKeys.has(keyOf(v))) {
      chosenKeys.add(keyOf(v));
      chosen.push(v);
    }
  }
  const isChosen = (v: Choice) => chosenKeys.has(keyOf(v));
  const atLimit = chosen.length >= max;
  const toggle = (v: Choice) =>
    onChange(isChosen(v) ? chosen.filter((x) => keyOf(x) !== keyOf(v)) : [...chosen, v]);

  const needle = query.trim().toLowerCase();
  const matches = options.filter((o) => o.name.toLowerCase().includes(needle));
  const showEmpty = needle === '' || EMPTY_LABEL.toLowerCase().includes(needle);

  const choice = (v: Choice, text: string, count: number) => (
    <label key={keyOf(v)} className="flex items-center gap-2 text-sm text-[var(--ju-text)]">
      <input type="checkbox" checked={isChosen(v)} disabled={!isChosen(v) && atLimit} onChange={() => toggle(v)} />
      <span className="flex-1">{text}</span>{' '}
      <span className="text-xs text-[var(--ju-muted)]">{count}</span>
    </label>
  );

  return (
    <div role="group" aria-label={label} className="flex flex-col gap-1">
      <span aria-hidden="true" className="text-sm font-medium text-[var(--ju-text)]">{label}</span>
      {chosen.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {chosen.map((v) => (
            <Badge key={keyOf(v)}>
              {labelOf(v)}
              <button type="button" className="ml-1" aria-label={`Remove ${labelOf(v)}`} onClick={() => toggle(v)}>
                ×
              </button>
            </Badge>
          ))}
        </div>
      )}
      <details ref={detailsRef} className="relative" onKeyDown={handleKeyDown}>
        <summary
          ref={summaryRef}
          className="cursor-pointer list-none rounded-[var(--ju-radius)] border border-[var(--ju-border)] px-3 py-2 text-sm text-[var(--ju-text)]"
        >
          Choose values
        </summary>
        <div className="absolute z-10 mt-1 flex max-h-72 w-64 flex-col gap-2 overflow-auto rounded-[var(--ju-radius)] border border-[var(--ju-border)] bg-[var(--ju-surface)] p-3 shadow-lg">
          <input
            type="search"
            aria-label={`Search ${label}`}
            className={INPUT_CLASS}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          {showEmpty && choice(null, EMPTY_LABEL, emptyCount)}
          {matches.map((o) => choice(o.name, o.name, o.count))}
          {!showEmpty && matches.length === 0 && (
            <p className="text-xs text-[var(--ju-muted)]">No matching values</p>
          )}
        </div>
      </details>
      {atLimit && <p className="text-xs text-[var(--ju-muted)]">You can add at most {max} values.</p>}
    </div>
  );
}
