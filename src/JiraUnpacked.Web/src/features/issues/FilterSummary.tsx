import { Button } from '../../ui';

interface FilterSummaryProps {
  /** describeFilter of what the list is queried with. */
  sentence: string;
  /** The result count, when known for THIS filter (not a placeholder's). */
  count?: number;
  expanded: boolean;
  /** The id of the builder's wrapper, which the toggle opens and closes. */
  controls: string;
  onToggle: () => void;
}

/**
 * "Showing: <sentence> · N issues" above the builder, with the Collapse / Edit filter toggle.
 * Holds no state: whether the builder is open belongs to IssueList (component state, never the
 * URL or storage — see its collapse rule).
 */
export function FilterSummary({ sentence, count, expanded, controls, onToggle }: FilterSummaryProps) {
  return (
    <section aria-label="Filter summary" className="flex items-start gap-3">
      <p className="flex-1 text-sm text-[var(--ju-text)]">
        <span className="font-medium">Showing:</span> {sentence}
        {count !== undefined && (
          <span className="text-[var(--ju-muted)]"> · {count} {count === 1 ? 'issue' : 'issues'}</span>
        )}
      </p>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        aria-expanded={expanded}
        aria-controls={expanded ? controls : undefined}
        onClick={onToggle}
      >
        {expanded ? 'Collapse' : 'Edit filter'}
      </Button>
    </section>
  );
}
