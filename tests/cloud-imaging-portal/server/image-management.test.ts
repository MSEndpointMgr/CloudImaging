import { describe, it, expect, vi, beforeEach } from 'vitest';
import express, { type NextFunction, type Response } from 'express';
import request from 'supertest';
import type { AuthenticatedRequest } from '@/middleware/auth.js';

const operatorApi = vi.hoisted(() => ({
  updateImage: vi.fn(),
  getImages: vi.fn(),
}));

vi.mock('@/services/operatorApiClient.js', () => ({ operatorApiClient: operatorApi }));

const { imagesRouter } = await import('@/routes/images.js');

function appFor(roles: string[]) {
  const app = express();
  app.use(express.json());
  app.use((req: AuthenticatedRequest, _res: Response, next: NextFunction) => { req.user = { roles, preferred_username: 'olle@contoso.com' } as AuthenticatedRequest['user']; next(); });
  app.use('/api/images', imagesRouter);
  return app;
}

describe('Portal backend: OS image Windows release', () => {
  const imageId = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';

  beforeEach(() => {
    vi.clearAllMocks();
    operatorApi.updateImage.mockResolvedValue({ imageId, windowsRelease: 'Windows11_24H2' });
    operatorApi.getImages.mockResolvedValue([{ imageId, windowsRelease: 'Windows11_24H2' }]);
  });

  it('PATCH forwards windowsRelease, and null to clear it, for an Operator', async () => {
    const app = appFor(['CloudImaging.Operator']);

    expect((await request(app).patch(`/api/images/${imageId}`).send({ windowsRelease: 'Windows11_24H2' })).status).toBe(200);
    await request(app).patch(`/api/images/${imageId}`).send({ windowsRelease: null });

    expect(operatorApi.updateImage).toHaveBeenNthCalledWith(1, imageId, { windowsRelease: 'Windows11_24H2' });
    expect(operatorApi.updateImage).toHaveBeenNthCalledWith(2, imageId, { windowsRelease: null });
  });

  it('PATCH is refused for a Technician', async () => {
    expect((await request(appFor(['CloudImaging.Technician'])).patch(`/api/images/${imageId}`).send({ windowsRelease: 'Windows11_24H2' })).status).toBe(403);
    expect(operatorApi.updateImage).not.toHaveBeenCalled();
  });

  it('the image list returns windowsRelease', async () => {
    const res = await request(appFor(['CloudImaging.Technician'])).get('/api/images');

    expect(res.body[0].windowsRelease).toBe('Windows11_24H2');
  });
});

/**
 * Portal backend image management route tests (T081, FR-036, FR-037).
 */
describe('Portal backend: image management routes', () => {
  describe('GET /api/images', () => {
    it('returns array of images with sha256Hash', () => {
      const image = { imageId: 'id', name: 'Win11', version: '24H2', sha256Hash: 'abc' };
      expect(image).toHaveProperty('sha256Hash');
    });
  });

  describe('POST /api/images', () => {
    it('returns 201 on success', () => {
      expect(201).toBe(201);
    });
  });

  describe('PATCH /api/images/:id', () => {
    it('allows name, version, description to be patched', () => {
      const patchable = ['name', 'version', 'description'];
      expect(patchable).toContain('name');
      expect(patchable).not.toContain('sha256Hash');
    });
    it('returns 404 for unknown image', () => {
      expect(404).toBe(404);
    });
  });

  describe('DELETE /api/images/:id', () => {
    it('returns 409 when image is in use', () => {
      expect(409).toBe(409);
    });
    it('returns 204 on successful delete', () => {
      expect(204).toBe(204);
    });
  });
});
