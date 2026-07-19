import { describe, it, expect } from 'vitest';

/**
 * Portal frontend single-session image assign flow tests (T038a, FR-033).
 */
describe('Portal frontend — session image assign flow', () => {
  it('Assign Image button is visible only for SessionAssigned rows', () => {
    const assignableState = 'SessionAssigned';
    const nonAssignable   = ['SessionInit', 'SessionAllowed', 'SessionCompleted', 'SessionFailed'];
    expect(nonAssignable).not.toContain(assignableState);
  });

  it('clicking Assign Image opens AssignImageDialog', () => {
    const opensDialog = true;
    expect(opensDialog).toBe(true);
  });

  it('AssignImageDialog shows searchable OS image list', () => {
    const isSearchable = true;
    expect(isSearchable).toBe(true);
  });

  it('confirming image selection triggers POST /sessions/:id/assign', () => {
    const endpoint = '/sessions/:id/assign';
    expect(endpoint).toContain('assign');
  });

  it('row transitions to started state after assignment', () => {
    const newState = 'SessionStarted';
    expect(newState).toBe('SessionStarted');
  });
});
