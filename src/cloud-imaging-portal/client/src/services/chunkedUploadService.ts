/**
 * Client-side chunked upload service for large OS images (5–10 GB).
 * Mirrors bootImageUploadService.ts: obtain a single write-SAS URL for the whole blob, then
 * stage each 4 MB block DIRECTLY to Azure Blob Storage from the browser (no proxy through the
 * portal server for the bytes themselves), and finally ask the Imaging Core API to commit the
 * block list + validate SHA-256 + register the OS image catalog entry.
 */

import { apiFetch } from '../lib/apiClient.ts';

export interface ChunkedUploadSession {
  uploadId:  string;
  blobName:  string;
  uploadUrl: string; // blob-level SAS URL, valid for repeated "stage block" + "commit" calls
  blockSize: number;
  expiresAt: string;
}

const BLOCK_SIZE = 4 * 1024 * 1024; // 4 MB

/** Starts a new staged upload session; returns a SAS URL for direct-to-blob block staging. */
export async function startChunkedUpload(
  name: string,
  version: string,
  sha256Hash: string,
): Promise<ChunkedUploadSession> {
  const res = await apiFetch('/api/images/upload/start', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ name, version, sha256Hash }),
  });
  if (!res.ok) throw new Error(`Failed to start upload: HTTP ${res.status}`);
  return res.json() as Promise<ChunkedUploadSession>;
}

/** Stages one block directly to Blob Storage via XHR (so upload progress can be reported). */
function stageBlock(uploadUrl: string, blockId: string, chunk: Blob, signal?: AbortSignal): Promise<void> {
  const target = new URL(uploadUrl);
  target.searchParams.set('comp', 'block');
  target.searchParams.set('blockid', blockId);

  return new Promise<void>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', target.toString());
    xhr.setRequestHeader('Content-Type', 'application/octet-stream');
    xhr.addEventListener('load', () => {
      if (xhr.status >= 200 && xhr.status < 300) resolve();
      else reject(new Error(`Block upload failed: HTTP ${xhr.status}`));
    });
    xhr.addEventListener('error', () => reject(new Error('Network error during block upload.')));
    xhr.addEventListener('abort', () => reject(new DOMException('Upload cancelled', 'AbortError')));
    signal?.addEventListener('abort', () => xhr.abort());
    xhr.send(chunk);
  });
}

/** Uploads all blocks sequentially, directly to Blob Storage; calls onProgress(0-100) after each block. */
export async function uploadBlocks(
  session:    ChunkedUploadSession,
  file:       File,
  onProgress: (percent: number) => void,
  signal?:    AbortSignal,
): Promise<string[]> {
  const blockIds: string[] = [];
  let offset = 0;
  let blockIndex = 0;

  while (offset < file.size) {
    if (signal?.aborted) throw new DOMException('Upload cancelled', 'AbortError');

    const chunk   = file.slice(offset, offset + BLOCK_SIZE);
    const blockId = btoa(String(blockIndex).padStart(6, '0'));

    await stageBlock(session.uploadUrl, blockId, chunk, signal);

    blockIds.push(blockId);
    offset += chunk.size;
    blockIndex++;
    onProgress(Math.round((offset / file.size) * 100));
  }

  return blockIds;
}

/** Finalizes the upload: commits the block list, validates SHA-256, and registers the catalog entry. */
export async function finalizeChunkedUpload(
  session:    ChunkedUploadSession,
  blockIds:   string[],
  name:       string,
  version:    string,
  sha256Hash: string,
  sizeBytes:  number,
): Promise<unknown> {
  const res = await apiFetch(`/api/images/upload/${session.uploadId}/publish`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ blobName: session.blobName, blockIds, name, version, sha256Hash, sizeBytes }),
  });
  if (!res.ok) {
    const msg = await res.text().catch(() => `HTTP ${res.status}`);
    throw new Error(msg || `Finalize failed: HTTP ${res.status}`);
  }
  return res.json();
}

