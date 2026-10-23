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

/** Local time as `2026-10-22 02:00`, for table columns that must read the same in every row. */
export function formatTimestamp(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${String(date.getFullYear())}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
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
 * absolute date. Always pair this with the absolute value in a `title`/tooltip: see
 * `RelativeTime`: because the exact instant still matters when correlating with device logs.
 *
 * Timestamps slightly in the future are reported as "just now": the client clock and the service
 * clock are not guaranteed to agree, and "in 3 seconds" reads as a bug.
 */
export function formatRelativeTime(iso: string, now: number = Date.now()): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '\u2014';

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

/**
 * Formats an elapsed span as "52s", "11m 38s" or "1h 04m".
 *
 * Operators watching a deployment are asking "how long has this been going", and answering that
 * from a start and finish timestamp means doing the subtraction by hand. Seconds are padded so a
 * column of these stays aligned as the value ticks.
 */
export function formatDuration(milliseconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(milliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, '0')}m`;
  if (minutes > 0) return `${minutes}m ${String(seconds).padStart(2, '0')}s`;
  return `${seconds}s`;
}
