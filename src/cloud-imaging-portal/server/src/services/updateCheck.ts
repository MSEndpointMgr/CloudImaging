import { DEPLOYED_VERSION } from '../version.js';
import { isUpdateAvailable } from './version.js';

/**
 * Checks GitHub for a newer Cloud Imaging backend/infrastructure release.
 *
 * Deliberately reads the `mse-ci-iac-latest` alias rather than GitHub's repository-wide
 * `releases/latest`. The repository publishes three independently versioned streams into one
 * flat release list, so `releases/latest` resolves to whichever stream published most recently
 * and could advertise a Cloud Imaging Client release as a portal upgrade. The alias is the same
 * one `update.ps1` resolves, so the portal can never offer a version the upgrade script would
 * not install.
 *
 * The call is made here, server-side, rather than from the browser: one cached result serves
 * every operator (keeping the deployment far below GitHub's 60-requests-per-hour unauthenticated
 * limit instead of spending one request per user per page load), no operator's IP address is
 * exposed to github.com, and a restricted network fails once here instead of leaving every
 * browser to hang.
 */

const ALIAS_TAG = 'mse-ci-iac-latest';
const RELEASE_URL = `https://api.github.com/repos/MSEndpointMgr/CloudImaging/releases/tags/${ALIAS_TAG}`;
const CACHE_TTL_MS = 6 * 60 * 60 * 1000;
const REQUEST_TIMEOUT_MS = 5000;

/** Why a check produced no `latest` value. Every non-`ok` value renders as "unavailable", never as an error. */
export type UpdateCheckStatus = 'ok' | 'disabled' | 'unreachable' | 'rate-limited' | 'unknown';

export interface UpdateCheckResult {
  current: string;
  latest: string | null;
  updateAvailable: boolean;
  releaseUrl: string | null;
  checkedAt: string | null;
  status: UpdateCheckStatus;
}

interface CachedLookup {
  latest: string | null;
  releaseUrl: string | null;
  status: UpdateCheckStatus;
  checkedAt: string;
  expiresAt: number;
}

let cache: CachedLookup | null = null;

/** Test seam: drops the cached GitHub lookup. */
export function resetUpdateCheckCache(): void {
  cache = null;
}

/**
 * The alias release's own tag is literally `mse-ci-iac-latest`; the real version is embedded in
 * its title, e.g. "Cloud Imaging (latest — mse-ci-v1.2.3)". Mirrors how `update.ps1` resolves it.
 */
function resolveVersionFromRelease(release: { name?: unknown; tag_name?: unknown }): string | null {
  const name = typeof release.name === 'string' ? release.name : '';
  const embedded = /mse-ci-v\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?/.exec(name);
  if (embedded) return embedded[0];
  return typeof release.tag_name === 'string' && release.tag_name !== ALIAS_TAG ? release.tag_name : null;
}

async function lookupLatest(): Promise<CachedLookup> {
  const checkedAt = new Date().toISOString();
  const expiresAt = Date.now() + CACHE_TTL_MS;

  try {
    const res = await fetch(RELEASE_URL, {
      headers: { Accept: 'application/vnd.github+json', 'User-Agent': 'CloudImaging-Portal' },
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });

    // GitHub reports an exhausted unauthenticated quota as 403 (or 429).
    if (res.status === 403 || res.status === 429) {
      return { latest: null, releaseUrl: null, status: 'rate-limited', checkedAt, expiresAt };
    }
    // 404 means no stable backend release has ever been published, so there is nothing to compare to.
    if (!res.ok) {
      return { latest: null, releaseUrl: null, status: 'unknown', checkedAt, expiresAt };
    }

    const body = await res.json() as { name?: unknown; tag_name?: unknown; html_url?: unknown };
    const latest = resolveVersionFromRelease(body);
    return {
      latest,
      releaseUrl: typeof body.html_url === 'string' ? body.html_url : null,
      status: latest ? 'ok' : 'unknown',
      checkedAt,
      expiresAt,
    };
  } catch {
    // No outbound access, DNS failure, or the timeout above. Not an error condition for the portal.
    return { latest: null, releaseUrl: null, status: 'unreachable', checkedAt, expiresAt };
  }
}

/**
 * Resolves the current and latest versions.
 *
 * When `enabled` is false this returns immediately and issues no outbound request at all. That
 * enforcement lives here rather than in the interface, because the point of the setting is to
 * guarantee no traffic leaves the tenant.
 */
export async function getUpdateStatus(enabled: boolean): Promise<UpdateCheckResult> {
  const current = DEPLOYED_VERSION;

  if (!enabled) {
    return { current, latest: null, updateAvailable: false, releaseUrl: null, checkedAt: null, status: 'disabled' };
  }

  if (!cache || cache.expiresAt <= Date.now()) {
    const fresh = await lookupLatest();
    // Keep serving a previously good answer through a rate-limit window rather than
    // flapping the banner off and back on again.
    cache = fresh.status === 'rate-limited' && cache?.status === 'ok'
      ? { ...cache, checkedAt: fresh.checkedAt, expiresAt: fresh.expiresAt }
      : fresh;
  }

  return {
    current,
    latest: cache.latest,
    updateAvailable: isUpdateAvailable(current, cache.latest),
    releaseUrl: cache.releaseUrl,
    checkedAt: cache.checkedAt,
    status: cache.status,
  };
}
