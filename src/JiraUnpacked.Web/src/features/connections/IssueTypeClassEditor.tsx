import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, ErrorState, Field, Select, Spinner, TBody, TD, TH, THead, TR, Table } from '../../ui';
import { ApiError } from '../../api/errors';
import { getIssueTypes, putIssueTypes, type IssueTypeClass } from '../../api/mappings';
import { queryKeys } from '../../api/queryKeys';

export function IssueTypeClassEditor({ connectionId }: { connectionId: string }) {
  const queryClient = useQueryClient();
  const { data, isPending, error } = useQuery({
    queryKey: queryKeys.issueTypes(connectionId),
    queryFn: () => getIssueTypes(connectionId),
  });

  // Local edits keyed by issue-type id; the submit merges them back over the
  // full fetched list so every row is sent, not only the edited ones. Sends
  // the complete set for consistency with the replace-all editors. This
  // endpoint is additive — unlisted types keep their class — so omitting
  // rows would be safe, but the whole batch is validated, so every id must
  // be valid.
  const [edits, setEdits] = useState<Record<string, IssueTypeClass>>({});

  const save = useMutation({
    mutationFn: () =>
      putIssueTypes(
        connectionId,
        (data ?? []).map((it) => ({
          jiraIssueTypeId: it.jiraIssueTypeId,
          class: edits[it.jiraIssueTypeId] ?? it.class,
        })),
      ),
    onSuccess: () => {
      setEdits({});
      void queryClient.invalidateQueries({ queryKey: queryKeys.issueTypes(connectionId) });
    },
  });

  if (isPending) return <Spinner aria-label="Loading issue types" />;
  if (error) return <ErrorState title={error instanceof ApiError ? error.title : 'Could not load issue types.'} />;

  return (
    <section>
      <h2>Issue types</h2>
      <p>Classify which issue type represents an epic. Epic gap-checks cannot run without it.</p>
      {save.error && (
        <p role="alert">{save.error instanceof ApiError ? save.error.title : 'Could not save issue types.'}</p>
      )}
      <Table>
        <THead><TR><TH>Issue type</TH><TH>Class</TH></TR></THead>
        <TBody>
          {data.map((it) => (
            <TR key={it.jiraIssueTypeId}>
              <TD>{it.name}</TD>
              <TD>
                <Field label={`Class for ${it.name}`} id={`itc-${it.jiraIssueTypeId}`}>
                  <Select
                    id={`itc-${it.jiraIssueTypeId}`}
                    value={edits[it.jiraIssueTypeId] ?? it.class}
                    onChange={(e) =>
                      setEdits((p) => ({ ...p, [it.jiraIssueTypeId]: e.target.value as IssueTypeClass }))}
                  >
                    <option value="Other">Other</option>
                    <option value="Epic">Epic</option>
                  </Select>
                </Field>
              </TD>
            </TR>
          ))}
        </TBody>
      </Table>
      <Button loading={save.isPending} onClick={() => save.mutate()}>Save issue types</Button>
    </section>
  );
}
