export interface LocationHistoryRecord {
  sessionId: string;
  finalState: string;
  deviceSerialNumber: string;
  deviceManufacturer: string;
  deviceModel: string;
  locationId?: string | null;
  locationName?: string | null;
  createdAt: string;
  terminalAt: string;
}

export interface OutcomeCounts {
  total: number;
  completed: number;
  failed: number;
  expired: number;
  notAuthorized: number;
}

export function recordsForLocation(
  records: LocationHistoryRecord[],
  locationId: string,
): LocationHistoryRecord[] {
  return locationId ? records.filter(record => record.locationId === locationId) : records;
}

export function countOutcomes(records: LocationHistoryRecord[]): OutcomeCounts {
  const counts: OutcomeCounts = { total: records.length, completed: 0, failed: 0, expired: 0, notAuthorized: 0 };
  for (const record of records) {
    if (record.finalState === 'SessionCompleted') counts.completed++;
    else if (record.finalState === 'SessionFailed') counts.failed++;
    else if (record.finalState === 'SessionExpired') counts.expired++;
    else if (record.finalState === 'SessionNotAuthorized') counts.notAuthorized++;
  }
  return counts;
}

export function durationMilliseconds(record: LocationHistoryRecord): number | null {
  const duration = new Date(record.terminalAt).getTime() - new Date(record.createdAt).getTime();
  return Number.isFinite(duration) && duration >= 0 ? duration : null;
}

export function formatDuration(milliseconds: number | null): string {
  if (milliseconds === null) return '-';
  const totalSeconds = Math.round(milliseconds / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  if (minutes < 60) return `${minutes}m ${totalSeconds % 60}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}