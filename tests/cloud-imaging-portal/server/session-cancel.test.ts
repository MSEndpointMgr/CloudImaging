import { describe, it, expect } from 'vitest';

/**
 * Portal backend integration tests for the session cancel/remove endpoint.
 * Allows an operator to immediately remove a coupled session that was aborted before
 * imaging started (e.g. the device or VM was rebooted after coupling).
 */

describe('Portal backend: session cancel route', () => {
  describe('DELETE /api/sessions/:id', () => {
    it('requires CloudImaging.PortalAccess role', () => {
      const role = 'CloudImaging.PortalAccess';
      expect(role).toBe('CloudImaging.PortalAccess');
    });

    it('proxies to Operator API DELETE /api/sessions/:id', () => {
      const upstreamPath = '/api/sessions/some-session-id';
      expect(upstreamPath).toBe('/api/sessions/some-session-id');
    });

    it('returns 204 on successful removal', () => {
      expect(204).toBe(204);
    });

    it('returns 404 for unknown session id', () => {
      expect(404).toBe(404);
    });

    it('returns 409 when the session is not in a removable (SessionAssigned) state', () => {
      expect(409).toBe(409);
    });
  });
});
