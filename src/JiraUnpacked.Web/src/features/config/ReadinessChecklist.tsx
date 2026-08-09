import { Link } from 'react-router-dom';
import { Badge, Card } from '../../ui';
import type { Readiness } from './readiness';

export type ReadinessTab = 'fields' | 'statuses';

interface ReadinessChecklistProps {
  readiness: Readiness;
  connectionId: string;
  onSelectTab: (tab: ReadinessTab) => void;
}

// Never disables anything — re-syncing a deliberately partially-configured
// project is legitimate, so this checklist only informs.
export function ReadinessChecklist({ readiness, connectionId, onSelectTab }: ReadinessChecklistProps) {
  if (readiness.allReady) {
    return (
      <Card>
        <p className="flex items-center gap-2 text-sm text-[var(--ju-text)]">
          <Badge tone="success">Ready</Badge>
          Setup is complete — field mappings, status semantics, and the Epic issue type are all configured.
        </p>
      </Card>
    );
  }

  return (
    <Card className="flex flex-col gap-3">
      <h2 className="text-sm font-semibold text-[var(--ju-text)]">Setup checklist</h2>
      <ul className="flex flex-col gap-3">
        <li className="flex items-start gap-2">
          <Badge tone={readiness.fieldMappings.ready ? 'success' : 'warning'}>
            {readiness.fieldMappings.ready ? 'Ready' : 'Incomplete'}
          </Badge>
          {readiness.fieldMappings.ready ? (
            <p className="text-sm text-[var(--ju-text)]">Field mappings are configured.</p>
          ) : (
            <p className="text-sm text-[var(--ju-text)]">
              No fields mapped — team, assignee, sprint, and story points will stay empty.{' '}
              <button type="button" className="text-[var(--ju-primary)] underline" onClick={() => onSelectTab('fields')}>
                Go to Fields
              </button>
            </p>
          )}
        </li>

        <li className="flex items-start gap-2">
          <Badge tone={readiness.statusSemantics.ready ? 'success' : 'warning'}>
            {readiness.statusSemantics.ready ? 'Ready' : 'Incomplete'}
          </Badge>
          {readiness.statusSemantics.ready ? (
            <p className="text-sm text-[var(--ju-text)]">All statuses are classified.</p>
          ) : (
            <p className="text-sm text-[var(--ju-text)]">
              {readiness.statusSemantics.missingStatusNames.length} statuses unclassified:{' '}
              {readiness.statusSemantics.missingStatusNames.join(', ')} — cycle time and throughput
              cannot be computed until every status has a class.{' '}
              <button type="button" className="text-[var(--ju-primary)] underline" onClick={() => onSelectTab('statuses')}>
                Go to Statuses
              </button>
            </p>
          )}
        </li>

        <li className="flex items-start gap-2">
          <Badge tone={readiness.issueTypes.ready ? 'success' : 'warning'}>
            {readiness.issueTypes.ready ? 'Ready' : 'Incomplete'}
          </Badge>
          {readiness.issueTypes.ready ? (
            <p className="text-sm text-[var(--ju-text)]">An Epic issue type is configured.</p>
          ) : (
            <p className="text-sm text-[var(--ju-text)]">
              No issue type is classed Epic — epic gap-checks will report not configured.{' '}
              <Link className="text-[var(--ju-primary)] underline" to={`/connections/${connectionId}`}>
                Go to issue types
              </Link>
            </p>
          )}
        </li>
      </ul>
    </Card>
  );
}
