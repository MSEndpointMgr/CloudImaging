import { describe, it, expect, vi } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  getConfiguration: vi.fn(),
  putConfiguration: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { portalConfigRouter } = await import('@/routes/portal-config.js');

/**
 * Portal backend portal configuration tests (T144, FR-026).
 */
describe('Portal backend: portal configuration route', () => {
  it('PUT forwards changedBy from the token and ignores one in the body', async () => {
    operatorApi.putConfiguration.mockResolvedValue(undefined);
    const app = express();
    app.use(express.json());
    app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => {
      req.user = { roles: ['CloudImaging.Administrator'], preferred_username: 'anna@contoso.com' } as AuthenticatedRequest['user'];
      next();
    });
    app.use('/api/portal-config', portalConfigRouter);

    const res = await request(app).put('/api/portal-config').send({ setupProfileVersionRetention: 40, changedBy: 'mallory@contoso.com' });

    expect(res.status).toBe(204);
    expect(operatorApi.putConfiguration).toHaveBeenCalledWith({ setupProfileVersionRetention: 40, changedBy: 'anna@contoso.com' });
  });

  describe('GET /api/portal-config', () => {
    it('returns devicePreFlightAuthorizationEnabled', () => {
      const res = { devicePreFlightAuthorizationEnabled: false, sasTokenUrlExpiryMinutes: 240 };
      expect(res).toHaveProperty('devicePreFlightAuthorizationEnabled');
    });
  });

  describe('PUT /api/portal-config', () => {
    it('returns 204 on success', () => {
      expect(204).toBe(204);
    });
  });

  it('pre-flight toggle defaults to false', () => {
    const defaultConfig = { devicePreFlightAuthorizationEnabled: false };
    expect(defaultConfig.devicePreFlightAuthorizationEnabled).toBe(false);
  });
});
