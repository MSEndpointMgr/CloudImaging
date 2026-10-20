/**
 * Country-to-region mapping shown on the Locations page's Regions tab. Imaging Core owns the
 * built-in map and resolves regions for group tags; these helpers only edit and preview it.
 */

export interface RegionDefinition {
  regionId: string;
  code: string;
}

export interface RegionSummary extends RegionDefinition {
  countryCount: number;
  locationCount: number;
}

export interface CountryRegion {
  countryCode: string;
  /** Current region id; null when the country has no region. */
  regionId: string | null;
  /** Region id from the built-in map; null for uninhabited territories. */
  defaultRegionId: string | null;
}

export interface RegionsView {
  regions: RegionSummary[];
  countries: CountryRegion[];
  lastModifiedAt: string;
}

export interface RegionsPayload {
  regions: RegionDefinition[];
  countryOverrides: Record<string, string | null>;
}

/** A location as far as region resolution is concerned. */
export interface RegionLocation {
  name: string;
  countryCode?: string | null;
  regionIsLegacy?: boolean;
}

export const MAX_REGION_CODE_LENGTH = 16;
const REGION_CODE_PATTERN = /^[A-Za-z0-9_-]+$/;

/** Region code a country resolves to, or null when it has none. */
export function regionCodeFor(regions: RegionDefinition[], countries: CountryRegion[], countryCode: string | null | undefined): string | null {
  if (!countryCode) return null;
  const regionId = countries.find((c) => c.countryCode === countryCode)?.regionId;
  return regions.find((r) => r.regionId === regionId)?.code ?? null;
}

/** PUT body: every region, plus only the countries that differ from the built-in map. */
export function toRegionsPayload(regions: RegionDefinition[], countries: CountryRegion[]): RegionsPayload {
  const countryOverrides: Record<string, string | null> = {};
  for (const country of countries) {
    if (country.regionId !== country.defaultRegionId) countryOverrides[country.countryCode] = country.regionId;
  }
  return { regions: regions.map(({ regionId, code }) => ({ regionId, code })), countryOverrides };
}

/** Returns a validation message for a region code, or null when it can be used. */
export function validateRegionCode(code: string, regions: RegionDefinition[], exceptRegionId?: string): string | null {
  const trimmed = code.trim();
  if (!trimmed) return 'A region code is required.';
  if (trimmed.length > MAX_REGION_CODE_LENGTH || !REGION_CODE_PATTERN.test(trimmed)) {
    return `Use up to ${String(MAX_REGION_CODE_LENGTH)} letters, digits, hyphens or underscores.`;
  }
  const clash = regions.some((r) => r.regionId !== exceptRegionId && r.code.toUpperCase() === trimmed.toUpperCase());
  return clash ? `Region ${trimmed.toUpperCase()} already exists.` : null;
}

export interface RegionChange {
  name: string;
  from: string | null;
  to: string | null;
}

/**
 * Locations whose region changes between two mappings. Locations still on a typed region keep it
 * until they are edited, so they never change here.
 */
export function regionChanges(
  locations: RegionLocation[],
  before: (countryCode: string | null | undefined) => string | null,
  after: (countryCode: string | null | undefined) => string | null,
): RegionChange[] {
  return locations
    .filter((l) => !l.regionIsLegacy && l.countryCode)
    .map((l) => ({ name: l.name, from: before(l.countryCode), to: after(l.countryCode) }))
    .filter((c) => c.from !== c.to);
}

/** "Gothenburg, Malmö and Uppsala" or "Aarhus, Bergen, Gothenburg and 34 other locations". */
export function describeNames(names: string[], noun = 'locations'): string {
  const sorted = [...names].sort((a, b) => a.localeCompare(b));
  if (sorted.length <= 3) {
    return sorted.length <= 1 ? (sorted[0] ?? '') : `${sorted.slice(0, -1).join(', ')} and ${sorted[sorted.length - 1]}`;
  }
  return `${sorted.slice(0, 3).join(', ')} and ${String(sorted.length - 3)} other ${noun}`;
}
