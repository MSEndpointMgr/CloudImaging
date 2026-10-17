import { describe, expect, it } from 'vitest';
import {
  describeRange,
  isValidRange,
  matchPreset,
  rangeDayCount,
  rangeLabel,
  resolvePreset,
} from '../../../src/cloud-imaging-portal/client/src/lib/dateRange.ts';

// Fixed point so the preset windows are assertable. Mid-month and mid-year on purpose.
const NOW = new Date('2026-10-17T11:30:00.000Z');

describe('Portal frontend: report date ranges', () => {
  it('resolves rolling windows inclusively', () => {
    // 30 days means today plus the 29 before it, not today minus 30.
    expect(resolvePreset('last30', NOW)).toEqual({ from: '2026-09-18', to: '2026-10-17' });
    expect(resolvePreset('last7', NOW)).toEqual({ from: '2026-10-11', to: '2026-10-17' });
    expect(rangeDayCount(resolvePreset('last30', NOW)!)).toBe(30);
    expect(rangeDayCount(resolvePreset('last7', NOW)!)).toBe(7);
  });

  it('resolves calendar month windows', () => {
    expect(resolvePreset('thisMonth', NOW)).toEqual({ from: '2026-10-01', to: '2026-10-17' });
    expect(resolvePreset('lastMonth', NOW)).toEqual({ from: '2026-09-01', to: '2026-09-30' });
  });

  it('rolls back across a year boundary', () => {
    const january = new Date('2026-01-09T00:30:00.000Z');
    expect(resolvePreset('lastMonth', january)).toEqual({ from: '2025-12-01', to: '2025-12-31' });
  });

  it('computes windows in UTC, so a late local hour does not shift the day', () => {
    // 23:30 UTC on the 17th is already the 18th in much of Asia. The bound must stay the 17th,
    // because the API is called with UTC day bounds.
    const lateEvening = new Date('2026-10-17T23:30:00.000Z');
    expect(resolvePreset('last7', lateEvening)?.to).toBe('2026-10-17');
  });

  it('has no fixed bounds for a custom range', () => {
    expect(resolvePreset('custom', NOW)).toBeNull();
  });

  it('recognises a range that matches a preset, so a restored range is not called custom', () => {
    expect(matchPreset({ from: '2026-09-18', to: '2026-10-17' }, NOW)).toBe('last30');
    expect(matchPreset({ from: '2026-08-01', to: '2026-09-14' }, NOW)).toBe('custom');
  });

  it('rejects an incomplete or inverted range', () => {
    expect(isValidRange({ from: '', to: '2026-10-17' })).toBe(false);
    expect(isValidRange({ from: '2026-10-17', to: '' })).toBe(false);
    expect(isValidRange({ from: 'not-a-date', to: '2026-10-17' })).toBe(false);
    expect(isValidRange({ from: '2026-10-17', to: '2026-10-01' })).toBe(false);
    expect(isValidRange({ from: '2026-10-17', to: '2026-10-17' })).toBe(true);
  });

  it('labels the trigger with the preset name, or the dates for a custom range', () => {
    const last30 = resolvePreset('last30', NOW)!;
    expect(rangeLabel(last30, 'last30')).toBe('Last 30 days');
    expect(rangeLabel({ from: '2026-08-01', to: '2026-09-14' }, 'custom')).toBe(
      describeRange({ from: '2026-08-01', to: '2026-09-14' }),
    );
  });

  it('describes a custom range without shifting the dates into the local zone', () => {
    const described = describeRange({ from: '2026-08-01', to: '2026-09-14' });
    expect(described).toContain('1');
    expect(described).toContain('14');
    expect(described).toContain('2026');
    expect(described).toContain(' to ');
  });
});
