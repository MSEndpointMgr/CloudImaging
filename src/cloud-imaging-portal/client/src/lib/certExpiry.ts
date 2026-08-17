/**
 * Boot media certificate expiry warning logic.
 *
 * Surfaces an escalating notification as the active boot media certificate approaches
 * expiry: a one-time notice at 30 days remaining, another at 14 days, then a recurring
 * (once per calendar day) warning for the final 7 days and after expiry. Administrators
 * only see each bucket once. "Seen" state is tracked per signed-in account + certificate
 * thumbprint in localStorage so it persists across tabs/sessions until the bucket changes
 * (e.g. a new day, or the certificate is rotated).
 */

const DAY_MS = 24 * 60 * 60 * 1000;

export interface CertExpiryWarning {
  /** Identifies the specific warning instance; a repeat with the same bucket is suppressed. */
  bucket: string;
  daysRemaining: number;
  /** `true` for the final 7 days / after expiry. Rendered as an error-level toast. */
  urgent: boolean;
  title: string;
  description: string;
}

/**
 * Computes the expiry warning (if any) for a certificate expiring at `expiresAt`.
 * Returns `null` when the certificate isn't within 30 days of expiring.
 */
export function getCertExpiryWarning(expiresAt: string, now: Date = new Date()): CertExpiryWarning | null {
  const expiry = new Date(expiresAt);
  if (Number.isNaN(expiry.getTime())) return null;

  const daysRemaining = Math.ceil((expiry.getTime() - now.getTime()) / DAY_MS);
  if (daysRemaining > 30) return null;

  const dayLabel = (n: number) => `${n} day${Math.abs(n) === 1 ? '' : 's'}`;

  if (daysRemaining <= 7) {
    const todayKey = now.toISOString().slice(0, 10);
    const expired = daysRemaining <= 0;
    return {
      bucket: `daily-${todayKey}`,
      daysRemaining,
      urgent: true,
      title: expired
        ? 'Boot media certificate has expired'
        : `Boot media certificate expires in ${dayLabel(daysRemaining)}`,
      description: expired
        ? 'Devices can no longer authenticate to the Device Gateway with this certificate. Rotate it and rebuild/redistribute boot media as soon as possible.'
        : 'Rotate the certificate and rebuild/redistribute boot media before it expires.',
    };
  }

  if (daysRemaining <= 14) {
    return {
      bucket: 'threshold-14',
      daysRemaining,
      urgent: false,
      title: `Boot media certificate expires in ${dayLabel(daysRemaining)}`,
      description: 'Plan a certificate rotation and boot media rebuild soon.',
    };
  }

  return {
    bucket: 'threshold-30',
    daysRemaining,
    urgent: false,
    title: `Boot media certificate expires in ${dayLabel(daysRemaining)}`,
    description: 'The certificate will need to be rotated within the next month.',
  };
}

const STORAGE_KEY = 'ci-cert-expiry-warning-shown';

type ShownRecord = Record<string, string>;

function readShown(): ShownRecord {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as ShownRecord) : {};
  } catch {
    return {};
  }
}

function shownKey(accountKey: string, thumbprint: string): string {
  return `${accountKey}:${thumbprint}`;
}

/** True if this exact warning bucket has already been shown to this account for this certificate. */
export function hasCertExpiryWarningBeenShown(accountKey: string, thumbprint: string, bucket: string): boolean {
  return readShown()[shownKey(accountKey, thumbprint)] === bucket;
}

/** Records that this warning bucket has now been shown to this account for this certificate. */
export function markCertExpiryWarningShown(accountKey: string, thumbprint: string, bucket: string): void {
  try {
    const record = readShown();
    record[shownKey(accountKey, thumbprint)] = bucket;
    localStorage.setItem(STORAGE_KEY, JSON.stringify(record));
  } catch {
    // Best-effort only (e.g. private browsing / storage quota). Worst case the
    // notification reappears on a later load, which is harmless.
  }
}
