import type { ChangeEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import type {
  FilterCatalog, FilterCondition, FilterOperator, ListValue, PeriodKey, PeriodValue, RelativeAmount, RelativeUnit,
} from '../../api/issues';
import { getFilterValues } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Field, Select } from '../../ui';
import { ChipInput } from './ChipInput';
import {
  fieldNames, isDictionaryField, operatorLabel, operatorsFor, resetOperatorIfDisallowed, typeFor,
} from './filterCatalogHelpers';
import {
  freshCondition, switchOperator, takesAmount, takesList, takesNoValue, takesPeriod, takesRange,
} from './filterTree';
import { INPUT_CLASS } from './styles';
import {
  DEFAULT_RELATIVE, PERIODS, RELATIVE_UNITS, browserTimeZone, isRelativeAmount, isScalar, isSmartValue, readPeriod,
} from './valueShapes';
import { MultiValuePicker, SingleValueSelect } from './ValuePicker';

interface ConditionRowProps {
  /** Only used to fetch this project's values for the pickers. */
  projectId: string;
  condition: FilterCondition;
  catalog: FilterCatalog;
  onChange: (next: FilterCondition) => void;
  /** Position in the tree, e.g. "1" or "2.1" — part of every control's accessible name. */
  label: string;
}

const JIRA_KEY_SEPARATORS = /[,\s]+/;
const toUpper = (s: string) => s.toUpperCase();

/**
 * One field / operator / value row. Every transition rebuilds the condition from scratch with
 * only the key its new operator takes, so a stale value/values key of the wrong shape never
 * rides along to a 400. The row's identity (id, on/off) belongs to GroupEditor, which puts it
 * back (withIdentityOf).
 */
export function ConditionRow({ projectId, condition, catalog, onChange, label }: ConditionRowProps) {
  const { field, op } = condition;
  const fieldType = typeFor(catalog, field);
  const known = fieldType !== undefined;
  const isDict = isDictionaryField(catalog, field);

  const handleFieldChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const nextField = event.target.value;
    const nextOp = resetOperatorIfDisallowed(catalog, nextField, op);
    // A value valid for the old field rarely is for the new one, so it is dropped, not carried.
    onChange(freshCondition(nextField, nextOp, browserTimeZone()));
  };

  // What carries over between operators, and what each one starts with, lives in switchOperator.
  const handleOperatorChange = (event: ChangeEvent<HTMLSelectElement>) =>
    onChange(switchOperator(condition, event.target.value as FilterOperator, browserTimeZone()));

  // Only dictionary fields have a value list, and only operators that take a value need one.
  const wantsValueList = isDict && !takesNoValue(op);
  const valueList = useQuery({
    queryKey: queryKeys.filterValues(projectId, field),
    queryFn: () => getFilterValues(projectId, field),
    enabled: wantsValueList,
    // A project's set of values changes only when a sync runs.
    staleTime: 5 * 60 * 1000,
  });
  // The list is an affordance, not a gate: while it loads, or if it fails, every control falls
  // back to typing — a broken picker must not make a working filter unusable.
  const picked = wantsValueList ? valueList.data : undefined;

  const valueId = `condition-${label}-value`;
  const valueLabel = `Value for condition ${label}`;
  const textValue = typeof condition.value === 'string' ? condition.value : '';

  const renderValueControl = () => {
    if (takesNoValue(op)) return null;

    if (takesRange(op)) {
      // Two bounds, either left blank for an open end. Dates for the date fields, numbers for points.
      const [from = null, to = null] = condition.values ?? [];
      const numeric = fieldType === 'Decimal';
      const shown = (v: ListValue) => (isScalar(v) ? String(v) : '');
      const setBound = (index: 0 | 1, raw: string) => {
        const bound: ListValue = raw === '' ? null : numeric ? Number(raw) : raw;
        onChange({ field, op, values: index === 0 ? [bound, to] : [from, bound] });
      };
      return (
        <>
          <Field label={`From for condition ${label}`} id={`condition-${label}-from`}>
            <input
              type={numeric ? 'number' : 'date'}
              step={numeric ? 'any' : undefined}
              className={INPUT_CLASS}
              value={shown(from)}
              onChange={(e) => setBound(0, e.target.value)}
            />
          </Field>
          <Field label={`To for condition ${label}`} id={`condition-${label}-to`}>
            <input
              type={numeric ? 'number' : 'date'}
              step={numeric ? 'any' : undefined}
              className={INPUT_CLASS}
              value={shown(to)}
              onChange={(e) => setBound(1, e.target.value)}
            />
          </Field>
        </>
      );
    }

    if (takesAmount(op)) {
      // An amount of 0 is "not entered yet": the input shows blank and the row is unfinished.
      const rel: RelativeAmount = isRelativeAmount(condition.value)
        ? condition.value
        : { amount: 0, unit: DEFAULT_RELATIVE.unit };
      const set = (next: RelativeAmount) => onChange({ field, op, value: next });
      return (
        <>
          <Field label={`Amount for condition ${label}`} id={`condition-${label}-amount`}>
            <input
              type="number"
              min={1}
              step={1}
              className={INPUT_CLASS}
              value={rel.amount >= 1 ? rel.amount : ''}
              onChange={(e) => set({
                amount: e.target.value === '' ? 0 : Math.trunc(Number(e.target.value)),
                unit: rel.unit,
              })}
            />
          </Field>
          <Field label={`Unit for condition ${label}`} id={`condition-${label}-unit`}>
            <Select value={rel.unit} onChange={(e) => set({ amount: rel.amount, unit: e.target.value as RelativeUnit })}>
              {RELATIVE_UNITS.map((u) => (
                <option key={u} value={u}>{u}</option>
              ))}
            </Select>
          </Field>
          {/* "more than [14] [days] ago": the operator reads "more than … ago", the row ends the sentence. */}
          {op === 'NotInLast' && <span className="pb-2 text-sm text-[var(--ju-text)]">ago</span>}
        </>
      );
    }

    if (takesPeriod(op)) {
      const current = readPeriod(condition.value);
      const pick = (period: PeriodKey) => {
        const next: PeriodValue = { period };
        // The zone captured with the operator stays with the condition. One that never had a zone
        // (a link from elsewhere) means UTC on the server, and keeps meaning it.
        const zone = current ? current.timeZone : browserTimeZone();
        if (zone) next.timeZone = zone;
        onChange({ field, op, value: next });
      };
      return (
        <Field label={`Period for condition ${label}`} id={`condition-${label}-period`} hint={current?.timeZone ?? 'UTC'}>
          <Select value={current?.period ?? ''} onChange={(e) => pick(e.target.value as PeriodKey)}>
            {!current && <option value="">— choose —</option>}
            {PERIODS.map((p) => (
              <option key={p.key} value={p.key}>{p.label}</option>
            ))}
          </Select>
        </Field>
      );
    }

    if (takesList(op)) {
      const values = condition.values ?? [];
      if (picked) {
        return (
          <MultiValuePicker
            label={`Values for condition ${label}`}
            options={picked.values}
            emptyCount={picked.emptyCount}
            selected={values}
            max={catalog.maxInValues}
            onChange={(next) => onChange({ field, op, values: next })}
          />
        );
      }
      const isKey = field === 'jiraKey';
      // Typing cannot produce (Empty) or a smart value, but one that arrived in a link is kept,
      // not silently lost.
      const kept = values.filter((v) => v === null || isSmartValue(v));
      return (
        <ChipInput
          key={field}
          id={valueId}
          label={valueLabel}
          values={values.filter(isScalar).map(String)}
          max={catalog.maxInValues - kept.length}
          splitOn={isKey ? JIRA_KEY_SEPARATORS : undefined}
          normalize={isKey ? toUpper : undefined}
          onChange={(next) => onChange({ field, op, values: [...kept, ...next] })}
        />
      );
    }

    if (isDict && picked) {
      return (
        <Field label={valueLabel} id={valueId}>
          <SingleValueSelect
            value={textValue}
            options={picked.values}
            emptyCount={picked.emptyCount}
            onPick={(name) => onChange({ field, op, value: name })}
            // "is (Empty)" is "is empty": the operator changes, so a null value never reaches the wire.
            onPickEmpty={() => onChange({ field, op: op === 'Neq' ? 'IsNotNull' : 'IsNull' })}
          />
        </Field>
      );
    }

    if (fieldType === 'Decimal') {
      return (
        <Field label={valueLabel} id={valueId}>
          <input
            type="number"
            className={INPUT_CLASS}
            value={isScalar(condition.value) ? condition.value : ''}
            onChange={(e) => onChange({ field, op, value: e.target.value === '' ? '' : Number(e.target.value) })}
          />
        </Field>
      );
    }

    return (
      <Field label={valueLabel} id={valueId}>
        <input
          type={fieldType === 'DateTime' ? 'date' : 'text'}
          className={INPUT_CLASS}
          value={textValue}
          onChange={(e) => onChange({ field, op, value: e.target.value })}
        />
      </Field>
    );
  };

  return (
    <div className="flex flex-wrap items-end gap-2">
      <Field
        label={`Field for condition ${label}`}
        id={`condition-${label}-field`}
        error={known ? undefined : `'${field}' can no longer be filtered on, so this condition is not applied.`}
      >
        <Select value={field} onChange={handleFieldChange}>
          {!known && <option value={field} disabled>{field}</option>}
          {fieldNames(catalog).map((f) => (
            <option key={f} value={f}>{f}</option>
          ))}
        </Select>
      </Field>

      <Field label={`Operator for condition ${label}`} id={`condition-${label}-operator`}>
        <Select value={op} onChange={handleOperatorChange}>
          {operatorsFor(catalog, field).map((o) => (
            <option key={o} value={o}>{operatorLabel(o)}</option>
          ))}
        </Select>
      </Field>

      {renderValueControl()}
    </div>
  );
}
