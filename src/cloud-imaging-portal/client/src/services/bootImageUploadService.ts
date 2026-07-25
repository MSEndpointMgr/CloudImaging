/**
 * Boot image upload service for the portal client (T128, FR-063).
 * Handles the 2-step staged upload: get a blob SAS URL, then PUT the file directly
 * to Azure Blob Storage, and finally call the publish endpoint.
 */

import { apiFetch } from '../lib/apiClient.ts';

export interface UploadSession {
  uploadId: string;
  blobName: string;
  uploadUrl: string;
  sha256Hash: string;
  expiresAt: string;
}

export interface UploadOptions {
  version: string;
  sha256Hash: string;
  sizeBytes: number;
  file: File;
  onProgress?: (percent: number) => void;
}

/**
 * Starts a staged boot image upload session.
 */
export async function startBootImageUpload(
  version: string,
  sha256Hash: string,
): Promise<UploadSession> {
  const res = await apiFetch('/api/boot-images/upload/start', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ version, sha256Hash }),
  });
  if (!res.ok) throw new Error(`Upload start failed: HTTP ${res.status}`);
  return res.json() as Promise<UploadSession>;
}

/**
 * Uploads the file directly to Azure Blob Storage using the SAS URL.
 * Reports progress via XMLHttpRequest so the UI can show a progress bar.
 */
export async function uploadFileToBlobStorage(
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
 * Finalizes a staged upload by validating the SHA-256 and publishing the boot image.
 */
export async function publishBootImageUpload(
  session: UploadSession,
  sizeBytes: number,
  version: string,
): Promise<unknown> {
  const res = await apiFetch(`/api/boot-images/upload/${session.uploadId}/publish`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({
      blobName: session.blobName,
      sha256Hash: session.sha256Hash,
      version,
      sizeBytes,
    }),
  });
  if (!res.ok) {
    const msg = await res.text().catch(() => `HTTP ${res.status}`);
    throw new Error(msg);
  }
  return res.json();
}
