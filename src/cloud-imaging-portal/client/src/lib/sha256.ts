/**
 * Streaming SHA-256 for large files.
 *
 * OS/boot/recovery image files can be several GB (chunked OS image uploads are documented as
 * 5-10 GB). Reading the whole file into one `ArrayBuffer` and calling the native
 * `crypto.subtle.digest()` once would require holding the entire file in memory at once, which
 * risks exhausting the tab's memory on large uploads. `hash-wasm`'s incremental hasher lets us
 * feed the file a chunk at a time (matching the chunked-upload block size) and only ever holds
 * one chunk in memory.
 */
import { createSHA256 } from 'hash-wasm';

const CHUNK_SIZE = 4 * 1024 * 1024; // 4 MB — matches chunkedUploadService's block size

/** Reads a Blob/File slice as an ArrayBuffer via FileReader (broader support than Blob.arrayBuffer(), notably in jsdom test environments). */
function readAsArrayBuffer(blob: Blob): Promise<ArrayBuffer> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as ArrayBuffer);
    reader.onerror = () => reject(reader.error ?? new Error('Failed to read file chunk.'));
    reader.readAsArrayBuffer(blob);
  });
}

/**
 * Computes the SHA-256 hash of `file` without loading it into memory all at once.
 * Optionally reports progress (0-100) as the file is read.
 */
export async function computeSha256Streaming(
  file: File,
  onProgress?: (percent: number) => void,
): Promise<string> {
  const hasher = await createSHA256();
  hasher.init();

  let offset = 0;
  while (offset < file.size) {
    const chunk = file.slice(offset, offset + CHUNK_SIZE);
    const buffer = await readAsArrayBuffer(chunk);
    hasher.update(new Uint8Array(buffer));
    offset += buffer.byteLength;
    onProgress?.(file.size === 0 ? 100 : Math.round((offset / file.size) * 100));
  }

  return hasher.digest('hex');
}
