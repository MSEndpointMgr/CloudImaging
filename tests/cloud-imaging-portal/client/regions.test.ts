import { describe, it, expect } from 'vitest';
import {
  describeNames,
  regionChanges,
  regionCodeFor,
  toRegionsPayload,
  validateRegionCode,
  type CountryRegion,
  type RegionDefinition,
  type RegionSummary,
} from '../../../src/cloud-imaging-portal/client/src/lib/regions.ts';

/**
 * A location's region follows from its country. The Regions tab edits that mapping, so the
 * helpers must preview the right code, send only real overrides, and warn about every location
 * whose group tag would change.
 */

const regions: RegionDefinition[] = [
  { regionId: 'emea', code: 'EMEA' },
  { regionId: 'amer', code: 'AMCS' },
  { regionId: 'apac', code: 'APAC' },
];

const countries: CountryRegion[] = [
  { countryCode: 'SE', regionId: 'emea', defaultRegionId: 'emea' },
  { countryCode: 'US', regionId: 'amer', defaultRegionId: 'amer' },
  { countryCode: 'IN', regionId: 'emea', defaultRegionId: 'apac' },
  { countryCode: 'AQ', regionId: null, defaultRegionId: null },
  { countryCode: 'JP', regionId: null, defaultRegionId: 'apac' },
];

describe('regionCodeFor', () => {
  it('returns the current code of the country region', () => {
    expect(regionCodeFor(regions, countries, 'US')).toBe('AMCS');
    expect(regionCodeFor(regions, countries, 'IN')).toBe('EMEA');
  });

  it('returns null without a country, for unknown countries and for countries without a region', () => {
    expect(regionCodeFor(regions, countries, '')).toBeNull();
    expect(regionCodeFor(regions, countries, null)).toBeNull();
    expect(regionCodeFor(regions, countries, 'ZZ')).toBeNull();
    expect(regionCodeFor(regions, countries, 'AQ')).toBeNull();
  });
});

describe('toRegionsPayload', () => {
  it('sends only countries that differ from the built-in map, including removals, and strips counts', () => {
    const summaries: RegionSummary[] = [{ regionId: 'emea', code: 'EMEA', countryCount: 1, locationCount: 0 }];
    const payload = toRegionsPayload(summaries, countries);
    expect(payload.countryOverrides).toEqual({ IN: 'emea', JP: null });
    expect(payload.regions).toEqual([{ regionId: 'emea', code: 'EMEA' }]);
  });
});

describe('validateRegionCode', () => {
  it('requires a code with allowed characters and length', () => {
    expect(validateRegionCode('  ', regions)).toBe('A region code is required.');
    expect(validateRegionCode('EU WEST', regions)).toMatch(/letters, digits/);
    expect(validateRegionCode('A'.repeat(17), regions)).toMatch(/letters, digits/);
    expect(validateRegionCode('LATAM', regions)).toBeNull();
  });

  it('rejects a duplicate code regardless of case, except for the region being renamed', () => {
    expect(validateRegionCode('emea', regions)).toBe('Region EMEA already exists.');
    expect(validateRegionCode('emea', regions, 'emea')).toBeNull();
  });
});

describe('regionChanges', () => {
  const before = (code: string | null | undefined) => regionCodeFor(regions, countries, code);
  const moved = countries.map((c) => (c.countryCode === 'SE' ? { ...c, regionId: 'apac' } : c));
  const after = (code: string | null | undefined) => regionCodeFor(regions, moved, code);

  it('lists locations whose country now maps to another region', () => {
    const changes = regionChanges(
      [
        { name: 'Gothenburg', countryCode: 'SE' },
        { name: 'Austin', countryCode: 'US' },
        { name: 'Unassigned', countryCode: null },
      ],
      before,
      after,
    );
    expect(changes).toEqual([{ name: 'Gothenburg', from: 'EMEA', to: 'APAC' }]);
  });

  it('ignores locations that still use a typed region', () => {
    expect(regionChanges([{ name: 'Lund', countryCode: 'SE', regionIsLegacy: true }], before, after)).toEqual([]);
  });
});

describe('describeNames', () => {
  it('joins up to three names and summarises the rest', () => {
    expect(describeNames([])).toBe('');
    expect(describeNames(['Malmo'])).toBe('Malmo');
    expect(describeNames(['Uppsala', 'Malmo', 'Gothenburg'])).toBe('Gothenburg, Malmo and Uppsala');
    expect(describeNames(['E', 'D', 'C', 'B', 'A'])).toBe('A, B, C and 2 other locations');
  });
});
