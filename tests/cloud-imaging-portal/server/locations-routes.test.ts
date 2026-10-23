import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import { AxiosError, AxiosHeaders, type InternalAxiosRequestConfig } from 'axios';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  deleteLocation: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { locationsRouter } = await import('@/routes/locations.js');
const { errorHandler } = await import('@/middleware/errorHandler.js');

const locationId = '1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d';
const operator = { roles: ['CloudImaging.Operator'], preferred_username: 'olle@contoso.com' };

function appFor(claims: Record<string, unknown>) {
  const app = express();
  app.use(express.json());
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.use('/api/locations', locationsRouter);
  app.use(errorHandler);
  return app;
}

describe('Portal backend: location delete', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  it('forwards changedBy from the token', async () => {
    operatorApi.deleteLocation.mockResolvedValue(undefined);

    const res = await request(appFor(operator)).delete(`/api/locations/${locationId}?changedBy=mallory`);

    expect(res.status).toBe(204);
    expect(operatorApi.deleteLocation).toHaveBeenCalledWith(locationId, 'olle@contoso.com');
  });

  it('relays an upstream 409 with its detail', async () => {
    const detail = 'Stockholm HQ is the only location of setup profile "Stockholm". Add another location to the profile or delete the profile, then try again.';
    const config = { headers: new AxiosHeaders() } as InternalAxiosRequestConfig;
    operatorApi.deleteLocation.mockRejectedValue(new AxiosError('Conflict', 'ERR_BAD_REQUEST', config, undefined, { status: 409, statusText: '', headers: {}, config, data: { status: 409, detail } }));

    const res = await request(appFor(operator)).delete(`/api/locations/${locationId}`);

    expect(res.status).toBe(409);
    expect(res.body.detail).toBe(detail);
  });
});
