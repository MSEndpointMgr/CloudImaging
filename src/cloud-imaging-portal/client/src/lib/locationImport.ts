import { countryOptions } from './countries.ts';

/** One entry as it appears in an import file, before validation. */
export interface LocationImportRow {
  name: string;
  countryCode: string | null;
}

export type ImportAction = 'create' | 'duplicate' | 'invalid';

export interface ImportEntry {
  /** 1-based position in the file, so an error can name the offending entry. */
  index: number;
  name: string;
  countryCode: string | null;
  action: ImportAction;
  /** Why the entry cannot be created, for `duplicate` and `invalid`. */
  reason?: string;
}

export interface ImportPlan {
  entries: ImportEntry[];
  createCount: number;
  duplicateCount: number;
  invalidCount: number;
}

/** Thrown for a file that is not an importable shape at all, as opposed to bad rows within one. */
export class LocationImportError extends Error {}

const NAME_MAX_LENGTH = 100;

function knownCountryCodes(): Set<string> {
  return new Set(countryOptions().map(c => c.code));
}

/**
 * Parses an import file into rows.
 *
 * Accepts a bare array, or an object with a `locations` array, because both are shapes people
 * reach for and rejecting one of them is an arbitrary obstacle. Country is accepted under
 * `countryCode` or `country`, since the portal shows the column as "Country".
 *
 * Region is deliberately not read. It is derived from the country by the region mapping, so a
 * region in the file would either agree with that mapping and be redundant, or disagree with it
 * and be silently discarded.
 */
export function parseLocationImport(text: string): LocationImportRow[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new LocationImportError('That file is not valid JSON.');
  }

  const raw =
    Array.isArray(parsed) ? parsed
    : typeof parsed === 'object' && parsed !== null && Array.isArray((parsed as { locations?: unknown }).locations)
      ? (parsed as { locations: unknown[] }).locations
      : null;

  if (!raw) {
    throw new LocationImportError('Expected a list of locations, or an object with a "locations" list.');
  }
  if (raw.length === 0) {
    throw new LocationImportError('That file contains no locations.');
  }

  return raw.map(item => {
    const row = (typeof item === 'object' && item !== null ? item : {}) as Record<string, unknown>;
    const country = row.countryCode ?? row.country;
    return {
      name: typeof row.name === 'string' ? row.name.trim() : '',
      countryCode: typeof country === 'string' && country.trim() !== '' ? country.trim().toUpperCase() : null,
    };
  });
}

/**
 * Decides what each row would do, without changing anything.
 *
 * Names are not unique in the catalog, so nothing stops the same file being imported twice and
 * silently doubling every location. Matching on name here is what makes an import repeatable.
 */
export function buildImportPlan(rows: LocationImportRow[], existingNames: readonly string[]): ImportPlan {
  const countries = knownCountryCodes();
  const taken = new Set(existingNames.map(n => n.trim().toLowerCase()));
  const entries: ImportEntry[] = [];

  rows.forEach((row, i) => {
    const index = i + 1;
    const key = row.name.toLowerCase();

    if (!row.name) {
      entries.push({ index, ...row, action: 'invalid', reason: 'No name' });
      return;
    }
    if (row.name.length > NAME_MAX_LENGTH) {
      entries.push({ index, ...row, action: 'invalid', reason: `Name is longer than ${NAME_MAX_LENGTH} characters` });
      return;
    }
    if (row.countryCode !== null && !countries.has(row.countryCode)) {
      entries.push({ index, ...row, action: 'invalid', reason: `"${row.countryCode}" is not an ISO country code` });
      return;
    }
    if (taken.has(key)) {
      // Covers both an existing location and an earlier row of this same file.
      entries.push({ index, ...row, action: 'duplicate', reason: 'A location with this name already exists' });
      return;
    }

    taken.add(key);
    entries.push({ index, ...row, action: 'create' });
  });

  return {
    entries,
    createCount: entries.filter(e => e.action === 'create').length,
    duplicateCount: entries.filter(e => e.action === 'duplicate').length,
    invalidCount: entries.filter(e => e.action === 'invalid').length,
  };
}
