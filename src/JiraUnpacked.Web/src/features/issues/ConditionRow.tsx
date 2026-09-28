import type { ChangeEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { FilterCatalog, FilterCondition, FilterOperator } from '../../api/issues';
import { getFilterValues } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Field, Select } from '../../ui';
import { ChipInput } from './ChipInput';
import {
  fieldNames, isDictionaryField, operatorLabel, operatorsFor, resetOperatorIfDisallowed, typeFor,
} from './filterCatalogHelpers';
import { freshCondition, switchOperator, takesList, takesNoValue } from './filterTree';
import { INPUT_CLASS } from './styles';
import { browserTimeZone, isScalar, isSmartValue } from './valueShapes';
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
