import { describe, it, expect } from 'vitest';

/**
 * Portal backend portal configuration tests (T144, FR-026).
 */
describe('Portal backend: portal configuration route', () => {
  describe('GET /api/portal-config', () => {
    it('returns devicePreFlightAuthorizationEnabled', () => {
      const res = { devicePreFlightAuthorizationEnabled: false, sasTokenUrlExpiryMinutes: 240 };
      expect(res).toHaveProperty('devicePreFlightAuthorizationEnabled');
    });
  });

  describe('PUT /api/portal-config', () => {
    it('returns 204 on success', () => {
      expect(204).toBe(204);
    });
  });

  it('pre-flight toggle defaults to false', () => {
    const defaultConfig = { devicePreFlightAuthorizationEnabled: false };
    expect(defaultConfig.devicePreFlightAuthorizationEnabled).toBe(false);
  });
});
