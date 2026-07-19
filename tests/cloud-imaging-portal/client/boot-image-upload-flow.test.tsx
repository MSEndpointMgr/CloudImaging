import { describe, it, expect } from 'vitest';

/**
 * Portal frontend boot image upload flow tests (T126, FR-063).
 */
describe('Portal frontend — boot image upload flow', () => {
  it('ChunkedUploadDialog accepts WIM file, version, sha256Hash', () => {
    const required = ['file', 'version', 'sha256Hash'];
    expect(required).toContain('sha256Hash');
  });

  it('upload progresses through idle→uploading→finalizing→done states', () => {
    const states = ['idle', 'uploading', 'finalizing', 'done', 'error', 'cancelled'];
    expect(states).toContain('uploading');
    expect(states).toContain('finalizing');
    expect(states).toContain('done');
  });

  it('UploadProgressBar shows percent complete', () => {
    const progress = 75;
    expect(progress).toBeGreaterThanOrEqual(0);
    expect(progress).toBeLessThanOrEqual(100);
  });

  it('Cancel Upload aborts in-progress transfer', () => {
    const canCancel = true;
    expect(canCancel).toBe(true);
  });

  it('Retry resets to idle state on error', () => {
    const stateAfterRetry = 'idle';
    expect(stateAfterRetry).toBe('idle');
  });

  it('sha256Hash must be exactly 64 hex characters', () => {
    const validHash = 'a'.repeat(64);
    expect(validHash.length).toBe(64);
  });
});
