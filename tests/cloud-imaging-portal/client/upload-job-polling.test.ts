import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * Covers the publish-status polling that backs all three image upload flows.
 *
 * The OS, boot and recovery publish endpoints return 202 Accepted and hand the expensive
 * verification work to a background worker, because Azure Static Web Apps terminates every API
 * request at a fixed 45 seconds. waitForUploadJob is what turns that back into a single await
 * for the upload dialogs, so its terminal-state and error-tolerance behaviour is what decides
 * whether an operator sees "published", a real failure reason, or a spurious error.
 */

const apiFetch = vi.fn();

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch: (...args: unknown[]) => apiFetch(...args) as unknown,
  extractErrorDetail: (_res: unknown, fallback: string) => Promise.resolve(fallback),
}));

const { waitForUploadJob } = await import(
  '../../../src/cloud-imaging-portal/client/src/services/uploadJobService.ts'
);

function jobResponse(status: string, extra: Record<string, unknown> = {}) {
  return {
    ok: true,
    status: 200,
    json: () => Promise.resolve({ uploadId: 'u1', kind: 'OsImage', status, ...extra }),
  };
}

function errorResponse(status: number) {
  return { ok: false, status, json: () => Promise.resolve({}) };
}

describe('waitForUploadJob', () => {
  beforeEach(() => { apiFetch.mockReset(); });
  afterEach(() => { vi.useRealTimers(); });

  it('resolves once the job reaches Completed', async () => {
    apiFetch
      .mockResolvedValueOnce(jobResponse('Pending'))
      .mockResolvedValueOnce(jobResponse('Processing'))
      .mockResolvedValueOnce(jobResponse('Completed', { resultImageId: 'img-1' }));

    const statuses: string[] = [];
    const job = await waitForUploadJob('u1', {
      pollIntervalMs: 0,
      onStatus: j => statuses.push(j.status),
    });

    expect(job.status).toBe('Completed');
    expect(job.resultImageId).toBe('img-1');
    expect(statuses).toEqual(['Pending', 'Processing', 'Completed']);
  });

  it('rejects with the worker failure reason when the job fails', async () => {
    apiFetch.mockResolvedValueOnce(
      jobResponse('Failed', { failureReason: 'Checksum mismatch: the uploaded file is corrupt.' }),
    );

    await expect(waitForUploadJob('u1', { pollIntervalMs: 0 }))
      .rejects.toThrow('Checksum mismatch: the uploaded file is corrupt.');
  });

  it('tolerates transient status-read failures and keeps polling', async () => {
    apiFetch
      .mockResolvedValueOnce(errorResponse(503))
      .mockResolvedValueOnce(errorResponse(503))
      .mockResolvedValueOnce(jobResponse('Completed'));

    const job = await waitForUploadJob('u1', { pollIntervalMs: 0 });
    expect(job.status).toBe('Completed');
    expect(apiFetch).toHaveBeenCalledTimes(3);
  });

  it('gives up after repeated status-read failures instead of polling forever', async () => {
    apiFetch.mockResolvedValue(errorResponse(500));

    await expect(waitForUploadJob('u1', { pollIntervalMs: 0 }))
      .rejects.toThrow('Failed to read publish status');
    expect(apiFetch).toHaveBeenCalledTimes(5);
  });

  it('stops polling when the caller aborts', async () => {
    const controller = new AbortController();
    apiFetch.mockImplementation(() => {
      controller.abort();
      return Promise.resolve(jobResponse('Processing'));
    });

    await expect(waitForUploadJob('u1', { pollIntervalMs: 0, signal: controller.signal }))
      .rejects.toThrow('Publish tracking cancelled');
  });
});
