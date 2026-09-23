import { useState, type ClipboardEvent, type KeyboardEvent } from 'react';
import { Badge, Button, Field } from '../../ui';
import { INPUT_CLASS } from './styles';

export interface ChipInputProps {
  id: string;
  label: string;
  values: string[];
  onChange: (next: string[]) => void;
  max: number;
  /**
   * Where a typed or pasted run breaks into separate values. Must not be global (/g), because
   * it is also used with .test(). Omitted = never split: the whole entry is one value, which is
   * right for dictionary names — "In Progress" is one status.
   */
  splitOn?: RegExp;
  /** e.g. upper-casing Jira keys, so "acme-1" and "ACME-1" are one chip. */
  normalize?: (value: string) => string;
}

const same = (s: string) => s;

/**
 * Free-text list entry: each committed value becomes a chip. Enter (or a separator key, when
 * there is one) commits; a pasted run is split into chips in one go. Values are trimmed,
 * normalised and de-duplicated, so the list can never carry the same value twice.
 */
export function ChipInput({ id, label, values, onChange, max, splitOn, normalize = same }: ChipInputProps) {
  const [draft, setDraft] = useState('');
  const shown = [...new Set(values)];
  const atLimit = shown.length >= max;

  const commit = (text: string) => {
    const next = [...shown];
    for (const token of splitOn ? text.split(splitOn) : [text]) {
      const value = normalize(token.trim());
      if (value && !next.includes(value) && next.length < max) next.push(value);
    }
    if (next.length !== shown.length) onChange(next);
    setDraft('');
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter' || (splitOn !== undefined && splitOn.test(event.key))) {
      event.preventDefault();
      commit(draft);
    }
  };

  const handlePaste = (event: ClipboardEvent<HTMLInputElement>) => {
    if (!splitOn) return;
    const text = event.clipboardData.getData('text/plain');
    if (!splitOn.test(text.trim())) return;   // a single value pastes into the box as usual
    event.preventDefault();
    commit(draft + text);
  };

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-end gap-2">
        <Field label={label} id={id}>
          <input
            type="text"
            className={INPUT_CLASS}
            value={draft}
            disabled={atLimit}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={handleKeyDown}
            onPaste={handlePaste}
          />
        </Field>
        <Button type="button" variant="secondary" onClick={() => commit(draft)} disabled={atLimit}>
          Add value
        </Button>
      </div>
      {atLimit && <p className="text-xs text-[var(--ju-muted)]">You can add at most {max} values.</p>}
      {shown.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {shown.map((v) => (
            <Badge key={v}>
              {v}
              <button
                type="button"
                aria-label={`Remove ${v}`}
                className="ml-1"
                onClick={() => onChange(shown.filter((x) => x !== v))}
              >
                ×
              </button>
            </Badge>
          ))}
        </div>
      )}
    </div>
  );
}
