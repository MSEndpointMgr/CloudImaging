/**
 * Reads a WIM's processor architecture from its XML metadata resource, in the browser, before
 * upload. TypeScript twin of CloudImaging.Contracts WimMetadataReader.cs; keep both in sync.
 * Only the 208-byte header and the (small) XML resource are read, never the image payload.
 */

/** Target processor architecture of a boot, OS or recovery image (todo/arm64-support.md). */
export type ImageArchitecture = 'x64' | 'arm64';

export const IMAGE_ARCHITECTURES: ImageArchitecture[] = ['x64', 'arm64'];

/** Operator-facing label, matching the Media Builder and Client wording. */
export function architectureLabel(architecture: ImageArchitecture | undefined): string {
  return architecture === 'arm64' ? 'ARM64' : 'x64';
}

/** Counts catalog rows per architecture; rows without one predate architecture tracking and are x64. */
export function countByArchitecture(rows: { architecture?: ImageArchitecture }[]): Record<ImageArchitecture, number> {
  return {
    x64: rows.filter(row => row.architecture !== 'arm64').length,
    arm64: rows.filter(row => row.architecture === 'arm64').length,
  };
}

export const WIM_HEADER_SIZE = 208;
const XML_RESOURCE_OFFSET = 72;
const COMPRESSED_RESOURCE_FLAG = 0x04;
const MAX_XML_BYTES = 16 * 1024 * 1024;
const WIM_MAGIC = [0x4d, 0x53, 0x57, 0x49, 0x4d, 0x00, 0x00, 0x00];

/** Outcome of inspecting a WIM: `undetermined` when the file carries no readable architecture. */
export type WimArchitectureDetection =
  | { kind: 'supported'; architecture: ImageArchitecture }
  | { kind: 'unsupported'; description: string }
  | { kind: 'undetermined' };

/** Byte range of the XML metadata resource, or null when the header is not a readable WIM. */
export function xmlResourceRange(header: Uint8Array): { offset: number; length: number } | null {
  if (header.length < WIM_HEADER_SIZE || WIM_MAGIC.some((b, i) => header[i] !== b)) return null;

  const view = new DataView(header.buffer, header.byteOffset, header.byteLength);
  const sizeAndFlags = view.getBigUint64(XML_RESOURCE_OFFSET, true);
  const flags = Number(sizeAndFlags >> 56n);
  const length = Number(sizeAndFlags & 0x00ff_ffff_ffff_ffffn);
  const offset = Number(view.getBigInt64(XML_RESOURCE_OFFSET + 8, true));

  if ((flags & COMPRESSED_RESOURCE_FLAG) !== 0 || length <= 0 || length > MAX_XML_BYTES || offset < WIM_HEADER_SIZE) {
    return null;
  }
  return { offset, length };
}

/** Raw PROCESSOR_ARCHITECTURE of image 1 (`IMAGE/WINDOWS/ARCH`), or null when absent. */
export function parseProcessorArchitecture(xml: Uint8Array): number | null {
  const hasBom = xml.length >= 2 && xml[0] === 0xff && xml[1] === 0xfe;
  const text = new TextDecoder(hasBom || (xml.length >= 2 && xml[1] === 0) ? 'utf-16le' : 'utf-8').decode(xml);
  const image = /<IMAGE\s+INDEX="1"\s*>([\s\S]*?)<\/IMAGE>/.exec(text)?.[1];
  const arch = image && /<WINDOWS>[\s\S]*?<ARCH>(\d+)<\/ARCH>/.exec(image)?.[1];
  return arch ? Number(arch) : null;
}

/** Maps a raw PROCESSOR_ARCHITECTURE value to a detection result. */
export function classifyProcessorArchitecture(raw: number | null): WimArchitectureDetection {
  switch (raw) {
    case null: return { kind: 'undetermined' };
    case 9: return { kind: 'supported', architecture: 'x64' };
    case 12: return { kind: 'supported', architecture: 'arm64' };
    case 0: return { kind: 'unsupported', description: 'x86' };
    case 5: return { kind: 'unsupported', description: 'ARM (32-bit)' };
    default: return { kind: 'unsupported', description: `an unknown architecture (${String(raw)})` };
  }
}

/** Detects the target architecture of a selected WIM file. Never throws. */
export async function detectWimArchitecture(file: Blob): Promise<WimArchitectureDetection> {
  try {
    const header = new Uint8Array(await file.slice(0, WIM_HEADER_SIZE).arrayBuffer());
    const range = xmlResourceRange(header);
    if (!range || range.offset + range.length > file.size) return { kind: 'undetermined' };

    const xml = new Uint8Array(await file.slice(range.offset, range.offset + range.length).arrayBuffer());
    return classifyProcessorArchitecture(parseProcessorArchitecture(xml));
  } catch {
    return { kind: 'undetermined' };
  }
}
