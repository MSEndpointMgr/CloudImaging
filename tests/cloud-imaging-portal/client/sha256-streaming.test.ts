import { describe, it, expect } from 'vitest';
import { computeSha256Streaming } from '../../../src/cloud-imaging-portal/client/src/lib/sha256.ts';

async function digestHex(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', bytes.buffer as ArrayBuffer);
  return Array.from(new Uint8Array(digest))
    .map(b => b.toString(16).padStart(2, '0'))
    .join('');
}

/**
 * Verifies the streaming SHA-256 helper used by the OS/boot/recovery image upload dialogs
 * (replaces manual hash entry — see ChunkedUploadDialog.tsx, BootImagesPage.tsx,
 * RecoveryImagesPage.tsx) against the browser's native one-shot digest, including content that
 * spans multiple internal chunk boundaries (the helper reads in 4 MB chunks). Expected digests
 * are always derived independently via `crypto.subtle.digest` rather than hardcoded, so the test
 * can't drift from a mistyped literal.
 */
describe('computeSha256Streaming', () => {
  it('matches crypto.subtle.digest for an empty file', async () => {
    const bytes = new Uint8Array(0);
    const file = new File([bytes], 'empty.bin');

    const [streamed, expected] = await Promise.all([computeSha256Streaming(file), digestHex(bytes)]);
    expect(streamed).toHaveLength(64);
    expect(streamed).toBe(expected);
  });

  it('matches crypto.subtle.digest for content smaller than one chunk', async () => {
    const bytes = new TextEncoder().encode('abc');
    const file = new File([bytes], 'abc.txt');

    const [streamed, expected] = await Promise.all([computeSha256Streaming(file), digestHex(bytes)]);
    expect(streamed).toBe(expected);
  });

  it('matches crypto.subtle.digest for content spanning multiple chunk boundaries', async () => {
    // 10 MB of pseudo-random-ish bytes — larger than the helper's 4 MB internal chunk size,
    // so this exercises the multi-chunk incremental-update path.
    const size = 10 * 1024 * 1024 + 137; // not an exact multiple of the chunk size either
    const bytes = new Uint8Array(size);
    for (let i = 0; i < size; i++) bytes[i] = (i * 2654435761) & 0xff;

    const file = new File([bytes], 'large.bin');
    const [streamed, expected] = await Promise.all([computeSha256Streaming(file), digestHex(bytes)]);
    expect(streamed).toBe(expected);
  });

  it('reports monotonically increasing progress from 0 to 100', async () => {
    const bytes = new Uint8Array(5 * 1024 * 1024);
    const file = new File([bytes], 'progress.bin');

    const reported: number[] = [];
    await computeSha256Streaming(file, percent => reported.push(percent));

    expect(reported.length).toBeGreaterThan(0);
    expect(reported[reported.length - 1]).toBe(100);
    for (let i = 1; i < reported.length; i++) {
      expect(reported[i]).toBeGreaterThanOrEqual(reported[i - 1]);
    }
  });
});

