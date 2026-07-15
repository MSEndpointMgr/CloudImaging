import { Response, NextFunction } from 'express';
import { AuthenticatedRequest } from './auth.js';

/**
 * User-level role authorization middleware for the Cloud Imaging Portal backend (FR-040a).
 *
 * Enforces the CloudImaging.Administrator / CloudImaging.Technician access model.
 * Called after the auth middleware; reads the resolved JWT from req.user.
 */

export type PortalRole = 'CloudImaging.Administrator' | 'CloudImaging.Technician';

/** Returns the set of roles the authenticated user holds. */
export function getUserRoles(req: AuthenticatedRequest): Set<string> {
  const roles: unknown = req.user?.['roles'];
  if (Array.isArray(roles)) return new Set<string>(roles as string[]);
  if (typeof roles === 'string') return new Set<string>([roles]);
  return new Set<string>();
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
 * Apply to: OS image upload/edit/delete, boot image lifecycle,
 * branding configuration, deployment configuration, cert management (FR-040a).
 */
export const requireAdmin = requireRole('CloudImaging.Administrator');
