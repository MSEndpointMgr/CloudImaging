import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  getRegions: vi.fn(),
  putRegions: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { regionsRouter } = await import('@/routes/regions.js');

type Claims = Record<string, unknown>;

function appFor(claims: Claims) {
  const app = express();
  app.use(express.json());
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.use('/api/regions', regionsRouter);
  return app;
}

const admin: Claims = { roles: ['CloudImaging.Administrator'] };
const technician: Claims = { roles: ['CloudImaging.Technician'] };
const reader: Claims = { roles: ['CloudImaging.Reader'] };

describe('Portal backend: region routes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    operatorApi.getRegions.mockResolvedValue({ regions: [], countries: [] });
    operatorApi.putRegions.mockResolvedValue(undefined);
  });

  it.each([
    ['Administrator', admin, 200],
    ['Technician', technician, 403],
    ['Reader', reader, 403],
  ])('shows the mapping to %s with status %i', async (_name, claims, status) => {
    const res = await request(appFor(claims)).get('/api/regions');
    expect(res.status).toBe(status);
  });

  it('forwards a save from an administrator and returns 204', async () => {
    const body = { regions: [{ regionId: 'amer', code: 'AMCS' }], countryOverrides: { MX: null } };
    const res = await request(appFor(admin)).put('/api/regions').send(body);
    expect(res.status).toBe(204);
    expect(operatorApi.putRegions).toHaveBeenCalledWith(body);
  });

  it('refuses a save from a technician', async () => {
    const res = await request(appFor(technician)).put('/api/regions').send({ regions: [] });
    expect(res.status).toBe(403);
    expect(operatorApi.putRegions).not.toHaveBeenCalled();
  });
});
