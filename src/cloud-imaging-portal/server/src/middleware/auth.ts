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
const issuer        = `https://login.microsoftonline.com/${tenantId}/v2.0`;

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

    const signingKey = await getSigningKey(decoded.header);
    const payload = jwt.verify(token, signingKey, {
      algorithms: ['RS256'],
      audience: clientId,
      issuer,
    }) as JwtPayload;

    req.user = payload;
    next();
  } catch {
    res.status(401).json({
      type: 'https://cloudimaging.io/errors/unauthorized',
      title: 'Unauthorized',
      status: 401,
      detail: 'Token validation failed.',
    });
  }
}
