import { useState, type ChangeEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { FilterCatalog, FilterCondition, FilterOperator } from '../../api/issues';
import { getFilterValues } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { fieldNames, isDictionaryField, operatorsFor, resetOperatorIfDisallowed, typeFor } from './filterCatalogHelpers';
import { Badge, Button, Field, Select } from '../../ui';

const INPUT_CLASS =
  'rounded-md border border-[var(--ju-border)] bg-[var(--ju-bg)] px-3 py-2 text-sm text-[var(--ju-text)] ' +
  'focus:outline-none focus:ring-2 focus:ring-[var(--ju-primary)] disabled:cursor-not-allowed disabled:opacity-60';

interface ConditionRowProps {
  /** Only used to fetch this project's values for the picker. */
  projectId: string;
  condition: FilterCondition;
  catalog: FilterCatalog;
  onChange: (next: FilterCondition) => void;
  /** Position in the tree, e.g. "1" or "2.1" — part of every control's accessible name. */
  label: string;
}

/**
 * One field/operator/value row. Every transition that could leave a stale
 * value/values key sitting alongside a new field or operator instead
 * rebuilds the condition from scratch — see the module doc on FilterCondition
 * in ../../api/issues for why a leftover key of the wrong shape is a runtime
 * 400 that nothing here would otherwise catch.
 */
export function ConditionRow({
  projectId, condition, catalog, onChange, label }: ConditionRowProps) {
  // Draft text for the "In" multi-value editor's add box. Local because it
  // is not part of the submitted condition until "Add value" commits it.
  const [inDraft, setInDraft] = useState('');

  // Rows are keyed by id (see GroupEditor), so this component instance is only ever reused for
  // the SAME condition across renders — its position label can change (a sibling removed above
  // it), but `condition` itself does not change identity except on a real edit. The reset below
  // therefore only fires when the condition's own field or operator changes, which is exactly
  // when a stale `inDraft` (not part of `condition`) would otherwise read as belonging to it.
  const [prev, setPrev] = useState(condition);
  if (prev !== condition) {
    setPrev(condition);
    setInDraft('');
  }

  const fieldType = typeFor(catalog, condition.field);
  const ops = operatorsFor(catalog, condition.field);

  const handleFieldChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const nextField = event.target.value;
    // A value valid for the old field's type rarely is for the new one, so
    // it — and any `values` array — is dropped entirely, not carried over.
    onChange({ field: nextField, op: resetOperatorIfDisallowed(catalog, nextField, condition.op) });
    setInDraft('');
  };

  const handleOperatorChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const nextOp = event.target.value as FilterOperator;
    if (nextOp === 'In') {
      onChange({ field: condition.field, op: nextOp, values: [] });
    } else if (nextOp === 'IsNull') {
      onChange({ field: condition.field, op: nextOp });
    } else {
      onChange({ field: condition.field, op: nextOp, value: '' });
    }
    setInDraft('');
  };

  const handleTextOrDateChange = (event: ChangeEvent<HTMLInputElement>) => {
    onChange({ field: condition.field, op: condition.op, value: event.target.value });
  };

  /** Same write, different element: a <select> is not an <input> as far as the types go. */
  const handlePickedChange = (event: ChangeEvent<HTMLSelectElement>) => {
    onChange({ field: condition.field, op: condition.op, value: event.target.value });
  };

  const handleNumberChange = (event: ChangeEvent<HTMLInputElement>) => {
    const raw = event.target.value;
    onChange({ field: condition.field, op: condition.op, value: raw === '' ? '' : Number(raw) });
  };

  const handleAddInValue = () => {
    const trimmed = inDraft.trim();
    const current = condition.values ?? [];
    if (!trimmed || current.length >= catalog.maxInValues) return;
    onChange({ field: condition.field, op: condition.op, values: [...current, trimmed] });
    setInDraft('');
  };

  const handleRemoveInValue = (removeIndex: number) => {
    const current = condition.values ?? [];
    onChange({
      field: condition.field,
      op: condition.op,
      values: current.filter((_, i) => i !== removeIndex),
    });
  };

  // Only dictionary fields have a value list, and only operators that take a value need one --
  // IsNull compares against nothing, so fetching for it is a wasted round trip.
  const wantsValueList = isDictionaryField(catalog, condition.field) && condition.op !== 'IsNull';
  const valueList = useQuery({
    queryKey: queryKeys.filterValues(projectId, condition.field),
    queryFn: () => getFilterValues(projectId, condition.field),
    enabled: wantsValueList,
    // A project's set of values changes only when a sync runs.
    staleTime: 5 * 60 * 1000,
  });

  // The list is an affordance, not a gate. If it is missing or failed, every control below falls
  // back to the text box it replaced, which is exactly what the field did before the picker
  // existed -- a broken picker must not make a working filter unusable.
  const pickable = wantsValueList && valueList.data ? valueList.data.values.map((v) => v.name) : null;

  const valueFieldId = `condition-${label}-value`;
  const valueLabel = `Value for condition ${label}`;

  const renderValueControl = () => {
    if (condition.op === 'IsNull') return null;

    if (condition.op === 'In') {
      const values = condition.values ?? [];
      const atLimit = values.length >= catalog.maxInValues;
      return (
        <div className="flex flex-col gap-2">
          <div className="flex items-end gap-2">
            <Field label={valueLabel} id={valueFieldId}>
              <input
                type="text"
                className={INPUT_CLASS}
                value={inDraft}
                onChange={(e) => setInDraft(e.target.value)}
              />
            </Field>
            <Button type="button" variant="secondary" onClick={handleAddInValue} disabled={atLimit}>
              Add value
            </Button>
          </div>
          {atLimit && (
            <p className="text-xs text-[var(--ju-muted)]">
              You can add at most {catalog.maxInValues} values.
            </p>
          )}
          {values.length > 0 && (
            <div className="flex flex-wrap gap-1">
              {values.map((v, i) => (
                <Badge key={`${v}-${i}`}>
                  {v}
                  <button
                    type="button"
                    aria-label={`Remove ${v}`}
                    className="ml-1"
                    onClick={() => handleRemoveInValue(i)}
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

    if (pickable) {
      return (
        <Field label={valueLabel} id={valueFieldId}>
          <Select
            id={valueFieldId}
            value={(condition.value as string | undefined) ?? ''}
            onChange={handlePickedChange}
          >
            <option value="">— choose —</option>
            {pickable.map((v) => (
              <option key={v} value={v}>{v}</option>
            ))}
          </Select>
        </Field>
      );
    }

    if (fieldType === 'Decimal') {
      return (
        <Field label={valueLabel} id={valueFieldId}>
          <input
            type="number"
            className={INPUT_CLASS}
            value={condition.value ?? ''}
            onChange={handleNumberChange}
          />
        </Field>
      );
    }

    if (fieldType === 'DateTime') {
      return (
        <Field label={valueLabel} id={valueFieldId}>
          <input
            type="date"
            className={INPUT_CLASS}
            value={(condition.value as string | undefined) ?? ''}
            onChange={handleTextOrDateChange}
          />
        </Field>
      );
    }

    return (
      <Field label={valueLabel} id={valueFieldId}>
        <input
          type="text"
          className={INPUT_CLASS}
          value={(condition.value as string | undefined) ?? ''}
          onChange={handleTextOrDateChange}
        />
      </Field>
    );
  };

  return (
    <div className="flex flex-wrap items-end gap-2 rounded-md border border-[var(--ju-border)] p-2">
      <Field label={`Field for condition ${label}`} id={`condition-${label}-field`}>
        <Select value={condition.field} onChange={handleFieldChange}>
          {fieldNames(catalog).map((f) => (
            <option key={f} value={f}>
              {f}
            </option>
          ))}
        </Select>
      </Field>

      <Field label={`Operator for condition ${label}`} id={`condition-${label}-operator`}>
        <Select value={condition.op} onChange={handleOperatorChange}>
          {ops.map((op) => (
            <option key={op} value={op}>
              {op}
            </option>
          ))}
        </Select>
      </Field>

      {renderValueControl()}
    </div>
  );
}
