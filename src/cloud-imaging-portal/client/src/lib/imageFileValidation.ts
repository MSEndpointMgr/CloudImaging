/**
 * Shared client-side allow-list for boot/recovery/OS image uploads.
 *
 * This is a defense-in-depth / UX convenience layer only — the file's `accept` attribute and
 * this extension check can both be bypassed by a malicious or modified client. The authoritative
 * check is the file-signature (magic bytes) validation performed server-side in Imaging Core API
 * during the publish step, which inspects actual file content rather than trusting the extension.
 */

/** Allowed image file extensions (lowercase, including the leading dot). */
export const ALLOWED_IMAGE_EXTENSIONS = ['.iso', '.wim'] as const;

/** `accept` attribute value for file-picker `<input type="file">` elements. */
export const IMAGE_FILE_ACCEPT = ALLOWED_IMAGE_EXTENSIONS.join(',');

/** Returns the lowercase file extension (including the dot), or '' if none. */
export function getFileExtension(fileName: string): string {
  const idx = fileName.lastIndexOf('.');
  return idx === -1 ? '' : fileName.slice(idx).toLowerCase();
}

/** Returns whether the given file name has an allowed image extension. */
export function isAllowedImageFile(fileName: string): boolean {
  return (ALLOWED_IMAGE_EXTENSIONS as readonly string[]).includes(getFileExtension(fileName));
}

/** Returns a user-facing error message if the file is not allowed, otherwise null. */
export function validateImageFile(file: File): string | null {
  if (!isAllowedImageFile(file.name)) {
    return `Only ${ALLOWED_IMAGE_EXTENSIONS.join(' and ')} files are allowed.`;
  }
  return null;
}
