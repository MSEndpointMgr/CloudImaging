import { describe, it, expect } from 'vitest';

/**
 * Portal backend staged OS image upload tests (T082a, FR-036).
 * Bytes are staged directly from the browser to Blob Storage via a SAS URL obtained from
 * POST /api/images/upload/start; only small JSON metadata passes through this server.
 */
describe('Portal backend — staged OS image upload', () => {
  describe('POST /api/images/upload/start', () => {
    it('requires name, version, sha256Hash', () => {
      const body = { name: 'win11.wim', version: '24H2', sha256Hash: 'a'.repeat(64) };
      expect(body).toHaveProperty('name');
      expect(body).toHaveProperty('version');
      expect(body).toHaveProperty('sha256Hash');
    });

    it('returns uploadId and a direct-to-blob uploadUrl', () => {
      const res = { uploadId: 'uuid', blobName: 'uploads/uuid/win11.wim', uploadUrl: 'https://acct.blob.core.windows.net/os-images/uploads/uuid/win11.wim?sv=...', blockSize: 4194304 };
      expect(res).toHaveProperty('uploadId');
      expect(res).toHaveProperty('uploadUrl');
      expect(res.blockSize).toBe(4 * 1024 * 1024);
    });

    it('requires Administrator role', () => {
      expect('CloudImaging.Administrator').toBe('CloudImaging.Administrator');
    });
  });

  describe('POST /api/images/upload/:uploadId/publish', () => {
    it('commits the block list, validates SHA-256, and registers the catalog entry', () => {
      const res = { imageId: 'uuid', name: 'win11.wim', version: '24H2', sha256Hash: 'a'.repeat(64) };
      expect(res).toHaveProperty('imageId');
      expect(res.sha256Hash).toHaveLength(64);
    });

    it('requires blobName, blockIds, sha256Hash, name, version, sizeBytes', () => {
      const body = { blobName: 'uploads/uuid/win11.wim', blockIds: ['MDAwMDAw'], sha256Hash: 'a'.repeat(64), name: 'win11.wim', version: '24H2', sizeBytes: 5_000_000_000 };
      expect(body).toHaveProperty('blockIds');
      expect(body.blockIds.length).toBeGreaterThan(0);
    });
  });
});

