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
