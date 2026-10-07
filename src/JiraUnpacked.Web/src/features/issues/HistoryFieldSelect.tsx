import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getHistoryFields } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Field, Select } from '../../ui';
import { MAX_HISTORY_FIELD_LENGTH } from './historyShapes';
import { INPUT_CLASS } from './styles';

interface HistoryFieldSelectProps {
  projectId: string;
  /** Position in the tree, e.g. "1" or "2.1" — part of the accessible name. */
  label: string;
  /** The field's name as Jira reports it; '' = none chosen yet. */
  value: string;
  onChange: (next: string) => void;
}

/**
 * Which field's history: the fields this project's changelog has, most-changed first, each with
 * its count. The list is an affordance, not a gate — while it loads, or if it fails, the name is
 * typed, exactly as Jira reports it.
 */
export function HistoryFieldSelect({ projectId, label, value, onChange }: HistoryFieldSelectProps) {
  const fields = useQuery({
    queryKey: queryKeys.historyFields(projectId),
    queryFn: () => getHistoryFields(projectId),
    // A project's changelog changes only when a sync runs.
    staleTime: 5 * 60 * 1000,
  });
  // The list arriving must not pull the input out from under someone typing: the swap to the
  // select waits until the input is left.
  const [typing, setTyping] = useState(false);
  const id = `condition-${label}-history-field`;
  const name = `History field for condition ${label}`;

  if (!fields.data || typing) {
    return (
      <Field
        label={name}
        id={id}
        hint={fields.isError && !fields.data ? 'Field list unavailable — type the name as Jira reports it' : undefined}
      >
        <input
          type="text"
          className={INPUT_CLASS}
          maxLength={MAX_HISTORY_FIELD_LENGTH}
          onFocus={() => setTyping(true)}
          onBlur={() => setTyping(false)}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
      </Field>
    );
  }

  const options = fields.data.fields;
  // A field from a link that this project's changelog does not have is shown as what it is.
  const stale = value !== '' && !options.some((o) => o.name === value);
  return (
    <Field label={name} id={id}>
      <Select value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">— choose —</option>
        {stale && <option value={value}>{`${value} (0)`}</option>}
        {options.map((o) => (
          <option key={o.name} value={o.name}>{`${o.name} (${o.count})`}</option>
        ))}
      </Select>
    </Field>
  );
}
