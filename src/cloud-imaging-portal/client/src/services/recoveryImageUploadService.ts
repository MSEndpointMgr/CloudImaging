/**
 * Recovery image upload service for the portal client.
 * Handles the 2-step staged upload: get a blob SAS URL, then PUT the file directly
 * to Azure Blob Storage, and finally call the publish endpoint. Mirrors
 * bootImageUploadService.ts.
 */

import { apiFetch, extractErrorDetail } from '../lib/apiClient.ts';
import { waitForUploadJob, type UploadJob } from './uploadJobService.ts';

export interface RecoveryUploadSession {
  uploadId: string;
  blobName: string;
  uploadUrl: string;
  sha256Hash: string;
  expiresAt: string;
}

/**
 * Starts a staged recovery image upload session.
 */
export async function startRecoveryImageUpload(
  version: string,
  sha256Hash: string,
  fileName: string,
): Promise<RecoveryUploadSession> {
  const res = await apiFetch('/api/recovery-images/upload/start', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ version, sha256Hash, fileName }),
  });
  if (!res.ok) {
    throw new Error(await extractErrorDetail(res, `Upload start failed: HTTP ${res.status}`));
  }
  return res.json() as Promise<RecoveryUploadSession>;
}

/**
 * Uploads the file directly to Azure Blob Storage using the SAS URL.
 * Reports progress via XMLHttpRequest so the UI can show a progress bar.
 */
export async function uploadRecoveryFileToBlobStorage(
  uploadUrl: string,
  file: File,
  onProgress?: (percent: number) => void,
): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', uploadUrl);
    xhr.setRequestHeader('x-ms-blob-type', 'BlockBlob');
    xhr.setRequestHeader('Content-Type', 'application/octet-stream');

    xhr.upload.addEventListener('progress', (e) => {
      if (e.lengthComputable) {
        onProgress?.(Math.round((e.loaded / e.total) * 100));
      }
    });

    xhr.addEventListener('load', () => {
      if (xhr.status >= 200 && xhr.status < 300) resolve();
      else reject(new Error(`Blob upload failed: HTTP ${xhr.status}`));
    });
    xhr.addEventListener('error', () => reject(new Error('Network error during blob upload.')));
    xhr.send(file);
  });
}

/**
 * Finalizes a staged upload. The server checks the file signature inline, then verifies the
 * SHA-256 and publishes in a background job (see uploadJobService), because hashing a large WIM
 * cannot finish inside the fixed 45 second Static Web Apps request cap. This resolves once the
 * recovery image is actually in the catalog.
 */
export async function publishRecoveryImageUpload(
  session: RecoveryUploadSession,
  sizeBytes: number,
  version: string,
  description?: string,
  options?: { onStatus?: (job: UploadJob) => void; signal?: AbortSignal },
): Promise<UploadJob> {
  const res = await apiFetch(`/api/recovery-images/upload/${session.uploadId}/publish`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({
      blobName: session.blobName,
      sha256Hash: session.sha256Hash,
      version,
      sizeBytes,
      description: description?.trim() || undefined,
    }),
  });
  if (!res.ok) {
    throw new Error(await extractErrorDetail(res, `Publish failed: HTTP ${res.status}`));
  }

  const job = await res.json() as UploadJob;
  options?.onStatus?.(job);
  return waitForUploadJob(job.uploadId, options);
}
