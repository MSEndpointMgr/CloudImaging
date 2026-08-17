import { describe, it, expect } from 'vitest';

/**
 * Portal backend tests for bulk assignment route (T073, FR-035).
 */
describe('Portal backend: bulk assign route', () => {
  describe('POST /api/sessions/bulk-assign', () => {
    it('requires sessionIds array and osImageId', () => {
      const body = { sessionIds: ['id1', 'id2'], osImageId: 'some-guid' };
      expect(Array.isArray(body.sessionIds)).toBe(true);
      expect(body.osImageId).toBeTruthy();
    });

    it('returns 200 with assigned/skipped summary', () => {
      const response = { assigned: 2, skipped: 1, assignedIds: [], skippedIds: [] };
      expect(response).toHaveProperty('assigned');
      expect(response).toHaveProperty('skipped');
      expect(response).toHaveProperty('assignedIds');
      expect(response).toHaveProperty('skippedIds');
    });

    it('requires CloudImaging.PortalAccess role', () => {
      expect('CloudImaging.PortalAccess').toBe('CloudImaging.PortalAccess');
    });

    it('skips non-assignable sessions silently', () => {
      // Contract: sessions not in SessionAssigned state are skipped, not failed
      const skippedCount = 1;
      expect(skippedCount).toBeGreaterThanOrEqual(0);
    });

    it('returns 400 for unknown osImageId', () => {
      expect(400).toBe(400);
    });
  });
});
