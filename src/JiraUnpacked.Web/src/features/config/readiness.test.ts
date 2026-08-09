import { describe, expect, it } from 'vitest';
import { deriveReadiness } from './readiness';

const base = {
  fieldMappings: [{ role: 'Status' as const, jiraFieldId: 'status' }],
  jiraStatuses: [{ id: '1', name: 'To Do' }, { id: '2', name: 'Done' }],
  statusSemantics: [
    { jiraStatusId: '1', statusName: 'To Do', class: 'Active' as const },
    { jiraStatusId: '2', statusName: 'Done',  class: 'Done'   as const },
  ],
  issueTypes: [{ jiraIssueTypeId: '1', name: 'Epic', class: 'Epic' as const }],
};

describe('deriveReadiness', () => {
  it('is complete when all three steps are satisfied', () => {
    const r = deriveReadiness(base);
    expect(r.fieldMappings.ready).toBe(true);
    expect(r.statusSemantics.ready).toBe(true);
    expect(r.issueTypes.ready).toBe(true);
    expect(r.allReady).toBe(true);
  });

  it('is not ready when no field is mapped', () => {
    expect(deriveReadiness({ ...base, fieldMappings: [] }).fieldMappings.ready).toBe(false);
  });

  // The subtle one: SOME statuses classified is NOT ready. A "non-empty"
  // check would pass here and let a user believe reporting is trustworthy.
  it('is not ready when status coverage is only partial', () => {
    const r = deriveReadiness({ ...base, statusSemantics: [base.statusSemantics[0]] });
    expect(r.statusSemantics.ready).toBe(false);
    expect(r.statusSemantics.missingStatusNames).toEqual(['Done']);
    expect(r.allReady).toBe(false);
  });

  it('is ready with zero statuses to classify (nothing is missing)', () => {
    const r = deriveReadiness({ ...base, jiraStatuses: [], statusSemantics: [] });
    expect(r.statusSemantics.ready).toBe(true);
  });

  it('is not ready when no issue type is classed Epic', () => {
    const r = deriveReadiness({ ...base, issueTypes: [{ jiraIssueTypeId: '1', name: 'Story', class: 'Other' }] });
    expect(r.issueTypes.ready).toBe(false);
  });

  it('ignores semantics for statuses Jira no longer returns', () => {
    const r = deriveReadiness({
      ...base,
      statusSemantics: [...base.statusSemantics, { jiraStatusId: '99', statusName: 'Gone', class: 'Other' }],
    });
    expect(r.statusSemantics.ready).toBe(true);
  });
});
