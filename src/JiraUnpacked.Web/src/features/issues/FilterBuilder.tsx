import { useQuery } from '@tanstack/react-query';
import type { FilterCondition } from '../../api/issues';
import { getFilterCatalog } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { ApiError } from '../../api/errors';
import { Button, ErrorState, Spinner } from '../../ui';
import { ConditionRow } from './ConditionRow';
import { isDictionaryField } from './filterCatalogHelpers';

interface FilterBuilderProps {
  conditions: FilterCondition[];
  onChange: (next: FilterCondition[]) => void;
}

/**
 * FR-E4 defines `Neq` on a dictionary field to also match issues where the
 * field is empty (`status != Done` includes issues with no status). That
 * reading is deliberate but not the only plausible one, so the UI states it
 * rather than letting a user infer the wrong thing from silence (Spec §4).
 */
const NEQ_DICT_NOTE =
  "Neq on status, type, priority, assignee, sprint, or team also matches issues where the field is empty.";

/**
 * Holds no server state of its own — every change flows up through
 * `onChange` so the caller (IssueList) remains the single owner of the
 * conditions that drive its query.
 *
 * The field/operator/type allow-list is fetched from GET /api/filter-catalog
 * rather than mirrored client-side (see filterCatalogHelpers.ts) — the
 * server's FilterFields.All is the sole authority, so drift between what
 * this UI offers and what the server accepts is impossible, not just tested.
 */
export function FilterBuilder({ conditions, onChange }: FilterBuilderProps) {
  const { data: catalog, isPending, error } = useQuery({
    queryKey: queryKeys.filterCatalog(),
    queryFn: getFilterCatalog,
  });

  if (isPending) return <Spinner aria-label="Loading filter catalog" />;
  if (error) {
    return (
      <ErrorState
        title={error instanceof ApiError ? error.title : 'Could not load the filter catalog.'}
      />
    );
  }

  const atConditionLimit = conditions.length >= catalog.maxConditions;

  const handleAdd = () => {
    if (atConditionLimit) return;
    onChange([...conditions, { field: 'status', op: 'Eq', value: '' }]);
  };

  const handleRowChange = (index: number, next: FilterCondition) => {
    onChange(conditions.map((c, i) => (i === index ? next : c)));
  };

  const handleRemove = (index: number) => {
    onChange(conditions.filter((_, i) => i !== index));
  };

  const handleClearAll = () => onChange([]);

  if (conditions.length === 0) {
    return (
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm text-[var(--ju-muted)]">No filter — showing all issues</p>
        <Button type="button" variant="secondary" onClick={handleAdd} disabled={atConditionLimit}>
          Add condition
        </Button>
      </div>
    );
  }

  const showsNeqDictNote = conditions.some((c) => c.op === 'Neq' && isDictionaryField(catalog, c.field));

  return (
    <div className="flex flex-col gap-3">
      {conditions.map((condition, index) => (
        <ConditionRow
          key={index}
          index={index}
          condition={condition}
          catalog={catalog}
          onChange={(next) => handleRowChange(index, next)}
          onRemove={() => handleRemove(index)}
        />
      ))}

      <div className="flex items-center gap-2">
        <Button type="button" variant="secondary" onClick={handleAdd} disabled={atConditionLimit}>
          Add condition
        </Button>
        <Button type="button" variant="secondary" onClick={handleClearAll}>
          Clear all
        </Button>
      </div>

      {atConditionLimit && (
        <p className="text-xs text-[var(--ju-muted)]">
          You can add at most {catalog.maxConditions} conditions.
        </p>
      )}

      {showsNeqDictNote && <p className="text-xs text-[var(--ju-muted)]">{NEQ_DICT_NOTE}</p>}
    </div>
  );
}
