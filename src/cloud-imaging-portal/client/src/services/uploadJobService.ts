/**
 * Polls the status of a background image publish job.
 *
 * The OS, boot and recovery image publish endpoints all answer 202 Accepted: they only run a
 * cheap file-signature check inline and hand the expensive work (full-file SHA-256 verification,
 * ISO extraction, blob move, catalog write) to a worker in the Imaging Core API. That split is
 * mandatory because Azure Static Web Apps terminates every API request at a fixed 45 seconds and
 * returns "Backend call failure", which multi-GB OS images can never meet.
 */

import { apiFetch, extractErrorDetail } from '../lib/apiClient.ts';

export type UploadJobStatus = 'Pending' | 'Processing' | 'Completed' | 'Failed';

export interface UploadJob {
  uploadId: string;
  kind: 'OsImage' | 'BootImage' | 'RecoveryImage';
  status: UploadJobStatus;
  blobName: string;
  sha256Hash: string;
  version: string;
  name?: string | null;
  description?: string | null;
  sizeBytes: number;
  createdAt: string;
  updatedAt: string;
  failureReason?: string | null;
  resultImageId?: string | null;
  attemptCount: number;
}

const POLL_INTERVAL_MS = 3_000;

/** Reads the current state of a publish job. */
export async function getUploadJob(uploadId: string): Promise<UploadJob> {
  const res = await apiFetch(`/api/upload-jobs/${encodeURIComponent(uploadId)}`, {
    credentials: 'include',
  });
  if (!res.ok) throw new Error(await extractErrorDetail(res, `Failed to read publish status: HTTP ${res.status}`));
  return res.json() as Promise<UploadJob>;
}

/**
 * Polls until the job reaches a terminal state. Resolves with the completed job, or throws with
 * the worker's failure reason. Transient read errors are tolerated so a brief network blip does
 * not discard an upload that is still being verified server-side.
 */
export async function waitForUploadJob(
  uploadId: string,
  options?: {
    onStatus?: (job: UploadJob) => void;
    signal?: AbortSignal;
    pollIntervalMs?: number;
  },
): Promise<UploadJob> {
  const interval = options?.pollIntervalMs ?? POLL_INTERVAL_MS;
  let consecutiveErrors = 0;

  for (;;) {
    if (options?.signal?.aborted) throw new DOMException('Publish tracking cancelled', 'AbortError');

    try {
      const job = await getUploadJob(uploadId);
      consecutiveErrors = 0;
      options?.onStatus?.(job);

      if (job.status === 'Completed') return job;
      if (job.status === 'Failed') {
        throw new Error(job.failureReason ?? 'Publishing failed. Check the Imaging Core API logs for details.');
      }
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') throw err;
      // A Failed job is reported by throwing above; only retry genuine read failures.
      if (err instanceof Error && !err.message.startsWith('Failed to read publish status')) throw err;
      if (++consecutiveErrors >= 5) throw err;
    }

    await delay(interval, options?.signal);
  }
}

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      reject(new DOMException('Publish tracking cancelled', 'AbortError'));
    };
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}
