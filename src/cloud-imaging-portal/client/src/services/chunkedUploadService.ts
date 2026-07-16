/**
 * Client-side chunked upload service for large OS images (T087a, FR-036).
 * Splits a File into blocks and uploads them sequentially with resume support.
 */

export interface ChunkedUploadSession {
  sessionId:  string;
  blobName:   string;
  uploadUrl:  string;
  blockSize:  number;
  totalBytes: number;
  expiresAt:  string;
}

const BLOCK_SIZE = 4 * 1024 * 1024; // 4 MB

/** Starts a new chunked upload session on the portal server. */
export async function startChunkedUpload(
  imageName: string,
  version:   string,
  totalBytes: number,
): Promise<ChunkedUploadSession> {
  const res = await fetch('/api/chunked-upload/start', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ imageName, version, totalBytes }),
  });
  if (!res.ok) throw new Error(`Failed to start upload: HTTP ${res.status}`);
  return res.json() as Promise<ChunkedUploadSession>;
}

/** Uploads all blocks sequentially; calls onProgress(0-100) after each block. */
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

    const res = await fetch(
      `/api/chunked-upload/${session.sessionId}/block?blockId=${encodeURIComponent(blockId)}`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/octet-stream' },
        credentials: 'include',
        body: chunk,
        signal,
      },
    );
    if (!res.ok) throw new Error(`Block upload failed at offset ${offset}: HTTP ${res.status}`);

    blockIds.push(blockId);
    offset += chunk.size;
    blockIndex++;
    onProgress(Math.round((offset / file.size) * 100));
  }

  return blockIds;
}

/** Finalizes the upload by committing the block list. */
export async function finalizeChunkedUpload(
  sessionId: string,
  blockIds:  string[],
): Promise<unknown> {
  const res = await fetch(`/api/chunked-upload/${sessionId}/finalize`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ blockIds }),
  });
  if (!res.ok) throw new Error(`Finalize failed: HTTP ${res.status}`);
  return res.json();
}

/** Cancels an in-progress upload. */
export async function cancelChunkedUpload(sessionId: string): Promise<void> {
  await fetch(`/api/chunked-upload/${sessionId}`, {
    method: 'DELETE',
    credentials: 'include',
  });
}
