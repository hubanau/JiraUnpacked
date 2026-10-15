import type { ListValue, PeriodKey, PeriodValue, RelativeAmount, RelativeUnit } from '../../api/issues';
import { Field, Select } from '../../ui';
import { INPUT_CLASS } from './styles';
import {
  DEFAULT_RELATIVE, MAX_RELATIVE_AMOUNT, PERIODS, RELATIVE_UNITS, browserTimeZone, isScalar,
} from './valueShapes';

/**
 * The controls the date operators and history windows share: an amount and a unit ("in the
 * last"), a calendar period with its zone ("in period"), and two bounds ("between"). Each part's
 * accessible name and id come from a PartNamer, so the same control reads "Amount for condition 1"
 * in a date row and "During: amount for condition 1" in a history window.
 */
export type DatePart = 'Amount' | 'Unit' | 'Period' | 'From' | 'To';
export type PartNamer = (part: DatePart) => { label: string; id: string };

/** A condition's own parts: "Amount for condition 1", id "condition-1-amount". */
export const conditionParts = (label: string): PartNamer => (part) => ({
  label: `${part} for condition ${label}`,
  id: `condition-${label}-${part.toLowerCase()}`,
});

interface AmountInputsProps {
  parts: PartNamer;
  /** Undefined = nothing entered yet. */
  value: RelativeAmount | undefined;
  onChange: (next: RelativeAmount) => void;
}

/** "[14] [days]". An amount of 0 is "not entered yet": the input shows blank and the row is unfinished. */
export function AmountInputs({ parts, value, onChange }: AmountInputsProps) {
  const rel: RelativeAmount = value ?? { amount: 0, unit: DEFAULT_RELATIVE.unit };
  const amount = parts('Amount');
  const unit = parts('Unit');
  return (
    <>
      <Field label={amount.label} id={amount.id}>
        <input
          type="number"
          min={1}
          max={MAX_RELATIVE_AMOUNT}
          step={1}
          className={INPUT_CLASS}
          value={rel.amount >= 1 ? rel.amount : ''}
          onChange={(e) => onChange({
            amount: e.target.value === '' ? 0 : Math.trunc(Number(e.target.value)),
            unit: rel.unit,
          })}
        />
      </Field>
      <Field label={unit.label} id={unit.id}>
        <Select value={rel.unit} onChange={(e) => onChange({ amount: rel.amount, unit: e.target.value as RelativeUnit })}>
          {RELATIVE_UNITS.map((u) => (
            <option key={u} value={u}>{u}</option>
          ))}
        </Select>
      </Field>
    </>
  );
}

interface PeriodSelectProps {
  parts: PartNamer;
  /** Undefined = no period yet (a link that had none, or an unknown one). */
  value: PeriodValue | undefined;
  onChange: (next: PeriodValue) => void;
}

/**
 * The ten periods in reading order. The zone captured with the value stays with it; one that
 * never had a zone (a link from elsewhere) means UTC on the server, and keeps meaning it. With no
 * period yet, the browser's zone is the one a pick will capture, so that is the one named.
 */
export function PeriodSelect({ parts, value, onChange }: PeriodSelectProps) {
  const zone = value ? value.timeZone : browserTimeZone();
  const period = parts('Period');
  const pick = (key: PeriodKey) => {
    const next: PeriodValue = { period: key };
    if (zone) next.timeZone = zone;
    onChange(next);
  };
  return (
    <Field label={period.label} id={period.id} hint={zone ?? 'UTC'}>
      <Select value={value?.period ?? ''} onChange={(e) => pick(e.target.value as PeriodKey)}>
        {!value && <option value="">— choose —</option>}
        {PERIODS.map((p) => (
          <option key={p.key} value={p.key}>{p.label}</option>
        ))}
      </Select>
    </Field>
  );
}

interface RangeInputsProps {
  parts: PartNamer;
  from: ListValue;
  to: ListValue;
  /** Number inputs (story points) rather than date inputs. */
  numeric: boolean;
  onChange: (from: ListValue, to: ListValue) => void;
}

/** Two bounds, either left blank for an open end (null). */
export function RangeInputs({ parts, from, to, numeric, onChange }: RangeInputsProps) {
  const shown = (v: ListValue) => (isScalar(v) ? String(v) : '');
  const read = (raw: string): ListValue => (raw === '' ? null : numeric ? Number(raw) : raw);
  const bound = (part: 'From' | 'To', v: ListValue, set: (raw: string) => void) => {
    const p = parts(part);
    return (
      <Field label={p.label} id={p.id}>
        <input
          type={numeric ? 'number' : 'date'}
          step={numeric ? 'any' : undefined}
          className={INPUT_CLASS}
          value={shown(v)}
          onChange={(e) => set(e.target.value)}
        />
      </Field>
    );
  };
  return (
    <>
      {bound('From', from, (raw) => onChange(read(raw), to))}
      {bound('To', to, (raw) => onChange(from, read(raw)))}
    </>
  );
}
