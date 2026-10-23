import { Response, NextFunction } from 'express';
import { AuthenticatedRequest } from './auth.js';

/**
 * User-level role authorization middleware for the Cloud Imaging Portal backend (FR-040a).
 *
 * Enforces the CloudImaging.Administrator / CloudImaging.Operator / CloudImaging.Technician access model.
 * Called after the auth middleware; reads the resolved JWT from req.user.
 */

export type PortalRole =
  | 'CloudImaging.Administrator'
  | 'CloudImaging.Operator'
  | 'CloudImaging.Technician'
  | 'CloudImaging.PortalAccess'
  | 'CloudImaging.Reader'
  | 'CloudImaging.AutopilotApprover';

/**
 * Implied-role hierarchy: Administrator implies Operator, which implies Technician.
 * `CloudImaging.PortalAccess` is a service role on the portal backend managed identity and
 * never appears in a user token, so every hierarchical user role is granted it to keep the
 * portal's read routes reachable. Each entry lists its full closure because expansion is a
 * single pass.
 *
 * `CloudImaging.Reader` is deliberately NOT part of this chain. It's a standalone,
 * narrowly-scoped role (Dashboard + Reports only) allowlisted explicitly on the handful of
 * routes those pages need, so it never gains the broad read access PortalAccess implies.
 * `CloudImaging.AutopilotApprover` is standalone for the same reason: it is allowlisted only
 * on the Autopilot registration routes, and Administrator is named alongside it there
 * explicitly rather than implying it here.
 */
const ROLE_IMPLICATIONS: Record<string, readonly PortalRole[]> = {
  'CloudImaging.Administrator': ['CloudImaging.Operator', 'CloudImaging.Technician', 'CloudImaging.PortalAccess'],
  'CloudImaging.Operator': ['CloudImaging.Technician', 'CloudImaging.PortalAccess'],
  'CloudImaging.Technician': ['CloudImaging.PortalAccess'],
};

/** Returns the set of roles the authenticated user holds, expanded with implied roles. */
export function getUserRoles(req: AuthenticatedRequest): Set<string> {
  const claim: unknown = req.user?.['roles'];
  const granted = new Set<string>();
  if (Array.isArray(claim)) for (const r of claim as string[]) granted.add(r);
  else if (typeof claim === 'string') granted.add(claim);

  for (const role of [...granted]) {
    for (const implied of ROLE_IMPLICATIONS[role] ?? []) granted.add(implied);
  }
  return granted;
}

/** Returns true if the user holds the Administrator role. */
export function isAdministrator(req: AuthenticatedRequest): boolean {
  return getUserRoles(req).has('CloudImaging.Administrator');
}

/**
 * Middleware factory that requires the user to hold at least one of the specified roles.
 * Returns HTTP 403 if the requirement is not met.
 */
export function requireRole(...roles: PortalRole[]) {
  return (req: AuthenticatedRequest, res: Response, next: NextFunction): void => {
    const userRoles = getUserRoles(req);
    const hasRequiredRole = roles.some(r => userRoles.has(r));
    if (!hasRequiredRole) {
      res.status(403).json({
        type: 'https://cloudimaging.io/errors/forbidden',
        title: 'Forbidden',
        status: 403,
        detail: `This operation requires one of the following roles: ${roles.join(', ')}.`,
      });
      return;
    }
    next();
  };
}

/**
 * Middleware that restricts access to CloudImaging.Administrator only.
 * Apply to: branding configuration, deployment configuration, cert management, pre-flight
 * overrides, partitioning scheme, update check and Autopilot group tag writes (FR-040a).
 */
export const requireAdmin = requireRole('CloudImaging.Administrator');

/**
 * Middleware for day-to-day operational administration (catalog, upload status, Locations,
 * regions, Wi-Fi). Administrator is listed explicitly so the 403 detail names every accepted role.
 */
export const requireOperator = requireRole('CloudImaging.Operator', 'CloudImaging.Administrator');

/**
 * Middleware for reads that Technicians may see but Reader and AutopilotApprover may not (setup
 * profiles). Operator and Administrator are listed so the 403 detail names every accepted role.
 */
export const requireTechnician = requireRole('CloudImaging.Technician', 'CloudImaging.Operator', 'CloudImaging.Administrator');
