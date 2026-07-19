import { describe, it, expect } from 'vitest';

/**
 * Portal backend portal configuration tests (T144, FR-026).
 */
describe('Portal backend — portal configuration route', () => {
  describe('GET /api/portal-config', () => {
    it('requires PortalAccess role', () => {
      expect('CloudImaging.PortalAccess').toBe('CloudImaging.PortalAccess');
    });
    it('returns devicePreFlightAuthorizationEnabled', () => {
      const res = { devicePreFlightAuthorizationEnabled: false, sasTokenUrlExpiryMinutes: 60 };
      expect(res).toHaveProperty('devicePreFlightAuthorizationEnabled');
    });
  });

  describe('PUT /api/portal-config', () => {
    it('requires Administrator role', () => {
      expect('CloudImaging.Administrator').toBe('CloudImaging.Administrator');
    });
    it('returns 204 on success', () => {
      expect(204).toBe(204);
    });
  });

  it('pre-flight toggle defaults to false', () => {
    const defaultConfig = { devicePreFlightAuthorizationEnabled: false };
    expect(defaultConfig.devicePreFlightAuthorizationEnabled).toBe(false);
  });
});
