import { apiFetch } from './apiClient.ts';

/**
 * Update check client and banner dismissal state.
 *
 * The comparison itself is done server-side, so this only transports the result. Every failure
 * resolves to a status the interface renders as "unavailable"; a version check must never be
 * able to degrade the portal.
 */

/** Why a check produced no `latest` value. Mirrors the backend's UpdateCheckStatus. */
export type UpdateCheckStatus = 'ok' | 'disabled' | 'unreachable' | 'rate-limited' | 'unknown';

export interface UpdateStatus {
  current: string;
  latest: string | null;
  updateAvailable: boolean;
  releaseUrl: string | null;
  checkedAt: string | null;
  status: UpdateCheckStatus;
}

/** Fetches the update status. Returns null on any failure, which renders as "unavailable". */
export async function fetchUpdateStatus(): Promise<UpdateStatus | null> {
  try {
    const res = await apiFetch('/api/update-check', { credentials: 'include' });
    if (!res.ok) return null;
    return await res.json() as UpdateStatus;
  } catch {
    return null;
  }
}

/** Strips the release tag prefix for display, leaving a bare version number. */
export function displayVersion(value: string | null | undefined): string {
  if (!value) return 'Unknown';
  return value.replace(/^mse-ci-v/, '');
}

// ── Banner dismissal ────────────────────────────────────────────────────────
// Recorded per signed-in account and per version, so dismissing the prompt for 1.3.0 does not
// also suppress 1.4.0 when it lands. localStorage so it survives reloads and applies across
// tabs, matching how certificate expiry warnings are tracked.

const STORAGE_KEY = 'ci-update-banner-dismissed';

type DismissedRecord = Record<string, string>;

function read(): DismissedRecord {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as DismissedRecord) : {};
  } catch {
    return {};
  }
}

/** True when this account has already dismissed the banner for this specific version. */
export function hasDismissedUpdate(accountKey: string, version: string): boolean {
  return read()[accountKey] === version;
}

/** Records that this account has dismissed the banner for this version. */
export function dismissUpdate(accountKey: string, version: string): void {
  try {
    const record = read();
    record[accountKey] = version;
    localStorage.setItem(STORAGE_KEY, JSON.stringify(record));
  } catch {
    // Storage unavailable (private mode quota). The banner reappearing on reload is a far
    // better outcome than failing the render.
  }
}
