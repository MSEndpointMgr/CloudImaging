import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  getWifiNetworks: vi.fn(),
  createWifiNetwork: vi.fn(),
  updateWifiNetwork: vi.fn(),
  deleteWifiNetwork: vi.fn(),
  setWifiNetworkOrder: vi.fn(),
  setLocationWifiNetworks: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { wifiNetworksRouter } = await import('@/routes/wifi-networks.js');
const { locationsRouter } = await import('@/routes/locations.js');

type Claims = Record<string, unknown>;

function appFor(claims: Claims) {
  const app = express();
  app.use(express.json());
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = claims as AuthenticatedRequest['user']; next(); });
  app.use('/api/wifi-networks', wifiNetworksRouter);
  app.use('/api/locations', locationsRouter);
  return app;
}

const admin: Claims = { roles: ['CloudImaging.Administrator'], preferred_username: 'anna@contoso.com' };
const technician: Claims = { roles: ['CloudImaging.Technician'] };
const networkId = '6f1c2a3e-4b5d-4e6f-8a9b-0c1d2e3f4a5b';
const locationId = '1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d';

describe('Portal backend: Wi-Fi network routes', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    operatorApi.getWifiNetworks.mockResolvedValue([{ networkId, ssid: 'Contoso-Imaging' }]);
    operatorApi.createWifiNetwork.mockResolvedValue({ networkId, ssid: 'Contoso-Imaging' });
    operatorApi.updateWifiNetwork.mockResolvedValue({ networkId, ssid: 'Contoso-Imaging' });
  });

  it.each([
    ['get', '/api/wifi-networks'],
    ['post', '/api/wifi-networks'],
    ['put', `/api/wifi-networks/${networkId}`],
    ['delete', `/api/wifi-networks/${networkId}`],
    ['put', '/api/wifi-networks/order'],
    ['put', `/api/locations/${locationId}/wifi-networks`],
  ] as const)('refuses %s %s to a technician', async (method, path) => {
    const res = await request(appFor(technician))[method](path).send({});
    expect(res.status).toBe(403);
  });

  it('forwards only known fields and stamps the signed-in administrator as the password changer', async () => {
    const res = await request(appFor(admin)).post('/api/wifi-networks').send({
      ssid: 'Contoso-Imaging', security: 'Wpa3Personal', password: 'correct horse', allLocations: true, changedBy: 'mallory', passwordChangedAt: 'x',
    });

    expect(res.status).toBe(201);
    expect(operatorApi.createWifiNetwork).toHaveBeenCalledWith({
      ssid: 'Contoso-Imaging', security: 'Wpa3Personal', password: 'correct horse', allLocations: true, changedBy: 'anna@contoso.com',
    });
    expect(JSON.stringify(res.body)).not.toContain('correct horse');
  });

  it('forwards an update to the right network', async () => {
    const res = await request(appFor(admin)).put(`/api/wifi-networks/${networkId}`).send({ ssid: 'Lab', security: 'Wpa2Personal' });

    expect(res.status).toBe(200);
    expect(operatorApi.updateWifiNetwork).toHaveBeenCalledWith(networkId, { ssid: 'Lab', security: 'Wpa2Personal', changedBy: 'anna@contoso.com' });
  });

  it('rejects a malformed network id', async () => {
    const res = await request(appFor(admin)).delete('/api/wifi-networks/not-a-guid');
    expect(res.status).toBe(400);
    expect(operatorApi.deleteWifiNetwork).not.toHaveBeenCalled();
  });

  it('forwards the order and a location selection as id lists', async () => {
    await request(appFor(admin)).put('/api/wifi-networks/order').send({ networkIds: [networkId] }).expect(204);
    await request(appFor(admin)).put(`/api/locations/${locationId}/wifi-networks`).send({ networkIds: 'nope' }).expect(204);

    expect(operatorApi.setWifiNetworkOrder).toHaveBeenCalledWith({ networkIds: [networkId] });
    expect(operatorApi.setLocationWifiNetworks).toHaveBeenCalledWith(locationId, { networkIds: [] });
  });
});
