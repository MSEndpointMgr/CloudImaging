import { describe, it, expect } from 'vitest';
import type { Response } from 'express';
import { getUserRoles, requireRole, requireOperator, requireTechnician, isAdministrator } from '@/middleware/roleGuard.js';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

/**
 * Regression tests for the portal backend role hierarchy (FR-040a).
 *
 * Portal SPA user tokens only ever carry CloudImaging.Administrator or
 * CloudImaging.Technician. CloudImaging.PortalAccess is a service role that never
 * appears in a user token, so any signed-in user must implicitly satisfy it, otherwise
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

describe('Portal backend: role hierarchy', () => {
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

  it('Reader does not imply PortalAccess or Administrator', () => {
    const roles = getUserRoles(reqWithRoles(['CloudImaging.Reader']));
    expect(roles.has('CloudImaging.Reader')).toBe(true);
    expect(roles.has('CloudImaging.PortalAccess')).toBe(false);
    expect(roles.has('CloudImaging.Administrator')).toBe(false);
    expect(roles.has('CloudImaging.Technician')).toBe(false);
  });

  it('Reader alone is rejected by a plain PortalAccess-only guard', () => {
    expect(runGuard(reqWithRoles(['CloudImaging.Reader']), ['CloudImaging.PortalAccess'])).toBe(403);
  });

  it('Reader is accepted only where explicitly allowlisted alongside PortalAccess', () => {
    expect(runGuard(reqWithRoles(['CloudImaging.Reader']), ['CloudImaging.PortalAccess', 'CloudImaging.Reader'])).toBe(200);
    expect(runGuard(reqWithRoles(['CloudImaging.Reader']), ['CloudImaging.Administrator'])).toBe(403);
  });

  it.each([
    ['CloudImaging.Technician', 200],
    ['CloudImaging.Operator', 200],
    ['CloudImaging.Administrator', 200],
    ['CloudImaging.Reader', 403],
    ['CloudImaging.AutopilotApprover', 403],
  ])('requireTechnician with %s returns %i', (role, expected) => {
    let status = 200;
    let nextCalled = false;
    const res = { status(code: number) { status = code; return this; }, json() { return this; } } as unknown as Response;
    requireTechnician(reqWithRoles([role]), res, () => { nextCalled = true; });
    expect(nextCalled ? 200 : status).toBe(expected);
  });

  it('requireTechnician refuses a user without roles and names every accepted role', () => {
    let body: { detail?: string } = {};
    let status = 200;
    const res = {
      status(code: number) { status = code; return this; },
      json(payload: { detail?: string }) { body = payload; return this; },
    } as unknown as Response;
    requireTechnician(reqWithRoles(undefined), res, () => { status = 200; });
    expect(status).toBe(403);
    expect(body.detail).toBe('This operation requires one of the following roles: CloudImaging.Technician, CloudImaging.Operator, CloudImaging.Administrator.');
  });
});

const ADMIN = 'CloudImaging.Administrator';
const OPS = 'CloudImaging.Operator';
const TECH = 'CloudImaging.Technician';
const PORTAL = 'CloudImaging.PortalAccess';
const READER = 'CloudImaging.Reader';
const APPROVER = 'CloudImaging.AutopilotApprover';
const ALL_ROLES = [ADMIN, OPS, TECH, PORTAL, READER, APPROVER];

function expand(roles: string[]): string[] {
  return [...getUserRoles(reqWithRoles(roles))].sort();
}

/** Runs a prebuilt guard and returns the status and response body it produced. */
function runPrebuilt(
  guard: ReturnType<typeof requireRole>,
  req: AuthenticatedRequest,
): { status: number; body: { detail?: string } | undefined } {
  let status = 200;
  let body: { detail?: string } | undefined;
  const res = {
    status(code: number) { status = code; return this; },
    json(payload: { detail?: string }) { body = payload; return this; },
  } as unknown as Response;
  guard(req, res, () => undefined);
  return { status, body };
}

describe('Portal backend: Operator role hierarchy', () => {
  it('Administrator expands to Operator, Technician and PortalAccess', () => {
    expect(expand([ADMIN])).toEqual([ADMIN, OPS, PORTAL, TECH].sort());
  });

  it('Operator expands to Technician and PortalAccess', () => {
    expect(expand([OPS])).toEqual([OPS, PORTAL, TECH].sort());
  });

  it('Technician expands to PortalAccess', () => {
    expect(expand([TECH])).toEqual([PORTAL, TECH].sort());
  });

  it.each([READER, APPROVER])('%s expands to itself only', (role) => {
    expect(expand([role])).toEqual([role]);
  });

  it.each(ALL_ROLES)('implication table is closed for %s', (role) => {
    const expanded = expand([role]);
    for (const implied of expanded) {
      for (const transitive of expand([implied])) expect(expanded).toContain(transitive);
    }
  });

  it.each(ALL_ROLES.filter(r => r !== ADMIN))('%s does not imply Administrator', (role) => {
    expect(expand([role])).not.toContain(ADMIN);
  });

  it.each([[[]], [[OPS]], [[TECH, READER]], [[OPS, APPROVER]], [ALL_ROLES]])('expansion of %j is a superset and idempotent', (roles) => {
    const once = expand(roles);
    for (const role of roles) expect(once).toContain(role);
    expect(expand(once)).toEqual(once);
  });

  it('requireOperator accepts Operator and Administrator and refuses other roles', () => {
    expect(runPrebuilt(requireOperator, reqWithRoles([OPS])).status).toBe(200);
    expect(runPrebuilt(requireOperator, reqWithRoles([ADMIN])).status).toBe(200);
    for (const role of [TECH, READER, APPROVER]) {
      expect(runPrebuilt(requireOperator, reqWithRoles([role])).status).toBe(403);
    }
  });

  it('requireOperator 403 detail names Operator and Administrator', () => {
    const { status, body } = runPrebuilt(requireOperator, reqWithRoles([TECH]));
    expect(status).toBe(403);
    expect(body?.detail).toBe('This operation requires one of the following roles: CloudImaging.Operator, CloudImaging.Administrator.');
  });

  it('Operator passes PortalAccess routes but not Administrator-only routes', () => {
    expect(runGuard(reqWithRoles([OPS]), [PORTAL])).toBe(200);
    expect(runGuard(reqWithRoles([OPS]), [ADMIN])).toBe(403);
    expect(isAdministrator(reqWithRoles([OPS]))).toBe(false);
  });
});
