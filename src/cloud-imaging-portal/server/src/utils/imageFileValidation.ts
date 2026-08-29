import path from 'node:path';

/**
 * Allow-lists for boot/recovery/OS image uploads (portal server layer).
 *
 * This is a fast-fail, defense-in-depth check performed before proxying the request to the
 * Operator API / Imaging Core API. The authoritative check remains the file-signature (magic
 * bytes) validation performed server-side in Imaging Core API during the publish step, which
 * inspects actual file content rather than trusting a client-supplied extension.
 *
 * OS images may be uploaded as either .wim or .iso — Imaging Core API extracts
 * `sources\install.wim`/`install.esd` from an uploaded ISO automatically at publish time. Boot
 * and recovery images are WIM-only: neither has a coherent standalone ISO form.
 */
export const OS_IMAGE_EXTENSIONS = ['.iso', '.wim'] as const;
export const WIM_ONLY_EXTENSIONS = ['.wim'] as const;

/** Returns the lowercase file extension (including the dot), or '' if none. */
export function getFileExtension(fileName: string): string {
  return path.extname(fileName).toLowerCase();
}

/** Returns whether the given file name has one of the given allowed image extensions. */
export function isAllowedImageFile(fileName: string, allowedExtensions: readonly string[]): boolean {
  return allowedExtensions.includes(getFileExtension(fileName));
}

