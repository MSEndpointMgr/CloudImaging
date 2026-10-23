import { describe, it, expect } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import { requireOperator, requireRole } from '@/middleware/roleGuard.js';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

/**
 * Portal backend role enforcement for tokens that carry no usable role (FR-040, FR-040a).
 * Per-route, per-role enforcement lives in role-route-matrix.test.ts.
 */
function appFor(claims: Record<string, unknown>) {
  const app = express();
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.get('/read', requireRole('CloudImaging.PortalAccess'), (_req, res) => { res.json({}); });
  app.post('/write', requireOperator, (_req, res) => { res.json({}); });
  return app;
}

describe('Portal backend: role enforcement', () => {
  // No valid role: 403 on read and write routes alike.
  it.each([
    ['no roles claim', {}],
    ['an empty roles claim', { roles: [] }],
    ['only unknown roles', { roles: ['CloudImaging.Unknown', 'cloudimaging.administrator'] }],
  ])('refuses a token with %s', async (_name, claims) => {
    const app = appFor(claims);
    const read = await request(app).get('/read');
    const write = await request(app).post('/write');

    expect(read.status).toBe(403);
    expect(read.body.detail).toBe('This operation requires one of the following roles: CloudImaging.PortalAccess.');
    expect(write.status).toBe(403);
    expect(write.body.detail).toBe('This operation requires one of the following roles: CloudImaging.Operator, CloudImaging.Administrator.');
  });
});
