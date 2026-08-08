import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, ErrorState, Field, Select, Spinner } from '../../ui';
import { ApiError } from '../../api/errors';
import { listConnectionFields } from '../../api/connections';
import { FIELD_ROLES, getFieldMappings, putFieldMappings, type FieldRole } from '../../api/mappings';
import { queryKeys } from '../../api/queryKeys';

export function FieldMappingsEditor({ projectId, connectionId }: { projectId: string; connectionId: string }) {
  const queryClient = useQueryClient();
  const fields = useQuery({
    queryKey: queryKeys.connectionFields(connectionId),
    queryFn: () => listConnectionFields(connectionId),
  });
  const saved = useQuery({
    queryKey: queryKeys.fieldMappings(projectId),
    queryFn: () => getFieldMappings(projectId),
  });

  const [filter, setFilter] = useState('');
  // Local edits keyed by role; the submit merges them back over the full
  // saved set so untouched roles are still sent (the PUT is replace-all).
  const [chosen, setChosen] = useState<Partial<Record<FieldRole, string>>>({});

  const current = (role: FieldRole) =>
    chosen[role] ?? saved.data?.find((m) => m.role === role)?.jiraFieldId ?? '';

  // Replace-all: send every role that has a field, including ones untouched
  // this session. Sending only edits would delete the rest server-side.
  // Hooks must run unconditionally on every render (Rules of Hooks), so this
  // is declared before the loading/error returns below, even though it can
  // only be invoked once the section actually renders the Save button.
  const save = useMutation({
    mutationFn: () => putFieldMappings(
      projectId,
      FIELD_ROLES
        .map((role) => ({ role, jiraFieldId: current(role) }))
        .filter((m) => m.jiraFieldId !== ''),
    ),
    onSuccess: () => {
      setChosen({});
      void queryClient.invalidateQueries({ queryKey: queryKeys.fieldMappings(projectId) });
    },
  });

  if (fields.isPending || saved.isPending) return <Spinner aria-label="Loading field mappings" />;
  if (fields.error || saved.error) return <ErrorState title="Could not load fields." />;

  const visible = fields.data.filter((f) =>
    f.name.toLowerCase().includes(filter.trim().toLowerCase()));

  return (
    <section>
      <h2>Field mappings</h2>
      {save.error && (
        <p role="alert">
          {save.error instanceof ApiError ? save.error.title : 'Could not save field mappings.'}
        </p>
      )}
      <Field label="Filter fields" id="field-filter" hint="A Jira site can expose hundreds of fields.">
        <input id="field-filter" value={filter} onChange={(e) => setFilter(e.target.value)} />
      </Field>
      {FIELD_ROLES.map((role) => (
        <Field key={role} label={role} id={`fm-${role}`}>
          <Select
            id={`fm-${role}`}
            value={current(role)}
            onChange={(e) => setChosen((p) => ({ ...p, [role]: e.target.value }))}
          >
            <option value="">— not mapped —</option>
            {visible.map((f) => (
              <option key={f.id} value={f.id}>{f.name}</option>
            ))}
          </Select>
        </Field>
      ))}
      <Button loading={save.isPending} onClick={() => save.mutate()}>Save field mappings</Button>
    </section>
  );
}
