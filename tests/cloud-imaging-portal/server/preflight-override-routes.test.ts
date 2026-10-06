import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  listPreFlightOverrides: vi.fn(),
  approvePreFlightOverride: vi.fn(),
  revokePreFlightOverride: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { preFlightOverridesRouter } = await import('@/routes/preflight-overrides.js');
const { sessionsRouter } = await import('@/routes/sessions.js');

const SESSION_ID = '3f2a9c1e-7b4d-4e2a-9c51-8d0e6b2f4a17';

type Claims = Record<string, unknown>;

/** Mounts both routers behind a stand-in for the auth middleware that injects the given claims. */
function appFor(claims: Claims) {
  const app = express();
  app.use(express.json());
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.use('/api/preflight-overrides', preFlightOverridesRouter);
  app.use('/api/sessions', sessionsRouter);
  return app;
}

const admin: Claims = { roles: ['CloudImaging.Administrator'], preferred_username: 'admin@contoso.com', oid: 'oid-admin' };
const technician: Claims = { roles: ['CloudImaging.Technician'], preferred_username: 'tech@contoso.com' };
const reader: Claims = { roles: ['CloudImaging.Reader'], preferred_username: 'reader@contoso.com' };

describe('Portal backend: pre-flight override routes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    operatorApi.listPreFlightOverrides.mockResolvedValue({ preFlightEnabled: true, overrides: [] });
    operatorApi.approvePreFlightOverride.mockResolvedValue({ serialNumber: 'PF4K7T2M' });
    operatorApi.revokePreFlightOverride.mockResolvedValue(undefined);
  });

  it.each([
    ['Administrator', admin, 200],
    ['Technician', technician, 200],
    ['Reader', reader, 403],
  ])('lists overrides for %s with status %i', async (_name, claims, status) => {
    const res = await request(appFor(claims)).get('/api/preflight-overrides');
    expect(res.status).toBe(status);
  });

  it.each([
    ['Administrator', admin, 200],
    ['Technician', technician, 403],
  ])('lets %s approve with status %i', async (_name, claims, status) => {
    const res = await request(appFor(claims)).post(`/api/sessions/${SESSION_ID}/preflight-override`);
    expect(res.status).toBe(status);
  });

  it('records the approver from the token, not the request body', async () => {
    await request(appFor(admin)).post(`/api/sessions/${SESSION_ID}/preflight-override`).send({ approvedBy: 'someone-else@contoso.com' });
    expect(operatorApi.approvePreFlightOverride).toHaveBeenCalledWith(SESSION_ID, { approvedBy: 'admin@contoso.com', approvedByObjectId: 'oid-admin' });
  });

  it('rejects a session id that is not a GUID', async () => {
    const res = await request(appFor(admin)).post('/api/sessions/not-a-guid/preflight-override');
    expect(res.status).toBe(400);
    expect(operatorApi.approvePreFlightOverride).not.toHaveBeenCalled();
  });

  it.each([
    ['Administrator', admin, 204],
    ['Technician', technician, 403],
  ])('lets %s revoke with status %i', async (_name, claims, status) => {
    const res = await request(appFor(claims)).delete('/api/preflight-overrides?serialNumber=PF4K7T2M');
    expect(res.status).toBe(status);
  });

  it('revokes as the caller and requires a serial number', async () => {
    await request(appFor(admin)).delete('/api/preflight-overrides?serialNumber=PF4K7T2M&revokedBy=someone-else');
    expect(operatorApi.revokePreFlightOverride).toHaveBeenCalledWith('PF4K7T2M', 'admin@contoso.com');

    const missing = await request(appFor(admin)).delete('/api/preflight-overrides');
    expect(missing.status).toBe(400);
  });
});
