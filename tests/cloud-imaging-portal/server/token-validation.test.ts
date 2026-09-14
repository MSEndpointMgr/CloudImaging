import { describe, it, expect, beforeAll, vi } from 'vitest';
import { generateKeyPairSync } from 'node:crypto';
import jwt from 'jsonwebtoken';

/**
 * Regression tests for portal backend token validation.
 *
 * Which access token version Entra issues is a property of the portal app registration, not of
 * anything the SPA does: an app manifest left at the default `requestedAccessTokenVersion: null`
 * produces v1.0 tokens (`iss` https://sts.windows.net/<tenant>/, `aud` api://<clientId>), while
 * setting it to 2 produces v2.0 tokens (`iss` .../v2.0, `aud` <clientId>). Accepting only the
 * v2.0 pair let sign-in succeed and then failed every API call with 401, which the SPA reads as
 * a dead session, so it restarted sign-in and the browser looped between the portal and Entra.
 *
 * The Operator API already accepts both pairs (EntraTokenValidator); these tests keep the portal
 * backend aligned with it.
 */

const TENANT_ID = '11111111-1111-1111-1111-111111111111';
const CLIENT_ID = '22222222-2222-2222-2222-222222222222';

const { privateKey, publicKey } = generateKeyPairSync('rsa', {
  modulusLength: 2048,
  publicKeyEncoding: { type: 'spki', format: 'pem' },
  privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
});

let verifyPortalToken: typeof import('@/middleware/auth.js').verifyPortalToken;

beforeAll(async () => {
  vi.stubEnv('ENTRA_TENANT_ID', TENANT_ID);
  vi.stubEnv('ENTRA_CLIENT_ID', CLIENT_ID);
  // auth.ts reads both values at import time, so the stubs have to be in place first.
  vi.resetModules();
  ({ verifyPortalToken } = await import('@/middleware/auth.js'));
});

function sign(claims: Record<string, unknown>, expiresIn: string | number = '1h'): string {
  return jwt.sign({ roles: ['CloudImaging.Administrator'], ...claims }, privateKey, {
    algorithm: 'RS256',
    keyid: 'test-key',
    expiresIn,
  });
}

function accepts(token: string): boolean {
  try {
    verifyPortalToken(token, publicKey);
    return true;
  } catch {
    return false;
  }
}

describe('Portal backend: Entra token validation', () => {
  it('accepts a v2.0 access token', () => {
    expect(accepts(sign({
      iss: `https://login.microsoftonline.com/${TENANT_ID}/v2.0`,
      aud: CLIENT_ID,
    }))).toBe(true);
  });

  it('accepts a v1.0 access token, which a default app manifest issues', () => {
    expect(accepts(sign({
      iss: `https://sts.windows.net/${TENANT_ID}/`,
      aud: `api://${CLIENT_ID}`,
    }))).toBe(true);
  });

  it('carries the roles claim through to the caller', () => {
    const payload = verifyPortalToken(sign({
      iss: `https://login.microsoftonline.com/${TENANT_ID}/v2.0`,
      aud: CLIENT_ID,
    }), publicKey);
    expect(payload['roles']).toEqual(['CloudImaging.Administrator']);
  });

  it('still rejects a token issued by another tenant', () => {
    expect(accepts(sign({
      iss: 'https://login.microsoftonline.com/99999999-9999-9999-9999-999999999999/v2.0',
      aud: CLIENT_ID,
    }))).toBe(false);
  });

  it('still rejects a token issued for another resource', () => {
    expect(accepts(sign({
      iss: `https://login.microsoftonline.com/${TENANT_ID}/v2.0`,
      aud: 'api://33333333-3333-3333-3333-333333333333',
    }))).toBe(false);
  });

  it('rejects an expired token, beyond the clock-skew allowance', () => {
    expect(accepts(sign({
      iss: `https://login.microsoftonline.com/${TENANT_ID}/v2.0`,
      aud: CLIENT_ID,
    }, -3600))).toBe(false);
  });

  it('tolerates a few seconds of host clock drift', () => {
    // App Service hosts drift by seconds; without a tolerance that alone produces a 401, which
    // the SPA cannot distinguish from a genuinely dead session.
    expect(accepts(sign({
      iss: `https://login.microsoftonline.com/${TENANT_ID}/v2.0`,
      aud: CLIENT_ID,
    }, -30))).toBe(true);
  });

  it('rejects a token signed by a different key', () => {
    const other = generateKeyPairSync('rsa', {
      modulusLength: 2048,
      publicKeyEncoding: { type: 'spki', format: 'pem' },
      privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
    });
    const token = jwt.sign(
      { iss: `https://login.microsoftonline.com/${TENANT_ID}/v2.0`, aud: CLIENT_ID },
      other.privateKey,
      { algorithm: 'RS256', keyid: 'test-key', expiresIn: '1h' },
    );
    expect(accepts(token)).toBe(false);
  });
});
