import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import { AxiosError, AxiosHeaders, type InternalAxiosRequestConfig } from 'axios';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  bulkAssign: vi.fn(),
  assignSession: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { sessionsRouter } = await import('@/routes/sessions.js');
const { errorHandler } = await import('@/middleware/errorHandler.js');

const sessionA = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';
const sessionB = '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
const imageId = '2c9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
const modelId = '3d9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
const technician = { roles: ['CloudImaging.Technician'], preferred_username: 'tina@contoso.com', oid: 'oid-tina' };
const actor = { upn: 'tina@contoso.com', objectId: 'oid-tina' };

function appFor(claims: Record<string, unknown>) {
  const app = express();
  app.use(express.json({ limit: '1mb' }));
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.use('/api/sessions', sessionsRouter);
  app.use(errorHandler);
  return app;
}

function upstreamError(status: number, data: unknown): AxiosError {
  const config = { headers: new AxiosHeaders() } as InternalAxiosRequestConfig;
  return new AxiosError('Request failed', 'ERR_BAD_REQUEST', config, undefined, { status, statusText: '', headers: {}, config, data });
}

describe('Portal backend: bulk assign route', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    operatorApi.bulkAssign.mockResolvedValue({ assigned: 2, skipped: 0, assignedIds: [sessionA, sessionB], skippedIds: [] });
    operatorApi.assignSession.mockResolvedValue({ sessionId: sessionA });
  });

  it('forwards no choices and no actor when nothing was chosen', async () => {
    const res = await request(appFor(technician)).post('/api/sessions/bulk-assign').send({ sessionIds: [sessionA, sessionB], osImageId: imageId });

    expect(res.status).toBe(202);
    expect(operatorApi.bulkAssign).toHaveBeenCalledWith([sessionA, sessionB], imageId, undefined);
  });

  it('forwards only sessionId, kind and modelId with the actor from the token, discarding a browser actor', async () => {
    await request(appFor(technician)).post('/api/sessions/bulk-assign').send({
      sessionIds: [sessionA, sessionB],
      osImageId: imageId,
      driverChoices: [{ sessionId: sessionA, kind: 'Model', modelId, extra: 1 }, { sessionId: sessionB, kind: 'None', modelId }],
      actor: { upn: 'mallory@contoso.com' },
    });

    expect(operatorApi.bulkAssign).toHaveBeenCalledWith([sessionA, sessionB], imageId, {
      driverChoices: [{ sessionId: sessionA, kind: 'Model', modelId }, { sessionId: sessionB, kind: 'None' }],
      actor,
    });
  });

  it.each([
    ['an unknown kind', [{ sessionId: sessionA, kind: 'Other' }]],
    ['Model without a modelId', [{ sessionId: sessionA, kind: 'Model' }]],
    ['a modelId that is not a GUID', [{ sessionId: sessionA, kind: 'Model', modelId: 'x' }]],
    ['a session that is not assigned', [{ sessionId: modelId, kind: 'None' }]],
    ['two choices for one session', [{ sessionId: sessionA, kind: 'None' }, { sessionId: sessionA, kind: 'None' }]],
    ['not a list', { sessionId: sessionA, kind: 'None' }],
  ])('400 without an upstream call for %s', async (_name, driverChoices) => {
    const res = await request(appFor(technician)).post('/api/sessions/bulk-assign').send({ sessionIds: [sessionA], osImageId: imageId, driverChoices });

    expect(res.status).toBe(400);
    expect(operatorApi.bulkAssign).not.toHaveBeenCalled();
  });

  it('401 without an identity when choices are present', async () => {
    const res = await request(appFor({ roles: ['CloudImaging.Technician'] })).post('/api/sessions/bulk-assign').send({ sessionIds: [sessionA], osImageId: imageId, driverChoices: [{ sessionId: sessionA, kind: 'None' }] });

    expect(res.status).toBe(401);
    expect(operatorApi.bulkAssign).not.toHaveBeenCalled();
  });

  it('relays the 409 with its errors per session', async () => {
    operatorApi.bulkAssign.mockRejectedValueOnce(upstreamError(409, {
      type: 'https://cloudimaging.io/errors/drivers',
      status: 409,
      detail: 'The chosen driver model is no longer available for 1 device. Choose again.',
      errors: [{ sessionId: sessionA, reason: 'The chosen driver model was removed.' }],
    }));

    const res = await request(appFor(technician)).post('/api/sessions/bulk-assign').send({ sessionIds: [sessionA], osImageId: imageId, driverChoices: [{ sessionId: sessionA, kind: 'Model', modelId }] });

    expect(res.status).toBe(409);
    expect(res.body.detail).toBe('The chosen driver model is no longer available for 1 device. Choose again.');
    expect(res.body.errors).toEqual([{ sessionId: sessionA, reason: 'The chosen driver model was removed.' }]);
  });

  it('the single assign validates and forwards a choice the same way', async () => {
    const app = appFor(technician);

    await request(app).post(`/api/sessions/${sessionA}/assign`).send({ osImageId: imageId, driverChoice: { kind: 'Model', modelId, sessionId: sessionB } });
    const bad = await request(app).post(`/api/sessions/${sessionA}/assign`).send({ osImageId: imageId, driverChoice: { kind: 'Model' } });
    await request(app).post(`/api/sessions/${sessionA}/assign`).send({ osImageId: imageId });

    expect(operatorApi.assignSession).toHaveBeenNthCalledWith(1, sessionA, imageId, { driverChoice: { kind: 'Model', modelId }, actor });
    expect(bad.status).toBe(400);
    expect(operatorApi.assignSession).toHaveBeenNthCalledWith(2, sessionA, imageId, undefined);
  });
});
