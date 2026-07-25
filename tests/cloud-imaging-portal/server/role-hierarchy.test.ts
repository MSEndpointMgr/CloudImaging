import { describe, it, expect } from 'vitest';
import type { Response } from 'express';
import { getUserRoles, requireRole, isAdministrator } from '@/middleware/roleGuard.js';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

/**
 * Regression tests for the portal backend role hierarchy (FR-040a).
 *
 * Portal SPA user tokens only ever carry CloudImaging.Administrator or
 * CloudImaging.Technician. CloudImaging.PortalAccess is a service role that never
 * appears in a user token, so any signed-in user must implicitly satisfy it — otherwise
 * every read route 403s for real users (reads fall back to defaults, writes report
 * "Failed to save").
 */
function reqWithRoles(roles: string[] | string | undefined): AuthenticatedRequest {
  return { user: roles === undefined ? {} : { roles } } as unknown as AuthenticatedRequest;
}

function runGuard(req: AuthenticatedRequest, roles: Parameters<typeof requireRole>): number {
  let status = 200;
  let nextCalled = false;
  const res = {
    status(code: number) { status = code; return this; },
    json() { return this; },
  } as unknown as Response;
  requireRole(...roles)(req, res, () => { nextCalled = true; });
  return nextCalled ? 200 : status;
}

describe('Portal backend — role hierarchy', () => {
  it('Administrator implies Technician and PortalAccess', () => {
    const roles = getUserRoles(reqWithRoles(['CloudImaging.Administrator']));
    expect(roles.has('CloudImaging.Administrator')).toBe(true);
    expect(roles.has('CloudImaging.Technician')).toBe(true);
    expect(roles.has('CloudImaging.PortalAccess')).toBe(true);
  });

  it('Technician implies PortalAccess but not Administrator', () => {
    const roles = getUserRoles(reqWithRoles(['CloudImaging.Technician']));
    expect(roles.has('CloudImaging.PortalAccess')).toBe(true);
    expect(roles.has('CloudImaging.Administrator')).toBe(false);
  });

  it('no roles claim yields an empty set', () => {
    expect(getUserRoles(reqWithRoles(undefined)).size).toBe(0);
  });

  it('read routes (PortalAccess) are reachable by any signed-in user', () => {
    expect(runGuard(reqWithRoles(['CloudImaging.Administrator']), ['CloudImaging.PortalAccess'])).toBe(200);
    expect(runGuard(reqWithRoles(['CloudImaging.Technician']), ['CloudImaging.PortalAccess'])).toBe(200);
  });

  it('write routes (Administrator) stay admin-only', () => {
    expect(runGuard(reqWithRoles(['CloudImaging.Administrator']), ['CloudImaging.Administrator'])).toBe(200);
    expect(runGuard(reqWithRoles(['CloudImaging.Technician']), ['CloudImaging.Administrator'])).toBe(403);
    expect(runGuard(reqWithRoles(undefined), ['CloudImaging.Administrator'])).toBe(403);
  });

  it('isAdministrator is only true for the real Administrator role', () => {
    expect(isAdministrator(reqWithRoles(['CloudImaging.Administrator']))).toBe(true);
    expect(isAdministrator(reqWithRoles(['CloudImaging.Technician']))).toBe(false);
  });
});
