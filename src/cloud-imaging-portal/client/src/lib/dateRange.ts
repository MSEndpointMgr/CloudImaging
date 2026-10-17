/**
 * Date range presets for the report filter bar.
 *
 * Reports work in whole UTC days: the API is called with `T00:00:00Z` through `T23:59:59Z` bounds,
 * so every boundary here is computed in UTC. Using local dates would shift the window by a day for
 * anyone east or west of UTC at the edges of the day.
 */

export interface DateRange {
  /** Inclusive lower bound, `yyyy-mm-dd`. */
  from: string;
  /** Inclusive upper bound, `yyyy-mm-dd`. */
  to: string;
}

export type DateRangePresetKey =
  | 'last7'
  | 'last30'
  | 'last90'
  | 'thisMonth'
  | 'lastMonth'
  | 'custom';

export const DATE_RANGE_PRESETS: readonly { key: DateRangePresetKey; label: string }[] = [
  { key: 'last7', label: 'Last 7 days' },
  { key: 'last30', label: 'Last 30 days' },
  { key: 'last90', label: 'Last 90 days' },
  { key: 'thisMonth', label: 'This month' },
  { key: 'lastMonth', label: 'Last month' },
  { key: 'custom', label: 'Custom range…' },
];

const DAY_MS = 86_400_000;

/** `yyyy-mm-dd` for a date, in UTC. */
export function toIsoDate(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function utcDay(iso: string): number | null {
  const parsed = Date.parse(`${iso}T00:00:00Z`);
  return Number.isNaN(parsed) ? null : parsed;
}

/** Resolves a preset to concrete bounds. Returns null for `custom`, which has no fixed bounds. */
export function resolvePreset(key: DateRangePresetKey, now: Date = new Date()): DateRange | null {
  const todayMs = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  const today = new Date(todayMs);

  switch (key) {
    case 'last7':
      return { from: toIsoDate(new Date(todayMs - 6 * DAY_MS)), to: toIsoDate(today) };
    case 'last30':
      return { from: toIsoDate(new Date(todayMs - 29 * DAY_MS)), to: toIsoDate(today) };
    case 'last90':
      return { from: toIsoDate(new Date(todayMs - 89 * DAY_MS)), to: toIsoDate(today) };
    case 'thisMonth':
      return {
        from: toIsoDate(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1))),
        to: toIsoDate(today),
      };
    case 'lastMonth': {
      const firstOfLast = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1));
      const lastOfLast = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 0));
      return { from: toIsoDate(firstOfLast), to: toIsoDate(lastOfLast) };
    }
    case 'custom':
      return null;
  }
}

/**
 * Which preset, if any, produces exactly this range. Lets a range restored from the URL show as
 * "Last 30 days" rather than as a custom range that happens to be 30 days long.
 */
export function matchPreset(range: DateRange, now: Date = new Date()): DateRangePresetKey {
  for (const { key } of DATE_RANGE_PRESETS) {
    const resolved = resolvePreset(key, now);
    if (resolved && resolved.from === range.from && resolved.to === range.to) return key;
  }
  return 'custom';
}

/** Inclusive day count, or null when either bound is missing or unparseable. */
export function rangeDayCount(range: DateRange): number | null {
  const from = utcDay(range.from);
  const to = utcDay(range.to);
  if (from === null || to === null || to < from) return null;
  return Math.round((to - from) / DAY_MS) + 1;
}

/** True when both bounds are present, parseable and in order. */
export function isValidRange(range: DateRange): boolean {
  return rangeDayCount(range) !== null;
}

function formatDay(iso: string): string {
  const ms = utcDay(iso);
  if (ms === null) return iso;
  return new Date(ms).toLocaleDateString(undefined, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

/** Trigger text for a resolved range, e.g. "1 Aug 2026 to 14 Sep 2026". */
export function describeRange(range: DateRange): string {
  if (!isValidRange(range)) return 'Custom range';
  return `${formatDay(range.from)} to ${formatDay(range.to)}`;
}

/** Label shown on the trigger: the preset name, or the resolved dates for a custom range. */
export function rangeLabel(range: DateRange, preset: DateRangePresetKey): string {
  if (preset === 'custom') return describeRange(range);
  return DATE_RANGE_PRESETS.find(p => p.key === preset)?.label ?? describeRange(range);
}
