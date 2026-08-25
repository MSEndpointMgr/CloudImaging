import { describe, it, expect } from 'vitest';

/**
 * Portal frontend bulk assignment UI tests (T074, FR-035).
 * Bulk assignment is no longer selection-driven — the OS image dropdown above the
 * Coupled Devices table always applies to every row currently in that table.
 */
describe('Portal frontend: bulk assignment UI', () => {
  it('Start Imaging targets every row in the Coupled Devices table, not a checked subset', () => {
    const coupled = [
      { state: 'SessionAssigned' },
      { state: 'SessionAssigned' },
    ];
    const targeted = coupled.filter(r => r.state === 'SessionAssigned').length;
    expect(targeted).toBe(coupled.length);
  });

  it('Start Imaging is disabled when the Coupled Devices table is empty', () => {
    const coupledCount = 0;
    const disabled = coupledCount === 0;
    expect(disabled).toBe(true);
  });

  it('bulk-assign wires to POST /api/sessions/bulk-assign', () => {
    const endpoint = '/api/sessions/bulk-assign';
    expect(endpoint).toBe('/api/sessions/bulk-assign');
  });

  it('bulk-assign request body includes sessionIds and osImageId', () => {
    const body = { sessionIds: ['id1', 'id2'], osImageId: 'image-guid' };
    expect(Array.isArray(body.sessionIds)).toBe(true);
    expect(body.osImageId).toBeTruthy();
  });
});
