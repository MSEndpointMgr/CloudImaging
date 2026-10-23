import { describe, it, expect } from 'vitest';
import { groupTagChange, preProvisioningLabel, resolveTemplate, stateLabel, stateVariant, validateGroupTagDefinition, validateResolvedTag, type GroupTagKind } from '../../../src/cloud-imaging-portal/client/src/lib/autopilot.ts';
import sharedCases from '../../fixtures/autopilot-group-tag-cases.json';
import { countryName, countryOptions } from '../../../src/cloud-imaging-portal/client/src/lib/countries.ts';
import { resolveSectionTitle } from '../../../src/cloud-imaging-portal/client/src/lib/routeTitles.ts';

/**
 * The approval dialog previews the group tag a template will produce, and Imaging Core resolves it
 * again at import time. These rules must agree with the server's, or an approver sees one tag and
 * Intune receives another.
 */
describe('Portal frontend: Autopilot group tag templates', () => {
  const stockholm = { name: 'Stockholm HQ', region: 'EMEA', countryCode: 'SE' };

  it('resolves every token against the device location', () => {
    expect(resolveTemplate('{Region}-{CountryCode}-STD', stockholm)).toEqual({ value: 'EMEA-SE-STD', reason: null });
    expect(resolveTemplate('{LocationName}', stockholm)).toEqual({ value: 'Stockholm HQ', reason: null });
  });

  it('explains why a template cannot resolve', () => {
    expect(resolveTemplate('{Region}-STD', { name: 'Lab' })).toEqual({ value: null, reason: 'The location has no value for {Region}.' });
    expect(resolveTemplate('{Region}-STD', {})).toEqual({ value: null, reason: 'The device has no location.' });
  });

  it('refuses a resolved value Intune would reject', () => {
    expect(resolveTemplate('{LocationName}', { name: 'Malmö/Lund' }).value).toBeNull();
    expect(validateResolvedTag('x'.repeat(129))).toMatch(/128 characters/);
    expect(validateResolvedTag('EMEA-SE_Std.1')).toBeNull();
  });

  it('validates definitions before they are saved', () => {
    expect(validateGroupTagDefinition('Static', '  ')).toBe('A value is required.');
    expect(validateGroupTagDefinition('Static', 'EMEA')).toBeNull();
    expect(validateGroupTagDefinition('Template', 'EMEA')).toMatch(/at least one token/);
    expect(validateGroupTagDefinition('Template', '{City}-STD')).toBe('Unknown token {City}.');
    expect(validateGroupTagDefinition('Template', '{Region}/STD')).toMatch(/Literal text/);
    expect(validateGroupTagDefinition('Template', '{Region}-{CountryCode}')).toBeNull();
  });

  it('labels every request state', () => {
    expect(preProvisioningLabel(true)).toBe('Supported');
    expect(preProvisioningLabel(false)).toMatch(/no TPM 2.0 data/);
    expect(preProvisioningLabel(null)).toBe('Unknown');
    expect(stateLabel('PendingApproval')).toBe('Pending approval');
    expect(stateVariant('ImportFailed')).toBe('negative');
    expect(stateVariant('AlreadyRegistered')).toBe('success');
    expect(stateLabel('GroupTagUpdated')).toBe('Group tag updated');
  });

  it('shows a tag change on an existing Autopilot device as old to new', () => {
    expect(groupTagChange({ state: 'GroupTagUpdated', existingDevice: true, previousGroupTag: 'OLD', groupTag: 'NEW' })).toBe('OLD \u2192 NEW');
    expect(groupTagChange({ state: 'GroupTagUpdated', existingDevice: true, previousGroupTag: null, groupTag: 'NEW' })).toBe('(none) \u2192 NEW');
    expect(groupTagChange({ state: 'Imported', existingDevice: false, groupTag: 'NEW' })).toBe('NEW');
    expect(groupTagChange({ state: 'Rejected', groupTag: null })).toBeNull();
  });
});

/** The same cases Imaging Core's tests run, so the preview and the server can never disagree. */
describe('Portal frontend: group tag cases shared with Imaging Core', () => {
  it.each(sharedCases.definitions)('definition $kind "$value" valid=$valid', ({ kind, value, valid }) => {
    expect(validateGroupTagDefinition(kind as GroupTagKind, value) === null).toBe(valid);
  });

  it.each(sharedCases.resolutions)('resolves "$template"', ({ template, locationName, region, countryCode, value, reason }) => {
    expect(resolveTemplate(template, { name: locationName, region, countryCode })).toEqual({ value, reason });
  });
});

describe('Portal frontend: country list', () => {
  it('offers each ISO code once, sorted by name', () => {
    const options = countryOptions();
    const codes = options.map((o) => o.code);
    expect(new Set(codes).size).toBe(codes.length);
    expect(codes).toContain('SE');
    expect([...options].sort((a, b) => a.name.localeCompare(b.name))).toEqual(options);
  });

  it('names a code, and falls back to the code itself when unknown', () => {
    expect(countryName('SE')).toBe(countryOptions().find((o) => o.code === 'SE')?.name);
    expect(countryName('ZZ')).toBe('ZZ');
    expect(countryName(null)).toBeNull();
  });
});

describe('Portal frontend: Autopilot route titles', () => {
  it('names the Autopilot queue and its history report', () => {
    expect(resolveSectionTitle('/autopilot')).toBe('Autopilot Registrations');
    expect(resolveSectionTitle('/reports/autopilot-registrations')).toBe('Autopilot Registration History');
  });
});
