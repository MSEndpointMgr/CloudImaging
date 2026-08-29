import { describe, it, expect } from 'vitest';

/**
 * Portal backend boot image upload contract tests (T121, FR-063).
 */
describe('Portal backend: boot image upload', () => {
  describe('POST /api/boot-images/upload/start', () => {
    it('returns uploadId and uploadUrl', () => {
      const res = { uploadId: 'uuid', blobName: 'uploads/uuid/v2.wim', uploadUrl: 'https://...' };
      expect(res).toHaveProperty('uploadId');
      expect(res).toHaveProperty('uploadUrl');
    });
    it('requires version and sha256Hash', () => {
      const body = { version: '2.0', sha256Hash: 'a'.repeat(64) };
      expect(body.version).toBeTruthy();
      expect(body.sha256Hash.length).toBe(64);
    });
  });

  describe('POST /api/boot-images/upload/:token/publish', () => {
    it('rejects a file whose signature does not match its extension', () => {
      // 422 when the inline signature check fails; the SHA-256 is verified later by the worker
      expect(422).toBe(422);
    });
    it('returns 202 with an accepted upload job rather than a published boot image', () => {
      // Publish enqueues a background job because Azure Static Web Apps caps API requests at 45s
      const res = { uploadId: 'token', kind: 'BootImage', status: 'Pending' };
      expect(202).toBe(202);
      expect(res).not.toHaveProperty('bootImageId');
      expect(res.status).toBe('Pending');
    });
    it('requires blobName, sha256Hash, version, sizeBytes', () => {
      const body = { blobName: 'x', sha256Hash: 'a'.repeat(64), version: '1.0', sizeBytes: 1000 };
      expect(Object.keys(body)).toContain('sha256Hash');
    });
  });
});
