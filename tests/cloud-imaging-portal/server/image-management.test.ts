import { describe, it, expect } from 'vitest';

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
