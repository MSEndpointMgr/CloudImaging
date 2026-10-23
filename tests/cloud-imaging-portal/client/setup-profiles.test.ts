import { describe, it, expect } from 'vitest';
import {
  EMPTY_TARGETING_MESSAGE,
  allDevicesDisabledReason,
  deleteImpact,
  draftChanges,
  duplicateName,
  emptyContent,
  itemCountsSummary,
  locationDisabledReason,
  profileForLocation,
  registryKeyPath,
  registryValueTooltip,
  saveLabel,
  targetingProblem,
  validateRegistryValue,
  type SetupProfileDraft,
  type SetupProfileSummary,
  type SetupRegistryValue,
} from '../../../src/cloud-imaging-portal/client/src/lib/setupProfiles.ts';

/**
 * Setup profile helpers: the picker must explain why a location or All devices is taken, the save
 * bar must say whether a save creates a version, and the editor must catch the same registry
 * problems as Imaging Core before the operator saves.
 */

const summary = (overrides: Partial<SetupProfileSummary>): SetupProfileSummary => ({
  profileId: 'p1',
  name: 'Stockholm',
  targeting: { allDevices: false, locationIds: ['sto'] },
  registryValueCount: 0,
  scriptCount: 0,
  hasAnswerFile: false,
  failureHandling: 'CompleteWithWarnings',
  currentVersion: 1,
  lastModifiedAt: '2026-10-22T14:03:00Z',
  ...overrides,
});

const value = (overrides: Partial<SetupRegistryValue> = {}): SetupRegistryValue => ({
  id: null,
  name: 'Turn on automatic time zone',
  hive: 'System',
  key: 'CurrentControlSet\\Services\\tzautoupdate',
  valueName: 'Start',
  type: 'DWord',
  data: '3',
  ...overrides,
});

const draft = (overrides: Partial<SetupProfileDraft> = {}): SetupProfileDraft => ({
  name: 'Stockholm',
  targeting: { allDevices: false, locationIds: ['sto'] },
  content: { ...emptyContent(), registryValues: [value()] },
  ...overrides,
});

describe('Portal frontend: setup profile helpers', () => {
  const profiles = [
    summary({ profileId: 'p1', name: 'Stockholm', targeting: { allDevices: false, locationIds: ['sto'] } }),
    summary({ profileId: 'p2', name: 'Gothenburg lab', targeting: { allDevices: false, locationIds: ['got'] } }),
    summary({ profileId: 'p3', name: 'Default profile', targeting: { allDevices: true, locationIds: [] } }),
  ];

  it('names the profile that already targets a location, but not for the profile being edited', () => {
    expect(locationDisabledReason(profiles, 'p1', 'got')).toBe('Targeted by Gothenburg lab');
    expect(locationDisabledReason(profiles, 'p1', 'sto')).toBeUndefined();
    expect(locationDisabledReason(profiles, undefined, 'mal')).toBeUndefined();
  });

  it('names the profile that already uses All devices', () => {
    expect(allDevicesDisabledReason(profiles, 'p1')).toBe('Already used by Default profile');
    expect(allDevicesDisabledReason(profiles, 'p3')).toBeUndefined();
  });

  it('resolves a location to its own profile first, then All devices', () => {
    expect(profileForLocation(profiles, 'got')?.name).toBe('Gothenburg lab');
    expect(profileForLocation(profiles, 'mal')?.name).toBe('Default profile');
    expect(profileForLocation(profiles, null)?.name).toBe('Default profile');
  });

  it('tells content changes apart from name or targeting changes', () => {
    const saved = draft();
    expect(draftChanges(saved, draft())).toEqual({ contentChanged: false, metadataChanged: false, dirty: false });
    expect(draftChanges(saved, draft({ name: 'Stockholm HQ' }))).toEqual({ contentChanged: false, metadataChanged: true, dirty: true });
    expect(draftChanges(saved, draft({ targeting: { allDevices: false, locationIds: ['sto', 'got'] } })).contentChanged).toBe(false);
    expect(draftChanges(saved, draft({ content: { ...saved.content, failureHandling: 'FailDeployment' } })).contentChanged).toBe(true);
    expect(draftChanges(saved, draft({ content: { ...saved.content, registryValues: [value({ data: '4' })] } })).contentChanged).toBe(true);
  });

  it('ignores the order of locations when comparing targeting', () => {
    const saved = draft({ targeting: { allDevices: false, locationIds: ['a', 'b'] } });
    expect(draftChanges(saved, draft({ targeting: { allDevices: false, locationIds: ['b', 'a'] } })).dirty).toBe(false);
  });

  it('labels the save button by whether a version is created', () => {
    expect(saveLabel(7, true)).toBe('Save as version 8');
    expect(saveLabel(7, false)).toBe('Save changes');
    expect(saveLabel(0, true)).toBe('Save as version 1');
  });

  it('refuses empty targeting', () => {
    expect(targetingProblem({ allDevices: false, locationIds: [] })).toBe('Choose at least one location or All devices.');
    expect(EMPTY_TARGETING_MESSAGE).toBe('Choose at least one location or All devices.');
    expect(targetingProblem({ allDevices: true, locationIds: [] })).toBeNull();
    expect(targetingProblem({ allDevices: false, locationIds: ['sto'] })).toBeNull();
  });

  it('proposes a free duplicate name', () => {
    expect(duplicateName('Stockholm', ['Stockholm'])).toBe('Stockholm (copy)');
    expect(duplicateName('Stockholm', ['Stockholm', 'Stockholm (copy)'])).toBe('Stockholm (copy 2)');
    expect(duplicateName('Stockholm', ['Stockholm', 'stockholm (COPY)', 'Stockholm (copy 2)'])).toBe('Stockholm (copy 3)');
  });

  it('describes the impact of deleting a profile', () => {
    expect(deleteImpact(['Gothenburg Office', 'Stockholm HQ'])).toBe(
      'Devices started from media for these locations stop getting this profile: Gothenburg Office, Stockholm HQ. Deployments already running keep the version they started with. History is kept.',
    );
  });

  it('formats a registry value path per hive with name and type in the tooltip', () => {
    expect(registryKeyPath(value({ hive: 'Software', key: 'Policies\\Microsoft\\FVE' }))).toBe('HKLM\\SOFTWARE\\Policies\\Microsoft\\FVE');
    expect(registryKeyPath(value({ hive: 'System', key: 'Setup' }))).toBe('HKLM\\SYSTEM\\Setup');
    expect(registryKeyPath(value({ hive: 'DefaultUser', key: 'Control Panel\\Desktop' }))).toBe('Default user\\Control Panel\\Desktop');
    expect(registryValueTooltip(value({ valueName: 'X', type: 'DWord' }))).toBe('Value name: X. Type: DWORD.');
    expect(registryValueTooltip(value({ valueName: '', type: 'MultiString' }))).toBe('Value name: (Default). Type: Multi-string.');
  });

  describe('registry value rules', () => {
    it('accepts a valid value', () => {
      expect(validateRegistryValue(value(), [])).toEqual([]);
    });

    it.each([
      ['HKLM\\SOFTWARE\\X', 'must be relative to the hive'],
      ['HKEY_LOCAL_MACHINE\\X', 'must be relative to the hive'],
      ['\\X', 'must be relative to the hive'],
      ['A\\\\B', 'must be relative to the hive'],
      ['', 'must be 1 to 512 characters'],
    ])('refuses key %s', (key, problem) => {
      expect(validateRegistryValue(value({ hive: 'Software', key }), []).join(' ')).toContain(problem);
    });

    it('allows CurrentControlSet only under HKLM SYSTEM', () => {
      expect(validateRegistryValue(value({ hive: 'Software' }), []).join(' ')).toContain('only exists under HKLM SYSTEM');
      expect(validateRegistryValue(value({ hive: 'System' }), [])).toEqual([]);
    });

    it.each([
      ['DWord', '4294967295', true],
      ['DWord', '4294967296', false],
      ['DWord', '0xFFFFFFFF', true],
      ['DWord', '0x100000000', false],
      ['DWord', '-1', false],
      ['QWord', '18446744073709551615', true],
      ['QWord', '18446744073709551616', false],
      ['QWord', '0xFFFFFFFFFFFFFFFF', true],
    ] as const)('%s data %s valid: %s', (type, data, valid) => {
      expect(validateRegistryValue(value({ type, data }), []).length === 0).toBe(valid);
    });

    it('refuses empty lines in a multi-string', () => {
      expect(validateRegistryValue(value({ type: 'MultiString', data: 'a\n\nb' }), []).join(' ')).toContain('cannot contain empty lines');
    });

    it('refuses two values that set the same thing', () => {
      const other = value({ name: 'Earlier value' });
      expect(validateRegistryValue(value(), [other])).toContain('"Turn on automatic time zone" sets the same value as "Earlier value". Keep one of them.');
    });
  });

  it('summarizes item counts per kind', () => {
    expect(itemCountsSummary([
      { kind: 'RegistryValue', outcome: 'Succeeded', count: 3 },
      { kind: 'WinPEScript', outcome: 'Succeeded', count: 1 },
      { kind: 'WinPEScript', outcome: 'Failed', count: 1 },
      { kind: 'WindowsSetupScript', outcome: 'Queued', count: 2 },
    ])).toBe('3 registry values written. 1 of 2 WinPE scripts failed. 2 items queued for Windows setup.');
    expect(itemCountsSummary([{ kind: 'RegistryValue', outcome: 'Succeeded', count: 1 }, { kind: 'AnswerFile', outcome: 'Succeeded', count: 1 }]))
      .toBe('1 registry value written. Answer file written.');
  });
});
