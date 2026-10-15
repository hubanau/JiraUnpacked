import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { FilterOperator, HistoryCompare, HistoryValue, HistoryValueKind, ListValue } from '../../api/issues';
import { getHistoryValues } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Field, Select } from '../../ui';
import { ChipInput } from './ChipInput';
import { DuringControl } from './DuringControl';
import { HistoryFieldSelect } from './HistoryFieldSelect';
import {
  HISTORY_COMPARES, MAX_CHANGE_COUNT, MAX_HISTORY_VALUES, canonicalHistoryValue, freshHistoryValue,
} from './historyShapes';
import { INPUT_CLASS } from './styles';
import { MultiValuePicker } from './ValuePicker';

/** A name, or (Empty). */
type Name = string | null;
const isName = (v: ListValue): v is Name => v === null || typeof v === 'string';

interface HistoryListProps {
  projectId: string;
  historyField: string;
  kind: HistoryValueKind;
  /** Accessible name, e.g. "Values for condition 1". */
  name: string;
  id: string;
  selected: Name[];
  /** (Empty) on offer: for values yes, for authors no. */
  allowEmpty: boolean;
  onChange: (next: Name[]) => void;
}

/**
 * A multi-value list fed by history-values. Typed chips while it loads, if it fails, or before a
 * field is chosen — the list is an affordance, not a gate.
 */
function HistoryList({ projectId, historyField, kind, name, id, selected, allowEmpty, onChange }: HistoryListProps) {
  const list = useQuery({
    queryKey: queryKeys.historyValues(projectId, historyField, kind),
    queryFn: () => getHistoryValues(projectId, historyField, kind),
    enabled: historyField.trim() !== '',
    staleTime: 5 * 60 * 1000,
  });
  // As in HistoryFieldSelect: the list arriving must not pull the typed chips out from under
  // someone typing (and lose the draft); the swap waits until focus leaves the chip input.
  const [typing, setTyping] = useState(false);
  const max = MAX_HISTORY_VALUES;
  if (list.data && !typing) {
    return (
      <MultiValuePicker
        label={name}
        options={list.data.values}
        emptyCount={list.data.emptyCount}
        allowEmpty={allowEmpty}
        selected={selected}
        max={max}
        onChange={(next) => onChange(next.filter(isName))}
      />
    );
  }
  // Typing cannot produce (Empty), but one that arrived in a link is kept, not silently lost.
  const kept = selected.filter((v) => v === null);
  return (
    <div
      className="contents"
      onFocus={() => setTyping(true)}
      onBlur={(e) => {
        // Moving between the input, "Add value" and the chips is still typing.
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setTyping(false);
      }}
    >
      <ChipInput
        key={historyField}
        id={id}
        label={name}
        values={selected.filter((v): v is string => v !== null)}
        max={max - kept.length}
        onChange={(next) => onChange([...kept, ...next])}
      />
    </div>
  );
}

interface HistoryConditionControlsProps {
  projectId: string;
  /** Position in the tree, e.g. "1" or "2.1" — part of every control's accessible name. */
  label: string;
  op: FilterOperator;
  /** Undefined = nothing readable yet: the controls start from the operator's fresh value. */
  value: HistoryValue | undefined;
  onChange: (next: HistoryValue) => void;
}

/**
 * The second line of a history row: the History field, then what the operator takes — the
 * values it was; from / to / by / during; or compare, count and during. Every change leaves in
 * the operator's canonical shape, so a link or a saved view never differs from what the editor
 * itself would write.
 */
export function HistoryConditionControls({ projectId, label, op, value, onChange }: HistoryConditionControlsProps) {
  const v = value ?? freshHistoryValue(op, '');
  const set = (patch: Partial<HistoryValue>) => onChange(canonicalHistoryValue(op, { ...v, ...patch }));
  const setList = (key: 'values' | 'from' | 'to' | 'by', next: Name[]) => {
    const patch: Partial<HistoryValue> = {};
    if (key === 'by') patch.by = next.filter((x): x is string => x !== null);
    else patch[key] = next;
    set(patch);
  };
  // Another field's history holds other values: "status was Blocked" must never quietly become
  // "priority was Blocked". The lists go; the window, compare and count stay.
  const setField = (historyField: string, replaced: boolean) =>
    set(replaced ? { historyField, values: [], from: undefined, to: undefined, by: undefined } : { historyField });
  const list = (key: 'values' | 'from' | 'to' | 'by', kind: HistoryValueKind, name: string, allowEmpty: boolean) => (
    <HistoryList
      projectId={projectId}
      historyField={v.historyField}
      kind={kind}
      name={name}
      id={`condition-${label}-${key}`}
      selected={v[key] ?? []}
      allowEmpty={allowEmpty}
      onChange={(next) => setList(key, next)}
    />
  );
  const during = <DuringControl label={label} value={v.during} onChange={(next) => set({ during: next })} />;

  return (
    <div className="flex basis-full flex-wrap items-end gap-2">
      <HistoryFieldSelect projectId={projectId} label={label} value={v.historyField} onChange={setField} />

      {op === 'Was' && list('values', 'value', `Values for condition ${label}`, true)}

      {op === 'Changed' && (
        <>
          {list('from', 'value', `From values for condition ${label}`, true)}
          {list('to', 'value', `To values for condition ${label}`, true)}
          {list('by', 'author', `Authors for condition ${label}`, false)}
          {during}
        </>
      )}

      {op === 'ChangeCount' && (
        <>
          <Field label={`Compare for condition ${label}`} id={`condition-${label}-compare`}>
            <Select value={v.compare ?? ''} onChange={(e) => set({ compare: e.target.value as HistoryCompare })}>
              {v.compare === undefined && <option value="">— choose —</option>}
              {HISTORY_COMPARES.map((c) => (
                <option key={c.key} value={c.key}>{c.label}</option>
              ))}
            </Select>
          </Field>
          <Field label={`Count for condition ${label}`} id={`condition-${label}-count`}>
            <input
              type="number"
              min={0}
              max={MAX_CHANGE_COUNT}
              step={1}
              className={INPUT_CLASS}
              value={v.count ?? ''}
              // Blank is "not entered yet": the key goes, and the row is unfinished until a number is typed.
              onChange={(e) => set({ count: e.target.value === '' ? undefined : Math.trunc(Number(e.target.value)) })}
            />
          </Field>
          {during}
        </>
      )}
    </div>
  );
}
