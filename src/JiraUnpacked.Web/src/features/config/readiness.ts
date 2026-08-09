import type { FieldMapping, IssueTypeClassInfo, JiraStatus, StatusSemantic } from '../../api/mappings';

export interface ReadinessInput {
  fieldMappings: FieldMapping[];
  jiraStatuses: JiraStatus[];
  statusSemantics: StatusSemantic[];
  issueTypes: IssueTypeClassInfo[];
}

export interface ReadinessStep { ready: boolean }
export interface StatusStep extends ReadinessStep { missingStatusNames: string[] }

export interface Readiness {
  fieldMappings: ReadinessStep;
  statusSemantics: StatusStep;
  issueTypes: ReadinessStep;
  allReady: boolean;
}

export function deriveReadiness(input: ReadinessInput): Readiness {
  const classified = new Set(input.statusSemantics.map((s) => s.jiraStatusId));
  // Every status Jira currently reports must be classified. Partial coverage
  // is NOT ready: cycle-time and throughput silently exclude the unclassified
  // ones, which looks like real data rather than a configuration gap.
  const missingStatusNames = input.jiraStatuses
    .filter((s) => !classified.has(s.id))
    .map((s) => s.name);

  const fieldMappings = { ready: input.fieldMappings.length > 0 };
  const statusSemantics = { ready: missingStatusNames.length === 0, missingStatusNames };
  const issueTypes = { ready: input.issueTypes.some((t) => t.class === 'Epic') };

  return {
    fieldMappings,
    statusSemantics,
    issueTypes,
    allReady: fieldMappings.ready && statusSemantics.ready && issueTypes.ready,
  };
}
