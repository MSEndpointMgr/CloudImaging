/**
 * Shared version-suggestion/validation helpers for the Boot Images and Recovery Images
 * catalog pages (both are "twin" WIM/ISO media catalogs with an identical upload workflow).
 */

/**
 * Derives a `YYYY.MM.DD.V` version string from an `YYYYMMDD` date embedded in a boot/recovery
 * media filename (Media Builder names uploads e.g. `cloud-imaging-boot-20260827-170846.wim`).
 * `V` starts at 1 and is bumped past any version already in `existingVersions` for the same
 * date, so publishing a second image on the same day suggests `.2`, `.3`, etc.
 * Returns `null` when no plausible date can be found in the filename.
 */
export function suggestVersionFromFileName(fileName: string, existingVersions: string[]): string | null {
  const match = /(\d{4})(\d{2})(\d{2})/.exec(fileName);
  if (!match) return null;

  const [, year, month, day] = match;
  const monthNum = Number(month);
  const dayNum = Number(day);
  if (monthNum < 1 || monthNum > 12 || dayNum < 1 || dayNum > 31) return null;

  const datePrefix = `${year}.${month}.${day}`;
  const versionPattern = new RegExp(`^${datePrefix.replace(/\./g, '\\.')}\\.(\\d+)$`);
  const usedVersionNumbers = existingVersions
    .map(v => versionPattern.exec(v.trim())?.[1])
    .filter((v): v is string => v !== undefined)
    .map(Number);
  const nextVersion = usedVersionNumbers.length > 0 ? Math.max(...usedVersionNumbers) + 1 : 1;
  return `${datePrefix}.${nextVersion}`;
}

/** Case-insensitive check for whether `version` already exists in `existingVersions`. */
export function isDuplicateVersion(version: string, existingVersions: string[]): boolean {
  const trimmed = version.trim();
  return trimmed.length > 0 && existingVersions.some(v => v.toLowerCase() === trimmed.toLowerCase());
}
