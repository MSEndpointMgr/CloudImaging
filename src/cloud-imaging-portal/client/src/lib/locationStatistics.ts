import type { MultiSelectOption } from '../components/ui/multi-select.tsx';
import { SUMMARY_COLORS, share } from './reportSummary.ts';

/** Dot and bar colour of each final state, the same as its badge in the session report tables. */
export const SESSION_OUTCOME_COLORS: Record<string, string> = {
  SessionCompleted: SUMMARY_COLORS.success,
  SessionFailed: SUMMARY_COLORS.negative,
  SessionExpired: SUMMARY_COLORS.muted,
  SessionNotAuthorized: SUMMARY_COLORS.warning,
};

/** A finished session as `/api/session-history` returns it; the session reports share it. */
export interface SessionHistoryRecord {
  sessionId: string;
  finalState: string;
  deviceSerialNumber: string;
  deviceManufacturer: string;
  deviceModel: string;
  locationId?: string | null;
  locationName?: string | null;
  failedStepName?: string | null;
  errorDetail?: string | null;
  createdAt: string;
  /** When the device started its first imaging step; null when it never did, or before this was recorded. */
  imagingStartedAt?: string | null;
  terminalAt: string;
}

export type LocationHistoryRecord = SessionHistoryRecord;

export interface OutcomeCounts {
  total: number;
  completed: number;
  failed: number;
  expired: number;
  notAuthorized: number;
}

/** Records at any of the chosen locations, or every record while none is chosen. */
export function recordsForLocations<T extends { locationId?: string | null }>(
  records: readonly T[],
  locationIds: readonly string[],
): T[] {
  if (locationIds.length === 0) return [...records];
  const chosen = new Set(locationIds);
  return records.filter(record => !!record.locationId && chosen.has(record.locationId));
}

/**
 * The Locations filter's values: the tenant's locations, plus any the records still name that has
 * since been deleted, so its rows can still be picked. Sorted by name.
 */
export function locationFilterOptions(
  locations: readonly { locationId: string; name: string }[],
  records: readonly { locationId?: string | null; locationName?: string | null }[],
): MultiSelectOption[] {
  const names = new Map(locations.map(location => [location.locationId, location.name]));
  for (const record of records) {
    if (record.locationId && record.locationName && !names.has(record.locationId)) {
      names.set(record.locationId, record.locationName);
    }
  }
  return [...names.entries()]
    .map(([value, label]) => ({ value, label }))
    .sort((a, b) => a.label.localeCompare(b.label));
}

export function countOutcomes(records: readonly SessionHistoryRecord[]): OutcomeCounts {
  const counts: OutcomeCounts = { total: records.length, completed: 0, failed: 0, expired: 0, notAuthorized: 0 };
  for (const record of records) {
    if (record.finalState === 'SessionCompleted') counts.completed++;
    else if (record.finalState === 'SessionFailed') counts.failed++;
    else if (record.finalState === 'SessionExpired') counts.expired++;
    else if (record.finalState === 'SessionNotAuthorized') counts.notAuthorized++;
  }
  return counts;
}

/** Completed share of the sessions, in whole percent, or null without sessions. */
export function successRate(counts: OutcomeCounts): number | null {
  return share(counts.completed, counts.total);
}

/**
 * How long imaging took, as on the Devices page: from the first step to the end. Null for a session
 * that never started imaging, whose time from registration to expiry says nothing about imaging.
 */
export function durationMilliseconds(record: SessionHistoryRecord): number | null {
  if (!record.imagingStartedAt) return null;
  const duration = new Date(record.terminalAt).getTime() - new Date(record.imagingStartedAt).getTime();
  return Number.isFinite(duration) && duration >= 0 ? duration : null;
}

/** When imaging started, for sorting; 0 when it never did. */
export function imagingStartTime(record: SessionHistoryRecord): number {
  return record.imagingStartedAt ? new Date(record.imagingStartedAt).getTime() : 0;
}

/** Average and longest of the durations, both null when there are none. */
export function durationStats(durations: readonly (number | null)[]): { average: number | null; longest: number | null } {
  const known = durations.filter((value): value is number => value !== null);
  if (known.length === 0) return { average: null, longest: null };
  return { average: known.reduce((sum, value) => sum + value, 0) / known.length, longest: Math.max(...known) };
}

export function formatDuration(milliseconds: number | null): string {
  if (milliseconds === null) return '-';
  const totalSeconds = Math.round(milliseconds / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  if (minutes < 60) return `${minutes}m ${totalSeconds % 60}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}