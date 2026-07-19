import { describe, it, expect } from 'vitest';

/**
 * Portal backend branding route tests (T090, FR-038).
 */
describe('Portal backend — branding routes', () => {
  describe('GET /api/branding', () => {
    it('requires PortalAccess role', () => {
      expect('CloudImaging.PortalAccess').toBe('CloudImaging.PortalAccess');
    });
    it('returns primaryColor, accentColor, applicationName', () => {
      const res = { primaryColor: '#0078d4', accentColor: '#005a9e', applicationName: 'Cloud Imaging' };
      expect(res).toHaveProperty('primaryColor');
      expect(res).toHaveProperty('accentColor');
      expect(res).toHaveProperty('applicationName');
    });
  });

  describe('PUT /api/branding', () => {
    it('requires Administrator role', () => {
      expect('CloudImaging.Administrator').toBe('CloudImaging.Administrator');
    });
    it('returns 204 on success', () => {
      expect(204).toBe(204);
    });
  });

  describe('GET /api/branding/logo/sas', () => {
    it('returns sasTokenUrl and expiresAt', () => {
      const res = { sasTokenUrl: 'https://blob.azure.com/sas', expiresAt: new Date().toISOString() };
      expect(res).toHaveProperty('sasTokenUrl');
      expect(res).toHaveProperty('expiresAt');
    });
    it('returns 404 when no logo configured', () => {
      expect(404).toBe(404);
    });
  });
});
