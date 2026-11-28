import { describe, expect, it } from 'vitest';
import {
  countOutcomes,
  durationMilliseconds,
  durationStats,
  formatDuration,
  imagingStartTime,
  locationFilterOptions,
  recordsForLocations,
  successRate,
  type LocationHistoryRecord,
} from '../../../src/cloud-imaging-portal/client/src/lib/locationStatistics.ts';
import { previousRange, previousRangeLabel } from '../../../src/cloud-imaging-portal/client/src/lib/dateRange.ts';

const base: LocationHistoryRecord = {
  sessionId: '11111111-1111-1111-1111-111111111111',
  finalState: 'SessionCompleted',
  deviceSerialNumber: 'SN-1',
  deviceManufacturer: 'Contoso',
  deviceModel: 'Model X',
  locationId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  locationName: 'Copenhagen HQ',
  createdAt: '2026-09-11T07:30:00.000Z',
  imagingStartedAt: '2026-09-11T08:00:00.000Z',
  terminalAt: '2026-09-11T08:10:00.000Z',
};

describe('Location Statistics report calculations', () => {
  it('keeps the records of any chosen location, and every record while none is chosen', () => {
    const other = { ...base, sessionId: '22222222-2222-2222-2222-222222222222', locationId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' };
    const unassigned = { ...base, sessionId: '33333333-3333-3333-3333-333333333333', locationId: null, locationName: null };

    expect(recordsForLocations([base, other, unassigned], [base.locationId!])).toEqual([base]);
    expect(recordsForLocations([base, other, unassigned], [base.locationId!, other.locationId])).toEqual([base, other]);
    expect(recordsForLocations([base, other, unassigned], [])).toHaveLength(3);
  });

  it('offers the tenant\'s locations and any the records still name, by name', () => {
    const options = locationFilterOptions(
      [{ locationId: 'b', name: 'Malmö' }, { locationId: 'a', name: 'Göteborg' }],
      [{ ...base, locationId: 'c', locationName: 'Copenhagen' }, { ...base, locationId: 'a', locationName: 'Old name' }, { ...base, locationId: null }],
    );

    expect(options).toEqual([{ value: 'c', label: 'Copenhagen' }, { value: 'a', label: 'Göteborg' }, { value: 'b', label: 'Malmö' }]);
  });

  it('works out the success rate, and the average and longest duration', () => {
    expect(successRate({ total: 142, completed: 128, failed: 9, expired: 3, notAuthorized: 2 })).toBe(90);
    expect(successRate({ total: 0, completed: 0, failed: 0, expired: 0, notAuthorized: 0 })).toBeNull();
    expect(durationStats([600_000, null, 1_200_000])).toEqual({ average: 900_000, longest: 1_200_000 });
    expect(durationStats([null])).toEqual({ average: null, longest: null });
  });

  it('compares with the period of the same length just before', () => {
    expect(previousRange({ from: '2026-10-24', to: '2026-11-22' })).toEqual({ from: '2026-09-24', to: '2026-10-23' });
    expect(previousRangeLabel({ from: '2026-10-24', to: '2026-11-22' })).toBe('Previous 30 days');
    expect(previousRange({ from: '2026-11-22', to: '2026-11-22' })).toEqual({ from: '2026-11-21', to: '2026-11-21' });
    expect(previousRangeLabel({ from: '2026-11-22', to: '2026-11-22' })).toBe('Previous day');
    expect(previousRange({ from: '', to: '2026-11-22' })).toBeNull();
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

  it('times imaging from its first step, not from when the device registered', () => {
    expect(durationMilliseconds(base)).toBe(600_000);
    expect(formatDuration(durationMilliseconds(base))).toBe('10m 0s');
    expect(imagingStartTime(base)).toBe(Date.parse('2026-09-11T08:00:00.000Z'));
  });

  it('has no duration for a session that never started imaging', () => {
    const expired = { ...base, finalState: 'SessionExpired', imagingStartedAt: null };
    expect(durationMilliseconds(expired)).toBeNull();
    expect(durationMilliseconds({ ...base, imagingStartedAt: undefined })).toBeNull();
    expect(imagingStartTime(expired)).toBe(0);
  });

  it('rejects invalid or negative durations', () => {
    expect(durationMilliseconds({ ...base, terminalAt: 'invalid' })).toBeNull();
    expect(durationMilliseconds({ ...base, terminalAt: '2026-09-11T07:00:00.000Z' })).toBeNull();
  });
});