/**
 * Release version parsing and comparison for the update check.
 *
 * Versions are the repository's backend/infrastructure release tags, `mse-ci-v<major>.<minor>.<patch>`,
 * optionally with a pre-release suffix (`-rc.1`). Anything that doesn't match is treated as
 * unknown rather than guessed at, so a local build (`dev`) simply disables the comparison.
 */

export interface ParsedVersion {
  major: number;
  minor: number;
  patch: number;
  /** Present only for pre-release tags such as `-rc.1`. */
  preRelease?: string;
}

const TAG_PATTERN = /^(?:mse-ci-v)?(\d+)\.(\d+)\.(\d+)(?:-([A-Za-z0-9.]+))?$/;

/** Parses a release tag. Returns null for anything unrecognised, including `dev` and `latest`. */
export function parseVersion(value: string | null | undefined): ParsedVersion | null {
  if (!value) return null;
  const match = TAG_PATTERN.exec(value.trim());
  if (!match) return null;
  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3]),
    ...(match[4] ? { preRelease: match[4] } : {}),
  };
}

/**
 * Returns a negative number when `a` precedes `b`, positive when it follows, 0 when equal.
 *
 * Compares numerically, segment by segment. A plain string comparison would order `v1.10.0`
 * before `v1.9.0`, which is the whole reason this exists. A pre-release sorts before the
 * release of the same number, matching semantic versioning.
 */
export function compareVersions(a: ParsedVersion, b: ParsedVersion): number {
  if (a.major !== b.major) return a.major - b.major;
  if (a.minor !== b.minor) return a.minor - b.minor;
  if (a.patch !== b.patch) return a.patch - b.patch;
  if (a.preRelease === b.preRelease) return 0;
  if (a.preRelease && !b.preRelease) return -1;
  if (!a.preRelease && b.preRelease) return 1;
  return (a.preRelease ?? '').localeCompare(b.preRelease ?? '');
}

/**
 * True when `latest` is a newer release than `current`.
 *
 * Returns false whenever either side is unparseable, so an unknown current version never
 * produces a spurious "upgrade available" prompt. A pre-release `latest` is also rejected:
 * the alias this check reads only ever moves on stable releases, so a pre-release value
 * means something unexpected happened and is not worth prompting an upgrade over.
 */
export function isUpdateAvailable(current: string | null | undefined, latest: string | null | undefined): boolean {
  const parsedCurrent = parseVersion(current);
  const parsedLatest = parseVersion(latest);
  if (!parsedCurrent || !parsedLatest) return false;
  if (parsedLatest.preRelease) return false;
  return compareVersions(parsedLatest, parsedCurrent) > 0;
}
