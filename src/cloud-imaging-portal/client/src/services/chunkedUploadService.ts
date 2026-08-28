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
  options?: {
    /** Block IDs already staged in a previous attempt (resume across a tab close/reload). */
    resumeBlockIds?: string[];
    /** Invoked after each block is staged so the caller can checkpoint progress for resume. */
    onBlockStaged?: (blockIds: string[]) => void;
  },
): Promise<string[]> {
  const blockIds: string[] = options?.resumeBlockIds ? [...options.resumeBlockIds] : [];
  let blockIndex = blockIds.length;
  let offset = blockIndex * BLOCK_SIZE;

  if (offset > 0) onProgress(Math.round((offset / file.size) * 100));

  while (offset < file.size) {
    if (signal?.aborted) throw new DOMException('Upload cancelled', 'AbortError');

    const chunk   = file.slice(offset, offset + BLOCK_SIZE);
    const blockId = btoa(String(blockIndex).padStart(6, '0'));

    await stageBlock(session.uploadUrl, blockId, chunk, signal);

    blockIds.push(blockId);
    offset += chunk.size;
    blockIndex++;
    onProgress(Math.round((offset / file.size) * 100));
    options?.onBlockStaged?.(blockIds);
  }

  return blockIds;
}

/**
 * Best-effort cleanup for a cancelled/discarded upload: deletes the uncommitted staged blob so
 * it doesn't linger in the container until Azure Storage's ~7-day uncommitted-block GC kicks in.
 * Never throws — cleanup failures shouldn't block the operator from dismissing the dialog.
 */
export async function abandonChunkedUpload(session: Pick<ChunkedUploadSession, 'uploadId' | 'blobName'>): Promise<void> {
  try {
    await apiFetch(`/api/images/upload/${session.uploadId}/abandon`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify({ blobName: session.blobName }),
    });
  } catch {
    // Best-effort — the uncommitted blocks will be garbage-collected by Azure Storage regardless.
  }
}

// ── Resumable-upload checkpoint (localStorage) ───────────────────────────────
// Persists just enough state to resume a staged upload after the browser tab is closed/reloaded:
// the SAS session details, the file identity to match against re-selection, and which blocks were
// already staged. The File object itself can never be persisted, so resuming requires the operator
// to re-select the same file — matched by name + size + last-modified timestamp.

const PERSISTED_UPLOAD_KEY = 'ci-os-image-upload-session';

export interface PersistedUploadState extends ChunkedUploadSession {
  version: string;
  sha256: string;
  fileName: string;
  fileSize: number;
  fileLastModified: number;
  stagedBlockIds: string[];
}

export function savePersistedUpload(state: PersistedUploadState): void {
  try { localStorage.setItem(PERSISTED_UPLOAD_KEY, JSON.stringify(state)); }
  catch { /* localStorage unavailable/full — resume just won't be offered next time */ }
}

/** Loads a persisted upload checkpoint, discarding it if its staging SAS URL has already expired. */
export function loadPersistedUpload(): PersistedUploadState | null {
  try {
    const raw = localStorage.getItem(PERSISTED_UPLOAD_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as PersistedUploadState;
    if (new Date(parsed.expiresAt).getTime() <= Date.now()) {
      clearPersistedUpload();
      return null;
    }
    return parsed;
  } catch {
    return null;
  }
}

export function clearPersistedUpload(): void {
  try { localStorage.removeItem(PERSISTED_UPLOAD_KEY); }
  catch { /* ignore */ }
}

/** True when `file` is (almost certainly) the same file the persisted checkpoint was staged from. */
export function matchesPersistedUpload(state: PersistedUploadState, file: File): boolean {
  return state.fileName === file.name
    && state.fileSize === file.size
    && state.fileLastModified === file.lastModified;
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

