import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Badge, Button, ErrorState, Field, Select, Spinner, Table, TBody, TD, TH, THead, TR } from '../../ui';
import { ApiError } from '../../api/errors';
import {
  getStatusSemantics,
  listJiraStatuses,
  putStatusSemantics,
  STATUS_CLASSES,
  type StatusClass,
  type StatusSemantic,
} from '../../api/mappings';
import { queryKeys } from '../../api/queryKeys';

export function StatusSemanticsEditor({ projectId }: { projectId: string }) {
  const queryClient = useQueryClient();
  // Live from Jira, so this can 502 — handled below via ApiError.title
  // rather than a generic failure message.
  const statuses = useQuery({
    queryKey: queryKeys.jiraStatuses(projectId),
    queryFn: () => listJiraStatuses(projectId),
  });
  const saved = useQuery({
    queryKey: queryKeys.statusSemantics(projectId),
    queryFn: () => getStatusSemantics(projectId),
  });

  // '' means "not classified yet" — a state the API has no representation
  // for, so it must exist only in local state and be filtered out before
  // the PUT.
  const [chosen, setChosen] = useState<Record<string, StatusClass | ''>>({});

  const current = (statusId: string): StatusClass | '' =>
    chosen[statusId] ?? saved.data?.find((s) => s.jiraStatusId === statusId)?.class ?? '';

  // Replace-all: send every classified status, including ones untouched
  // this session. Sending only edits would delete the rest server-side.
  // Hooks must run unconditionally on every render (Rules of Hooks), so this
  // is declared before the loading/error returns below, even though it can
  // only be invoked once the section actually renders the Save button.
  const save = useMutation({
    mutationFn: () => putStatusSemantics(
      projectId,
      (statuses.data ?? [])
        .map((s) => ({ jiraStatusId: s.id, statusName: s.name, class: current(s.id) }))
        .filter((s): s is StatusSemantic => s.class !== ''),
    ),
    onSuccess: () => {
      setChosen({});
      void queryClient.invalidateQueries({ queryKey: queryKeys.statusSemantics(projectId) });
    },
  });

  if (statuses.isPending || saved.isPending) return <Spinner aria-label="Loading status semantics" />;
  if (statuses.error) {
    return (
      <ErrorState
        title={statuses.error instanceof ApiError ? statuses.error.title : 'Could not load statuses.'}
      />
    );
  }
  if (saved.error) {
    return (
      <ErrorState
        title={saved.error instanceof ApiError ? saved.error.title : 'Could not load status semantics.'}
      />
    );
  }

  return (
    <section>
      <h2>Status semantics</h2>
      <p className="text-sm text-[var(--ju-muted)]">
        Every status needs a class — otherwise cycle-time and throughput cannot be computed.
      </p>
      {save.error && (
        <p role="alert">
          {save.error instanceof ApiError ? save.error.title : 'Could not save status semantics.'}
        </p>
      )}
      <Table>
        <THead>
          <TR>
            <TH>Status</TH>
            <TH>Class</TH>
          </TR>
        </THead>
        <TBody>
          {statuses.data.map((status) => {
            const value = current(status.id);
            return (
              <TR key={status.id}>
                <TD>
                  <span className="mr-2">{status.name}</span>
                  {value === '' && <Badge tone="warning">Unclassified</Badge>}
                </TD>
                <TD>
                  <Field label={`Class for ${status.name}`} id={`ss-${status.id}`}>
                    <Select
                      id={`ss-${status.id}`}
                      value={value}
                      onChange={(e) =>
                        setChosen((p) => ({ ...p, [status.id]: e.target.value as StatusClass | '' }))}
                    >
                      <option value="">— not classified —</option>
                      {STATUS_CLASSES.map((c) => (
                        <option key={c} value={c}>{c}</option>
                      ))}
                    </Select>
                  </Field>
                </TD>
              </TR>
            );
          })}
        </TBody>
      </Table>
      <Button loading={save.isPending} onClick={() => save.mutate()}>Save status semantics</Button>
    </section>
  );
}
