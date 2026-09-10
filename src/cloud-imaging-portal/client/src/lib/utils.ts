import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

/**
 * Merge conditional class names and resolve Tailwind conflicts.
 * Standard shadcn/ui utility used by every UI primitive.
 */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}

/**
 * Formats an ISO timestamp as a locale-aware date + time (e.g. "Aug 25, 2026, 2:19 PM").
 * Use this wherever a table/detail view shows *when* something happened (uploaded, created,
 * published, etc.) so those timestamps are presented consistently across the portal.
 */
export function formatDateTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  });
}

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/**
 * Formats an ISO timestamp as an age relative to now ("4 min ago", "3 days ago").
 *
 * Operators scanning a device or image list are asking "is this recent?", and answering that
 * from an absolute timestamp requires reading the date, recalling today's date, and subtracting.
 * Past a week the relative form stops being informative ("63 days ago"), so it falls back to the
 * absolute date. Always pair this with the absolute value in a `title`/tooltip — see
 * `RelativeTime` — because the exact instant still matters when correlating with device logs.
 *
 * Timestamps slightly in the future are reported as "just now": the client clock and the service
 * clock are not guaranteed to agree, and "in 3 seconds" reads as a bug.
 */
export function formatRelativeTime(iso: string, now: number = Date.now()): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '—';

  const elapsed = now - then;
  if (elapsed < MINUTE) return 'just now';
  if (elapsed < HOUR) {
    const minutes = Math.floor(elapsed / MINUTE);
    return `${minutes} min ago`;
  }
  if (elapsed < DAY) {
    const hours = Math.floor(elapsed / HOUR);
    return `${hours} hr ago`;
  }
  if (elapsed < 7 * DAY) {
    const days = Math.floor(elapsed / DAY);
    return days === 1 ? 'yesterday' : `${days} days ago`;
  }
  return new Date(iso).toLocaleDateString(undefined, { dateStyle: 'medium' });
}
