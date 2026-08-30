import { describe, it, expect } from 'vitest';
import {
  uploadJobProgressPercent,
  uploadJobStageLabel,
  type UploadJob,
} from '../../../src/cloud-imaging-portal/client/src/services/uploadJobService.ts';

function job(overrides: Partial<UploadJob>): UploadJob {
  return {
    uploadId: 'u1',
    kind: 'OsImage',
    status: 'Processing',
    blobName: 'staged/u1/image.iso',
    sha256Hash: 'a'.repeat(64),
    version: '23H2',
    sizeBytes: 1,
    createdAt: '2026-08-30T10:00:00Z',
    updatedAt: '2026-08-30T10:00:00Z',
    attemptCount: 1,
    stage: 'Verifying',
    progressPercent: 0,
    ...overrides,
  };
}

/**
 * The publish half of an upload runs server-side and can take minutes on a multi-GB image, so the
 * dialog must render the worker's reported stage and percent rather than pinning the bar at 100%.
 */
describe('uploadJobService publish progress helpers', () => {
  it('labels each publish stage distinctly', () => {
    expect(uploadJobStageLabel(job({ stage: 'Verifying' }))).toMatch(/checksum/i);
    expect(uploadJobStageLabel(job({ stage: 'Extracting' }))).toMatch(/ISO/i);
    expect(uploadJobStageLabel(job({ stage: 'Publishing' }))).toMatch(/catalog/i);
  });

  it('shows the queued label until the worker claims the job', () => {
    expect(uploadJobStageLabel(null)).toMatch(/queued/i);
    expect(uploadJobStageLabel(job({ status: 'Pending', stage: 'Queued' }))).toMatch(/queued/i);
  });

  it('reports the worker percent while processing', () => {
    expect(uploadJobProgressPercent(job({ progressPercent: 37 }))).toBe(37);
  });

  it('reads 0 before the first job status arrives, never inheriting the upload stage bar', () => {
    expect(uploadJobProgressPercent(null)).toBe(0);
  });

  it('reads 100 once completed even if the final progress report was lost', () => {
    expect(uploadJobProgressPercent(job({ status: 'Completed', progressPercent: 82 }))).toBe(100);
  });

  it('clamps out-of-range values reported by an older or faulty backend', () => {
    expect(uploadJobProgressPercent(job({ progressPercent: 140 }))).toBe(100);
    expect(uploadJobProgressPercent(job({ progressPercent: -5 }))).toBe(0);
  });
});
