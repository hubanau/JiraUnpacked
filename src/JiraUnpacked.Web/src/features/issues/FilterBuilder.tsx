import { useQuery } from '@tanstack/react-query';
import { getFilterCatalog } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { ApiError } from '../../api/errors';
import { Button, ErrorState, Spinner } from '../../ui';
import { GroupEditor } from './GroupEditor';
import { blankCondition, blankGroup, countEnabled, emptyRoot, isEmptyTree, type EditorGroup } from './filterTree';

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

  if (isEmptyTree(tree)) {
    return (
      <div className="flex items-center justify-between gap-4">
        <p className="text-sm text-[var(--ju-muted)]">No filter — showing all issues</p>
        <div className="flex gap-2">
          <Button type="button" variant="secondary" onClick={() => onChange({ ...tree, conditions: [blankCondition()] })}>
            Add condition
          </Button>
          {catalog.maxDepth > 1 && (
            <Button type="button" variant="secondary" onClick={() => onChange({ ...tree, groups: [blankGroup()] })}>
              Add group
            </Button>
          )}
        </div>
      </div>
    );
  }

  const counts = countEnabled(tree);

  return (
    <div className="flex flex-col gap-3">
      <GroupEditor
        group={tree}
        onChange={onChange}
        catalog={catalog}
        projectId={projectId}
        counts={counts}
        depth={1}
        label=""
      />
      <div>
        <Button type="button" variant="secondary" size="sm" onClick={() => onChange(emptyRoot())}>
          Clear all
        </Button>
      </div>
      {counts.conditions >= catalog.maxConditions && (
        <p className="text-xs text-[var(--ju-muted)]">You can add at most {catalog.maxConditions} conditions.</p>
      )}
      {counts.groups >= catalog.maxGroups && (
        <p className="text-xs text-[var(--ju-muted)]">You can add at most {catalog.maxGroups} groups.</p>
      )}
    </div>
  );
}
