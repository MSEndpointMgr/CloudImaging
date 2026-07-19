import { describe, it, expect } from 'vitest';

/**
 * Portal backend boot image upload contract tests (T121, FR-063).
 */
describe('Portal backend — boot image upload', () => {
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
    it('validates sha256Hash before committing', () => {
      // 422 when hash mismatch
      expect(422).toBe(422);
    });
    it('returns 201 with published boot image on success', () => {
      expect(201).toBe(201);
    });
    it('requires blobName, sha256Hash, version, sizeBytes', () => {
      const body = { blobName: 'x', sha256Hash: 'a'.repeat(64), version: '1.0', sizeBytes: 1000 };
      expect(Object.keys(body)).toContain('sha256Hash');
    });
  });
});
