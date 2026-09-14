import { Request, Response, NextFunction } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import jwksClient from 'jwks-rsa';

/**
 * Entra ID token validation middleware for the Cloud Imaging Portal backend (FR-030, FR-040).
 * Validates the access token from the Authorization header against the Cloud Imaging Portal
 * SPA app registration (portalClientId / ENTRA_CLIENT_ID).
 */

const tenantId      = process.env['ENTRA_TENANT_ID']    ?? '';
const clientId      = process.env['ENTRA_CLIENT_ID']    ?? '';
const jwksUri       = `https://login.microsoftonline.com/${tenantId}/discovery/v2.0/keys`;

/**
 * Both token versions are accepted, because which one Entra issues is a property of the portal
 * app registration rather than of anything this code or the SPA does.
 *
 * An app whose manifest leaves `requestedAccessTokenVersion` at its default of null issues v1.0
 * access tokens: `iss` is `https://sts.windows.net/<tenant>/` and `aud` is the App ID URI string
 * (`api://<clientId>`). Set it to 2 and the same request yields `iss`
 * `https://login.microsoftonline.com/<tenant>/v2.0` and `aud` as the bare client ID. Pinning
 * only the v2.0 pair meant a tenant that registered the portal the other way had sign-in
 * succeed and then every single API call fail 401, which the SPA can only read as a dead
 * session, so it restarted sign-in and the browser looped between the portal and Entra.
 *
 * Both issuers are tenant-pinned and both audiences are this app, so nothing is loosened: a
 * token for another tenant or another resource still fails.
 */
const issuers: [string, ...string[]] = [`https://login.microsoftonline.com/${tenantId}/v2.0`, `https://sts.windows.net/${tenantId}/`];
const audiences: [string, ...string[]] = [clientId, `api://${clientId}`];

const client = jwksClient({ jwksUri, cache: true, cacheMaxAge: 600_000 });

function getSigningKey(header: jwt.JwtHeader): Promise<string> {
  return new Promise((resolve, reject) => {
    client.getSigningKey(header.kid ?? '', (err, key) => {
      if (err || !key) { reject(err ?? new Error('Signing key not found')); return; }
      resolve(key.getPublicKey());
    });
  });
}

export interface AuthenticatedRequest extends Request {
  user?: JwtPayload;
}

/**
 * Verifies a token against the portal's expected issuers/audiences. Throws on any failure.
 *
 * Split out from {@link auth} so the accepted claim pairs can be tested without reaching the
 * live JWKS endpoint, which is the one part of validation a test cannot stand in for.
 */
export function verifyPortalToken(token: string, signingKey: string): JwtPayload {
  return jwt.verify(token, signingKey, {
    algorithms: ['RS256'],
    audience: audiences,
    issuer: issuers,
    // Matches the Operator API's tolerance. jsonwebtoken defaults to zero, so without this a
    // few seconds of clock drift on the App Service host expires tokens early, and a 401 is
    // what sends the SPA back to sign-in.
    clockTolerance: 300,
  }) as JwtPayload;
}

export async function auth(req: AuthenticatedRequest, res: Response, next: NextFunction): Promise<void> {
  const authHeader = req.headers['authorization'];
  if (!authHeader?.startsWith('Bearer ')) {
    res.status(401).json({
      type: 'https://cloudimaging.io/errors/unauthorized',
      title: 'Unauthorized',
      status: 401,
      detail: 'Missing or malformed Authorization header.',
    });
    return;
  }

  const token = authHeader.slice(7);
  try {
    const decoded = jwt.decode(token, { complete: true });
    if (!decoded?.header) throw new Error('Token cannot be decoded');

    req.user = verifyPortalToken(token, await getSigningKey(decoded.header));
    next();
  } catch (err) {
    // Every rejection here reaches the SPA as a dead session, so a misconfiguration that fails
    // all tokens is otherwise indistinguishable from an expired one. Log what the token claimed
    // (never the token itself) so the mismatch is one log line instead of a guess.
    const claims = jwt.decode(token) as JwtPayload | null;
    console.warn('Portal token validation failed', {
      reason: err instanceof Error ? err.message : String(err),
      tokenAudience: claims?.aud,
      tokenIssuer: claims?.iss,
      expectedAudiences: audiences,
      expectedIssuers: issuers,
    });
    res.status(401).json({
      type: 'https://cloudimaging.io/errors/unauthorized',
      title: 'Unauthorized',
      status: 401,
      detail: 'Token validation failed.',
    });
  }
}
