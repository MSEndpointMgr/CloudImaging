import { Request, Response, NextFunction } from 'express';

/**
 * Route parameter shape validation for the portal backend.
 *
 * Every identifier the portal accepts in a URL path is server-generated: catalog and session
 * ids are GUIDs (`Guid.NewGuid()`), and staged-upload ids are the same GUIDs in 32-hex "N"
 * form. The Operator API re-validates them with `Guid.TryParse` and answers 400, so the value
 * of checking here is not the parse itself but rejecting anything that is not an identifier
 * *before* it is interpolated into a downstream request path. Combined with the
 * `encodeURIComponent` applied in the Operator API client, that leaves no way to steer a
 * portal-authenticated request at a different Operator API route than the one the caller's
 * role was checked against.
 */

/** A GUID in dashed ("D") or 32-hex ("N") form. Both are produced by the backend. */
const GUID_PATTERN =
  /^(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})$/i;

/**
 * Middleware factory that rejects the request with 400 unless every named route parameter is a
 * GUID. Parameter names come from the route definitions in this repository, never from the
 * request, so echoing one back in the problem detail cannot reflect caller-controlled content.
 */
export function requireGuidParams(...names: string[]) {
  return (req: Request, res: Response, next: NextFunction): void => {
    for (const name of names) {
      const value = req.params[name];
      if (typeof value !== 'string' || !GUID_PATTERN.test(value)) {
        res.status(400).json({
          type: 'https://cloudimaging.io/errors/invalid-parameter',
          title: 'Invalid identifier.',
          status: 400,
          detail: `Route parameter '${name}' must be a GUID.`,
        });
        return;
      }
    }
    next();
  };
}
