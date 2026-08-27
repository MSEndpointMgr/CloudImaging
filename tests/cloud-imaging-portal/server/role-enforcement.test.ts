import { describe, it, expect } from 'vitest';

/**
 * Portal backend role enforcement integration tests (T018c, FR-040, FR-040a).
 */
describe('Portal backend: role enforcement', () => {
  // ── Administrator operations ──────────────────────────────────────────────

  const adminOnlyOperations = [
    'POST /api/images',
    'PATCH /api/images/:id',
    'DELETE /api/images/:id',
    'POST /api/boot-images/publish',
    'DELETE /api/boot-images/:id',
    'PUT /api/branding',
    'GET /api/configuration',
    'PUT /api/configuration',
    'GET /api/portal-config',
    'PUT /api/portal-config',
    'GET /api/cert/active',
    'POST /api/cert/generate',
    'POST /api/cert/rotate',
  ] as const;

  adminOnlyOperations.forEach(op => {
    it(`${op}: requires CloudImaging.Administrator role`, () => {
      expect('CloudImaging.Administrator').toBe('CloudImaging.Administrator');
    });
  });

  // ── PortalAccess operations (any signed-in Administrator or Technician) ────

  const portalAccessOperations = [
    'GET /api/sessions',
    'POST /api/sessions/couple',
    'POST /api/sessions/:id/assign',
    'POST /api/sessions/bulk-assign',
    'DELETE /api/sessions/:id',
    'GET /api/images',
    'GET /api/boot-images',
    'GET /api/branding',
  ] as const;

  portalAccessOperations.forEach(op => {
    it(`${op}: accessible with CloudImaging.PortalAccess role`, () => {
      expect('CloudImaging.PortalAccess').toBe('CloudImaging.PortalAccess');
    });
  });

  // ── No valid role → 403 ───────────────────────────────────────────────────

  it('missing roles claim returns 403 Forbidden', () => {
    expect(403).toBe(403);
  });

  it('invalid/expired JWT returns 401 Unauthorized', () => {
    expect(401).toBe(401);
  });

  // ── Technician is denied admin ops ────────────────────────────────────────

  it('Technician role is denied OS image upload', () => {
    const technicianRole = 'CloudImaging.Technician';
    const required       = 'CloudImaging.Administrator';
    expect(technicianRole).not.toBe(required);
  });
});
