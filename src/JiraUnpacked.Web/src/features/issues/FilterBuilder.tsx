import { useQuery } from '@tanstack/react-query';
import type { FilterCondition, FilterLogic } from '../../api/issues';
import { getFilterCatalog } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { ApiError } from '../../api/errors';
import { Button, ErrorState, Field, Select, Spinner } from '../../ui';
import { ConditionRow } from './ConditionRow';
import { blankCondition, emptyRoot, isEmptyTree, withIdentityOf, type EditorGroup } from './filterTree';

/**
 * The server's filter-field allow-list and guards. Static and tenant-independent, hence
 * `staleTime: Infinity`: fetched once per page load and shared through the query cache by every
 * caller (FilterBuilder renders from it; IssueList prunes retired fields with it).
 */
export function useFilterCatalog() {
  return useQuery({
    queryKey: queryKeys.filterCatalog(),
    queryFn: getFilterCatalog,
    staleTime: Infinity,
  });
}

interface FilterBuilderProps {
  /** Needed only to fetch this project's filter values; the builder holds no other project state. */
  projectId: string;
  tree: EditorGroup;
  onChange: (next: EditorGroup) => void;
}

/**
 * Holds no state of its own — every change flows up through `onChange`, so the caller
 * (IssueList, via the URL) is the single owner of the tree that drives its query.
 */
export function FilterBuilder({ projectId, tree, onChange }: FilterBuilderProps) {
  const { data: catalog, isPending, error } = useFilterCatalog();

  if (isPending) return <Spinner aria-label="Loading filter catalog" />;
  if (error) {
    return (
      <ErrorState
        title={error instanceof ApiError ? error.title : 'Could not load the filter catalog.'}
      />
    );
  }

  const conditions = tree.conditions;
  const atConditionLimit = conditions.length >= catalog.maxConditions;

  const handleAdd = () => {
    if (atConditionLimit) return;
    onChange({ ...tree, conditions: [...conditions, blankCondition()] });
  };

  const handleRowChange = (id: string, next: FilterCondition) =>
    onChange({ ...tree, conditions: conditions.map((c) => (c.id === id ? withIdentityOf(c, next) : c)) });

  const handleRemove = (id: string) =>
    onChange({ ...tree, conditions: conditions.filter((c) => c.id !== id) });

  if (isEmptyTree(tree)) {
    return (
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm text-[var(--ju-muted)]">No filter — showing all issues</p>
        <Button type="button" variant="secondary" onClick={handleAdd} disabled={atConditionLimit}>
          Add condition
        </Button>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <Field label="Match" id="filter-logic" hint="All: every condition must hold. Any: at least one.">
        <Select
          id="filter-logic"
          value={tree.logic}
          onChange={(e) => onChange({ ...tree, logic: e.target.value as FilterLogic })}
        >
          <option value="And">All conditions</option>
          <option value="Or">Any condition</option>
        </Select>
      </Field>

      {conditions.map((condition, index) => (
        <ConditionRow
          key={condition.id}
          index={index}
          projectId={projectId}
          condition={condition}
          catalog={catalog}
          onChange={(next) => handleRowChange(condition.id, next)}
          onRemove={() => handleRemove(condition.id)}
        />
      ))}

      <div className="flex items-center gap-2">
        <Button type="button" variant="secondary" onClick={handleAdd} disabled={atConditionLimit}>
          Add condition
        </Button>
        <Button type="button" variant="secondary" onClick={() => onChange(emptyRoot())}>
          Clear all
        </Button>
      </div>

      {atConditionLimit && (
        <p className="text-xs text-[var(--ju-muted)]">
          You can add at most {catalog.maxConditions} conditions.
        </p>
      )}
    </div>
  );
}
