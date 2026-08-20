import { useState, type ChangeEvent } from 'react';
import type { FilterCondition, FilterOperator } from '../../api/issues';
import { FILTER_FIELDS, MAX_IN_VALUES, operatorsFor, resetOperatorIfDisallowed, typeFor } from './filterCatalog';
import { Badge, Button, Field, Select } from '../../ui';

const INPUT_CLASS =
  'rounded-md border border-[var(--ju-border)] bg-[var(--ju-bg)] px-3 py-2 text-sm text-[var(--ju-text)] ' +
  'focus:outline-none focus:ring-2 focus:ring-[var(--ju-primary)] disabled:cursor-not-allowed disabled:opacity-60';

interface ConditionRowProps {
  condition: FilterCondition;
  onChange: (next: FilterCondition) => void;
  onRemove: () => void;
  index: number;
}

/**
 * One field/operator/value row. Every transition that could leave a stale
 * value/values key sitting alongside a new field or operator instead
 * rebuilds the condition from scratch — see the module doc on FilterCondition
 * in ../../api/issues for why a leftover key of the wrong shape is a runtime
 * 400 that nothing here would otherwise catch.
 */
export function ConditionRow({ condition, onChange, onRemove, index }: ConditionRowProps) {
  // Draft text for the "In" multi-value editor's add box. Local because it
  // is not part of the submitted condition until "Add value" commits it.
  const [inDraft, setInDraft] = useState('');

  // FilterBuilder keys rows by array index, so removing a row shifts index
  // identity onto a different condition — this component instance stays
  // mounted but starts receiving a different `condition` object. Without
  // this reset, `inDraft` (which is not part of `condition`) would survive
  // that shift and appear to belong to the condition that took its place.
  // Comparing against the previous condition during render (rather than in
  // an effect) clears it in the same render that shows the new condition,
  // before anything paints. Condition objects are only re-created on real
  // edits, so this does not cause spurious resets.
  const [prev, setPrev] = useState(condition);
  if (prev !== condition) {
    setPrev(condition);
    setInDraft('');
  }

  const fieldType = typeFor(condition.field);
  const ops = operatorsFor(condition.field);
  const rowNumber = index + 1;

  const handleFieldChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const nextField = event.target.value;
    // A value valid for the old field's type rarely is for the new one, so
    // it — and any `values` array — is dropped entirely, not carried over.
    onChange({ field: nextField, op: resetOperatorIfDisallowed(nextField, condition.op) });
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

  const handleNumberChange = (event: ChangeEvent<HTMLInputElement>) => {
    const raw = event.target.value;
    onChange({ field: condition.field, op: condition.op, value: raw === '' ? '' : Number(raw) });
  };

  const handleAddInValue = () => {
    const trimmed = inDraft.trim();
    const current = condition.values ?? [];
    if (!trimmed || current.length >= MAX_IN_VALUES) return;
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

  const valueFieldId = `condition-${index}-value`;
  const valueLabel = `Value for condition ${rowNumber}`;

  const renderValueControl = () => {
    if (condition.op === 'IsNull') return null;

    if (condition.op === 'In') {
      const values = condition.values ?? [];
      const atLimit = values.length >= MAX_IN_VALUES;
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
              You can add at most {MAX_IN_VALUES} values.
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
      <Field label={`Field for condition ${rowNumber}`} id={`condition-${index}-field`}>
        <Select value={condition.field} onChange={handleFieldChange}>
          {FILTER_FIELDS.map((f) => (
            <option key={f} value={f}>
              {f}
            </option>
          ))}
        </Select>
      </Field>

      <Field label={`Operator for condition ${rowNumber}`} id={`condition-${index}-operator`}>
        <Select value={condition.op} onChange={handleOperatorChange}>
          {ops.map((op) => (
            <option key={op} value={op}>
              {op}
            </option>
          ))}
        </Select>
      </Field>

      {renderValueControl()}

      <Button type="button" variant="secondary" onClick={onRemove}>
        Remove
      </Button>
    </div>
  );
}
