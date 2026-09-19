import { describe, expect, it } from 'vitest';
import {
  countOutcomes,
  durationMilliseconds,
  formatDuration,
  recordsForLocation,
  type LocationHistoryRecord,
} from '../../../src/cloud-imaging-portal/client/src/lib/locationStatistics.ts';

const base: LocationHistoryRecord = {
  sessionId: '11111111-1111-1111-1111-111111111111',
  finalState: 'SessionCompleted',
  deviceSerialNumber: 'SN-1',
  deviceManufacturer: 'Contoso',
  deviceModel: 'Model X',
  locationId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  locationName: 'Copenhagen HQ',
  createdAt: '2026-09-11T08:00:00.000Z',
  terminalAt: '2026-09-11T08:10:00.000Z',
};

describe('Location Statistics report calculations', () => {
  it('isolates records to the selected location', () => {
    const other = { ...base, sessionId: '22222222-2222-2222-2222-222222222222', locationId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' };

    expect(recordsForLocation([base, other], base.locationId!)).toEqual([base]);
    expect(recordsForLocation([base, other], '')).toHaveLength(2);
  });

  it('counts every terminal outcome independently', () => {
    const records = [
      base,
      { ...base, sessionId: '2', finalState: 'SessionCompleted' },
      { ...base, sessionId: '3', finalState: 'SessionFailed' },
      { ...base, sessionId: '4', finalState: 'SessionExpired' },
      { ...base, sessionId: '5', finalState: 'SessionNotAuthorized' },
    ];

    expect(countOutcomes(records)).toEqual({ total: 5, completed: 2, failed: 1, expired: 1, notAuthorized: 1 });
  });

  it('calculates and formats deployment duration', () => {
    expect(durationMilliseconds(base)).toBe(600_000);
    expect(formatDuration(durationMilliseconds(base))).toBe('10m 0s');
  });

  it('rejects invalid or negative durations', () => {
    expect(durationMilliseconds({ ...base, terminalAt: 'invalid' })).toBeNull();
    expect(durationMilliseconds({ ...base, terminalAt: '2026-09-11T07:00:00.000Z' })).toBeNull();
  });
});