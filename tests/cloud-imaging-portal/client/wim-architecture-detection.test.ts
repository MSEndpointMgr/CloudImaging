import { describe, it, expect } from 'vitest';
import {
  classifyProcessorArchitecture,
  detectWimArchitecture,
  parseProcessorArchitecture,
  xmlResourceRange,
  WIM_HEADER_SIZE,
} from '../../../src/cloud-imaging-portal/client/src/lib/wimMetadata.ts';

/** Builds a minimal WIM (header + UTF-16LE XML metadata), mirroring the .NET SyntheticWim helper. */
function buildWim(arch: number | null, options: { compressed?: boolean } = {}): Uint8Array {
  const archXml = arch === null ? '' : `<ARCH>${String(arch)}</ARCH>`;
  const xml = `<WIM><IMAGE INDEX="1"><WINDOWS>${archXml}</WINDOWS><NAME>Microsoft Windows PE</NAME></IMAGE></WIM>`;
  const xmlBytes = new Uint8Array(2 + xml.length * 2);
  xmlBytes[0] = 0xff;
  xmlBytes[1] = 0xfe;
  for (let i = 0; i < xml.length; i++) {
    xmlBytes[2 + i * 2] = xml.charCodeAt(i) & 0xff;
    xmlBytes[3 + i * 2] = xml.charCodeAt(i) >> 8;
  }

  const bytes = new Uint8Array(WIM_HEADER_SIZE + xmlBytes.length);
  bytes.set([0x4d, 0x53, 0x57, 0x49, 0x4d, 0, 0, 0]);
  const view = new DataView(bytes.buffer);
  const flags = options.compressed ? 0x04n : 0x02n;
  view.setBigUint64(72, (flags << 56n) | BigInt(xmlBytes.length), true);
  view.setBigInt64(80, BigInt(WIM_HEADER_SIZE), true);
  bytes.set(xmlBytes, WIM_HEADER_SIZE);
  return bytes;
}

describe('WIM architecture detection', () => {
  it.each([
    [9, 'x64'],
    [12, 'arm64'],
  ] as const)('reads ARCH %i as %s from the XML metadata', async (raw, expected) => {
    const detection = await detectWimArchitecture(new Blob([buildWim(raw)]));
    expect(detection).toEqual({ kind: 'supported', architecture: expected });
  });

  it('flags x86 images as unsupported instead of silently cataloging them', async () => {
    const detection = await detectWimArchitecture(new Blob([buildWim(0)]));
    expect(detection).toEqual({ kind: 'unsupported', description: 'x86' });
  });

  it('is undetermined when the image records no architecture', async () => {
    expect(await detectWimArchitecture(new Blob([buildWim(null)]))).toEqual({ kind: 'undetermined' });
  });

  it('is undetermined for content that is not a WIM', async () => {
    expect(await detectWimArchitecture(new Blob([new Uint8Array(512)]))).toEqual({ kind: 'undetermined' });
  });

  it('refuses a compressed XML resource', () => {
    expect(xmlResourceRange(buildWim(12, { compressed: true }).slice(0, WIM_HEADER_SIZE))).toBeNull();
  });

  it('only reads image 1', () => {
    const xml = new TextEncoder().encode('<WIM><IMAGE INDEX="2"><WINDOWS><ARCH>12</ARCH></WINDOWS></IMAGE><IMAGE INDEX="1"><WINDOWS><ARCH>9</ARCH></WINDOWS></IMAGE></WIM>');
    expect(parseProcessorArchitecture(xml)).toBe(9);
    expect(classifyProcessorArchitecture(9)).toEqual({ kind: 'supported', architecture: 'x64' });
  });
});
