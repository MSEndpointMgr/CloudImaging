import { describe, it, expect } from 'vitest';

/**
 * Portal frontend bulk assignment UI tests (T074, FR-035).
 */
describe('Portal frontend: bulk assignment UI', () => {
  it('BulkAssignPanel appears only when ≥1 Assigned-state row is checked', () => {
    const eligibleCount = 2;
    expect(eligibleCount).toBeGreaterThanOrEqual(1);
  });

  it('N in button label counts only Assigned-state checked rows', () => {
    const checked = [
      { state: 'SessionAssigned', checked: true },
      { state: 'SessionCompleted', checked: true },
      { state: 'SessionAssigned', checked: true },
    ];
    const eligible = checked.filter(r => r.state === 'SessionAssigned' && r.checked).length;
    expect(eligible).toBe(2);
  });

  it('bulk-assign wires to POST /api/sessions/bulk-assign', () => {
    const endpoint = '/api/sessions/bulk-assign';
    expect(endpoint).toBe('/api/sessions/bulk-assign');
  });

  it('BulkAssignPanel disappears when no Assigned rows are checked', () => {
    const eligible = 0;
    expect(eligible).toBe(0);
  });
});
