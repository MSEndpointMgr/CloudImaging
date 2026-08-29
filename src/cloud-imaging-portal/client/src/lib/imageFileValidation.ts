/**
 * Client-side allow-lists for boot/recovery/OS image uploads.
 *
 * This is a defense-in-depth / UX convenience layer only — the file's `accept` attribute and
 * this extension check can both be bypassed by a malicious or modified client. The authoritative
 * check is the file-signature (magic bytes) validation performed server-side in Imaging Core API
 * during the publish step, which inspects actual file content rather than trusting the extension.
 *
 * OS images may be uploaded as either .wim or .iso — a retail/volume Windows ISO ships
 * `sources\install.wim`/`install.esd`, which the Imaging Core API extracts automatically at
 * publish time. Boot and recovery images are WIM-only: a boot image is the WinPE `boot.wim` that
 * Media Builder/self-update replace in place, and a recovery image is a standalone `Winre.wim`
 * pulled from a reference machine — neither has a coherent standalone ISO form.
 */

/** Allowed image file extensions for OS image uploads (lowercase, including the leading dot). */
export const OS_IMAGE_EXTENSIONS = ['.iso', '.wim'] as const;

/** Allowed image file extensions for boot/recovery image uploads (lowercase, including the leading dot). */
export const WIM_ONLY_EXTENSIONS = ['.wim'] as const;

/** `accept` attribute value for file-picker `<input type="file">` elements. */
export function fileAccept(allowedExtensions: readonly string[]): string {
  return allowedExtensions.join(',');
}

/** Returns the lowercase file extension (including the dot), or '' if none. */
export function getFileExtension(fileName: string): string {
  const idx = fileName.lastIndexOf('.');
  return idx === -1 ? '' : fileName.slice(idx).toLowerCase();
}

/** Returns whether the given file name has one of the given allowed image extensions. */
export function isAllowedImageFile(fileName: string, allowedExtensions: readonly string[]): boolean {
  return allowedExtensions.includes(getFileExtension(fileName));
}

/** Returns a user-facing error message if the file is not allowed, otherwise null. */
export function validateImageFile(file: File, allowedExtensions: readonly string[]): string | null {
  if (!isAllowedImageFile(file.name, allowedExtensions)) {
    return `Only ${allowedExtensions.join(' and ')} files are allowed.`;
  }
  return null;
}

