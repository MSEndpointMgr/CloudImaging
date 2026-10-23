import { describe, expect, it } from 'vitest';
import {
  buildImportPlan,
  parseLocationImport,
  LocationImportError,
} from '../../../src/cloud-imaging-portal/client/src/lib/locationImport.ts';

describe('Portal frontend: location import', () => {
  describe('parsing', () => {
    it('accepts a bare array', () => {
      const rows = parseLocationImport('[{"name":"Seattle HQ","countryCode":"US"}]');
      expect(rows).toEqual([{ name: 'Seattle HQ', countryCode: 'US' }]);
    });

    it('accepts an object wrapping a locations array', () => {
      const rows = parseLocationImport('{"locations":[{"name":"Copenhagen","countryCode":"DK"}]}');
      expect(rows).toEqual([{ name: 'Copenhagen', countryCode: 'DK' }]);
    });

    it('accepts "country" as well as "countryCode"', () => {
      // The portal column is labelled Country, so a hand-written file plausibly uses that.
      expect(parseLocationImport('[{"name":"Oslo","country":"no"}]')[0].countryCode).toBe('NO');
    });

    it('normalises the country code to upper case and trims the name', () => {
      const [row] = parseLocationImport('[{"name":"  Lisbon  ","countryCode":" pt "}]');
      expect(row).toEqual({ name: 'Lisbon', countryCode: 'PT' });
    });

    it('treats a missing or blank country as no country', () => {
      expect(parseLocationImport('[{"name":"Remote"}]')[0].countryCode).toBeNull();
      expect(parseLocationImport('[{"name":"Remote","countryCode":"  "}]')[0].countryCode).toBeNull();
    });

    it('ignores a region in the file, because the country decides it', () => {
      const [row] = parseLocationImport('[{"name":"Madrid","countryCode":"ES","region":"APAC"}]');
      expect(row).toEqual({ name: 'Madrid', countryCode: 'ES' });
    });

    it.each([
      ['not json at all', 'not json'],
      ['a JSON value that is not a list', '"hello"'],
      ['an object without a locations list', '{"items":[]}'],
      ['an empty list', '[]'],
    ])('rejects %s', (_label, text) => {
      expect(() => parseLocationImport(text)).toThrow(LocationImportError);
    });
  });

  describe('planning', () => {
    it('marks new entries for creation', () => {
      const plan = buildImportPlan([{ name: 'Seattle HQ', countryCode: 'US' }], []);
      expect(plan.createCount).toBe(1);
      expect(plan.entries[0].action).toBe('create');
    });

    it('skips a location that already exists, ignoring case and surrounding space', () => {
      const plan = buildImportPlan([{ name: 'copenhagen', countryCode: 'DK' }], ['  Copenhagen  ']);
      expect(plan.duplicateCount).toBe(1);
      expect(plan.createCount).toBe(0);
    });

    it('skips a name repeated within the same file', () => {
      // Names are not unique in the catalog, so this is the only thing stopping a file from
      // creating the same location twice in one run.
      const plan = buildImportPlan(
        [{ name: 'Oslo', countryCode: 'NO' }, { name: 'OSLO', countryCode: 'NO' }],
        [],
      );
      expect(plan.createCount).toBe(1);
      expect(plan.duplicateCount).toBe(1);
    });

    it('rejects an unknown country code rather than dropping it silently', () => {
      const plan = buildImportPlan([{ name: 'Nowhere', countryCode: 'ZZ' }], []);
      expect(plan.entries[0].action).toBe('invalid');
      expect(plan.entries[0].reason).toContain('ZZ');
    });

    it('rejects a nameless entry', () => {
      const plan = buildImportPlan([{ name: '', countryCode: 'US' }], []);
      expect(plan.entries[0].action).toBe('invalid');
      expect(plan.invalidCount).toBe(1);
    });

    it('rejects a name longer than the catalog allows', () => {
      const plan = buildImportPlan([{ name: 'x'.repeat(101), countryCode: null }], []);
      expect(plan.entries[0].action).toBe('invalid');
    });

    it('allows an entry with no country, since country is optional', () => {
      const plan = buildImportPlan([{ name: 'Remote workers', countryCode: null }], []);
      expect(plan.createCount).toBe(1);
    });

    it('numbers entries from one so a bad row can be pointed at', () => {
      const plan = buildImportPlan(
        [{ name: 'A', countryCode: null }, { name: '', countryCode: null }],
        [],
      );
      expect(plan.entries.map(e => e.index)).toEqual([1, 2]);
    });

    it('reports a mixed file without losing any entry', () => {
      const plan = buildImportPlan(
        [
          { name: 'New', countryCode: 'SE' },
          { name: 'Existing', countryCode: 'DK' },
          { name: 'Bad', countryCode: 'QQ' },
        ],
        ['Existing'],
      );
      expect(plan).toMatchObject({ createCount: 1, duplicateCount: 1, invalidCount: 1 });
      expect(plan.entries).toHaveLength(3);
    });
  });
});
