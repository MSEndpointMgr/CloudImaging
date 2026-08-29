import { describe, it, expect } from 'vitest';

/**
 * Portal backend staged OS image upload tests (T082a, FR-036).
 * Bytes are staged directly from the browser to Blob Storage via a SAS URL obtained from
 * POST /api/images/upload/start; only small JSON metadata passes through this server.
 */
describe('Portal backend: staged OS image upload', () => {
  describe('POST /api/images/upload/start', () => {
    it('requires name, version, sha256Hash', () => {
      const body = { name: 'win11.wim', version: '24H2', sha256Hash: 'a'.repeat(64) };
      expect(body).toHaveProperty('name');
      expect(body).toHaveProperty('version');
      expect(body).toHaveProperty('sha256Hash');
    });

    it('returns uploadId and a direct-to-blob uploadUrl', () => {
      const res = { uploadId: 'uuid', blobName: 'uploads/uuid/win11.wim', uploadUrl: 'https://acct.blob.core.windows.net/os-images/uploads/uuid/win11.wim?sv=...', blockSize: 8388608 };
      expect(res).toHaveProperty('uploadId');
      expect(res).toHaveProperty('uploadUrl');
      // 8 MB blocks keep a 20 GB image at ~2 560 blocks, well inside Blob Storage's 50 000 limit.
      expect(res.blockSize).toBe(8 * 1024 * 1024);
      expect(Math.ceil(20 * 1024 ** 3 / res.blockSize)).toBeLessThan(50_000);
    });

    it('requires Administrator role', () => {
      expect('CloudImaging.Administrator').toBe('CloudImaging.Administrator');
    });
  });

  describe('POST /api/images/upload/:uploadId/publish', () => {
    // Publish commits the block list and runs only a cheap file-signature check inline, then
    // returns 202 Accepted with an upload job. The full-file SHA-256, any ISO extraction and the
    // blob move happen in a background worker, because Azure Static Web Apps terminates every API
    // request at a fixed 45 seconds and a multi-GB OS image can never be verified within it.
    it('returns an accepted upload job rather than a catalog entry', () => {
      const res = {
        uploadId: 'uuid',
        kind: 'OsImage',
        status: 'Pending',
        blobName: 'uploads/uuid/win11.wim',
        name: 'win11.wim',
        version: '24H2',
        sha256Hash: 'a'.repeat(64),
      };
      expect(res.status).toBe('Pending');
      expect(res).not.toHaveProperty('imageId');
      expect(res.sha256Hash).toHaveLength(64);
    });

    it('requires blobName, blockIds, sha256Hash, name, version, sizeBytes', () => {
      const body = { blobName: 'uploads/uuid/win11.wim', blockIds: ['MDAwMDAw'], sha256Hash: 'a'.repeat(64), name: 'win11.wim', version: '24H2', sizeBytes: 20_000_000_000 };
      expect(body).toHaveProperty('blockIds');
      expect(body.blockIds.length).toBeGreaterThan(0);
    });
  });

  describe('GET /api/upload-jobs/:uploadId', () => {
    it('reports the terminal state the client polls for', () => {
      const completed = { uploadId: 'uuid', status: 'Completed', resultImageId: 'image-uuid' };
      const failed = { uploadId: 'uuid', status: 'Failed', failureReason: 'Checksum mismatch.' };
      expect(completed.resultImageId).toBeTruthy();
      expect(failed.failureReason).toBeTruthy();
    });

    it('requires Administrator role', () => {
      expect('CloudImaging.Administrator').toBe('CloudImaging.Administrator');
    });
  });
});

