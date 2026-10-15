import type { HistoryWindow } from '../../api/issues';
import { Field, Select } from '../../ui';
import { AmountInputs, PeriodSelect, RangeInputs, type PartNamer } from './DateControls';
import { DEFAULT_WINDOW_AMOUNT } from './historyShapes';
import { DEFAULT_PERIOD, browserTimeZone } from './valueShapes';

/** In reading order; '' is "any time" — no window at all. */
const KINDS: readonly { key: '' | HistoryWindow['kind']; label: string }[] = [
  { key: '', label: 'any time' },
  { key: 'inLast', label: 'in the last' },
  { key: 'inPeriod', label: 'in period' },
  { key: 'between', label: 'between' },
];

/** The window's own parts: "During: amount for condition 1", id "condition-1-during-amount". */
const duringParts = (label: string): PartNamer => (part) => ({
  label: `During: ${part.toLowerCase()} for condition ${label}`,
  id: `condition-${label}-during-${part.toLowerCase()}`,
});

interface DuringControlProps {
  /** Position in the tree, e.g. "1" or "2.1" — part of every control's accessible name. */
  label: string;
  /** Undefined = any time. */
  value: HistoryWindow | undefined;
  onChange: (next: HistoryWindow | undefined) => void;
}

/**
 * When the change happened: any time, in the last N units, in a calendar period (in the zone
 * captured when it was chosen) or between two dates — the date operators' own controls, so the
 * two read and behave alike.
 */
export function DuringControl({ label, value, onChange }: DuringControlProps) {
  const parts = duringParts(label);
  const choose = (kind: string) => {
    if (kind === 'inLast') onChange({ kind: 'inLast', ...DEFAULT_WINDOW_AMOUNT });
    else if (kind === 'inPeriod') onChange({ kind: 'inPeriod', period: DEFAULT_PERIOD, timeZone: browserTimeZone() });
    else if (kind === 'between') onChange({ kind: 'between', from: null, to: null });
    else onChange(undefined);
  };
  return (
    <>
      <Field label={`During for condition ${label}`} id={`condition-${label}-during`}>
        <Select value={value?.kind ?? ''} onChange={(e) => choose(e.target.value)}>
          {KINDS.map((k) => (
            <option key={k.key} value={k.key}>{k.label}</option>
          ))}
        </Select>
      </Field>
      {value?.kind === 'inLast' && (
        <AmountInputs
          parts={parts}
          value={{ amount: value.amount, unit: value.unit }}
          onChange={(next) => onChange({ kind: 'inLast', ...next })}
        />
      )}
      {value?.kind === 'inPeriod' && (
        <PeriodSelect
          parts={parts}
          value={value.timeZone ? { period: value.period, timeZone: value.timeZone } : { period: value.period }}
          onChange={(next) => onChange({ kind: 'inPeriod', ...next })}
        />
      )}
      {value?.kind === 'between' && (
        <RangeInputs
          parts={parts}
          from={value.from}
          to={value.to}
          numeric={false}
          onChange={(from, to) => onChange({
            kind: 'between',
            from: typeof from === 'string' ? from : null,
            to: typeof to === 'string' ? to : null,
          })}
        />
      )}
    </>
  );
}
