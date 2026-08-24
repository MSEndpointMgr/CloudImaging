import path from 'node:path';

/**
 * Shared allow-list for boot/recovery/OS image uploads (portal server layer).
 *
 * This is a fast-fail, defense-in-depth check performed before proxying the request to the
 * Operator API / Imaging Core API. The authoritative check remains the file-signature (magic
 * bytes) validation performed server-side in Imaging Core API during the publish step, which
 * inspects actual file content rather than trusting a client-supplied extension.
 */
export const ALLOWED_IMAGE_EXTENSIONS = ['.iso', '.wim'] as const;

/** Returns the lowercase file extension (including the dot), or '' if none. */
export function getFileExtension(fileName: string): string {
  return path.extname(fileName).toLowerCase();
}

/** Returns whether the given file name has an allowed image extension. */
export function isAllowedImageFile(fileName: string): boolean {
  return (ALLOWED_IMAGE_EXTENSIONS as readonly string[]).includes(getFileExtension(fileName));
}
