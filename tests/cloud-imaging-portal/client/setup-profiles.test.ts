import { describe, it, expect } from 'vitest';
import {
  EMPTY_TARGETING_MESSAGE,
  allDevicesDisabledReason,
  deleteImpact,
  draftChanges,
  duplicateName,
  emptyContent,
  itemCountsSummary,
  itemStatusText,
  itemTooltip,
  locationDisabledReason,
  locationsTargetedElsewhere,
  moveScript,
  orderedScripts,
  profileForLocation,
  registryValuePath,
  registryValueTooltip,
  renumberScripts,
  saveLabel,
  scriptSizeLabel,
  sortItemsFailuresFirst,
  targetingProblem,
  TIMEOUT_OPTIONS,
  timeoutLabel,
  upsertScript,
  validateScript,
  validateRegistryValue,
  type SetupProfileDraft,
  type SetupProfileSummary,
  type SetupRegistryValue,
  type SetupScript,
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
    expect(allDevicesDisabledReason(profiles, 'p1')).toBe('Used by Default profile. Only one profile can target all devices.');
    expect(allDevicesDisabledReason(profiles, 'p3')).toBeUndefined();
  });

  it('counts the locations other profiles target, for the picker footer', () => {
    expect(locationsTargetedElsewhere(profiles, 'p1', ['sto', 'got', 'mal'])).toBe(1);
    expect(locationsTargetedElsewhere(profiles, undefined, ['sto', 'got', 'mal'])).toBe(2);
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

  it('formats the full registry value path per hive with the type in the tooltip', () => {
    expect(registryValuePath(value({ hive: 'Software', key: 'Policies\\Microsoft\\FVE', valueName: 'EncryptionMethodWithXtsOs' }))).toBe('HKLM\\SOFTWARE\\Policies\\Microsoft\\FVE\\EncryptionMethodWithXtsOs');
    expect(registryValuePath(value({ hive: 'System', key: 'Setup', valueName: '' }))).toBe('HKLM\\SYSTEM\\Setup\\(Default)');
    expect(registryValuePath(value({ hive: 'DefaultUser', key: 'Control Panel\\Desktop', valueName: 'MenuShowDelay' }))).toBe('Default user\\Control Panel\\Desktop\\MenuShowDelay');
    expect(registryValueTooltip(value({ hive: 'System', key: 'CurrentControlSet\\Control\\BitLocker', valueName: 'PreventDeviceEncryption', type: 'DWord' })))
      .toBe('HKLM\\SYSTEM\\CurrentControlSet\\Control\\BitLocker\\PreventDeviceEncryption (DWORD)');
    expect(registryValueTooltip(value({ valueName: '', type: 'MultiString' }))).toContain('\\(Default) (Multi-string)');
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

  it('summarizes item counts as separate items, failures first', () => {
    expect(itemCountsSummary([
      { kind: 'RegistryValue', outcome: 'Succeeded', count: 4 },
      { kind: 'WinPEScript', outcome: 'Succeeded', count: 2 },
      { kind: 'WindowsSetupScript', outcome: 'Queued', count: 1 },
      { kind: 'DriverInstaller', outcome: 'Queued', count: 5 },
    ])).toEqual([
      { tone: 'succeeded', text: '4 registry values written' },
      { tone: 'succeeded', text: '2 WinPE scripts succeeded' },
      { tone: 'queued', text: '1 script and 5 driver installers queued for Windows setup' },
    ]);
    expect(itemCountsSummary([
      { kind: 'RegistryValue', outcome: 'Succeeded', count: 7 },
      { kind: 'WinPEScript', outcome: 'Failed', count: 1 },
      { kind: 'AnswerFile', outcome: 'Succeeded', count: 1 },
    ])).toEqual([
      { tone: 'failed', text: '1 WinPE script failed' },
      { tone: 'succeeded', text: '7 registry values written' },
    ]);
    expect(itemCountsSummary([{ kind: 'RegistryValue', outcome: 'NotRun', count: 2 }, { kind: 'AnswerFile', outcome: 'NotRun', count: 1 }]))
      .toEqual([{ tone: 'notRun', text: '2 registry values not run' }, { tone: 'notRun', text: 'Answer file not run' }]);
  });

  it('describes one item row with a short status and the full reason in the tooltip', () => {
    expect(itemStatusText({ kind: 'WinPEScript', outcome: 'Failed', reason: 'Exited with code 1.', exitCode: 1 })).toBe('Failed: exit code 1');
    expect(itemStatusText({ kind: 'RegistryValue', outcome: 'Failed', reason: 'The SYSTEM hive could not be loaded. Access denied.', exitCode: null })).toBe('Failed: The SYSTEM hive could not be loaded');
    expect(itemStatusText({ kind: 'WinPEScript', outcome: 'Failed', reason: 'Stopped after 5 minutes.', exitCode: null })).toBe('Failed: Stopped after 5 minutes');
    expect(itemStatusText({ kind: 'RegistryValue', outcome: 'Succeeded', reason: null, exitCode: null })).toBe('Written');
    expect(itemStatusText({ kind: 'WinPEScript', outcome: 'Succeeded', reason: null, exitCode: 0 })).toBe('Succeeded');
    expect(itemStatusText({ kind: 'WindowsSetupScript', outcome: 'Queued', reason: null, exitCode: null })).toBe('Queued for Windows setup');
    expect(itemStatusText({ kind: 'AnswerFile', outcome: 'NotRun', reason: 'x', exitCode: null })).toBe('Not run');
    expect(itemTooltip({ reason: 'Exited with code 1.', lastOutputLine: 'Invalid namespace' })).toBe('Exited with code 1. Last output: Invalid namespace');
    expect(itemTooltip({ reason: null, lastOutputLine: null })).toBeUndefined();
  });

  it('sorts failed items first, then not run, then the rest in device order', () => {
    const sorted = sortItemsFailuresFirst([
      { index: 0, outcome: 'Succeeded' as const },
      { index: 1, outcome: 'NotRun' as const },
      { index: 2, outcome: 'Failed' as const },
      { index: 3, outcome: 'Queued' as const },
    ]);
    expect(sorted.map((i) => i.index)).toEqual([2, 1, 0, 3]);
  });

  describe('scripts', () => {
    const script = (name: string, runs: SetupScript['runs'], order: number, extra: Partial<SetupScript> = {}): SetupScript =>
      ({ id: null, name, runs, order, timeoutMinutes: 5, content: 'exit 0', ...extra });
    const names = (list: SetupScript[]) => list.map((s) => `${s.runs}:${String(s.order)}:${s.name}`);
    const list = [script('Setup B', 'WindowsSetup', 2), script('PE B', 'WinPE', 2), script('Setup A', 'WindowsSetup', 1), script('PE A', 'WinPE', 1)];

    it('lists WinPE scripts first, with order restarting in each phase', () => {
      expect(names(orderedScripts(list))).toEqual(['WinPE:1:PE A', 'WinPE:2:PE B', 'WindowsSetup:1:Setup A', 'WindowsSetup:2:Setup B']);
    });

    it('moves a script within its phase only and renumbers contiguously', () => {
      const ordered = orderedScripts(list);
      expect(names(moveScript(ordered, 1, -1))).toEqual(['WinPE:1:PE B', 'WinPE:2:PE A', 'WindowsSetup:1:Setup A', 'WindowsSetup:2:Setup B']);
      expect(names(moveScript(ordered, 1, 1))).toEqual(names(ordered));
      expect(names(moveScript(ordered, 2, -1))).toEqual(names(ordered));
      expect(names(renumberScripts([script('A', 'WinPE', 3), script('B', 'WinPE', 7)]))).toEqual(['WinPE:1:A', 'WinPE:2:B']);
    });

    it('adds a script last in its phase and moves an edited script to the end of a new phase', () => {
      const ordered = orderedScripts(list);
      expect(names(upsertScript(ordered, script('PE C', 'WinPE', 0)))).toEqual(['WinPE:1:PE A', 'WinPE:2:PE B', 'WinPE:3:PE C', 'WindowsSetup:1:Setup A', 'WindowsSetup:2:Setup B']);
      expect(names(upsertScript(ordered, { ...ordered[0], runs: 'WindowsSetup' }, 0))).toEqual(['WinPE:1:PE B', 'WindowsSetup:1:Setup A', 'WindowsSetup:2:Setup B', 'WindowsSetup:3:PE A']);
      expect(names(upsertScript(ordered, { ...ordered[1], name: 'PE B2' }, 1))).toEqual(['WinPE:1:PE A', 'WinPE:2:PE B2', 'WindowsSetup:1:Setup A', 'WindowsSetup:2:Setup B']);
    });

    it('shows size and timeouts as the dialog and table read them', () => {
      expect(scriptSizeLabel('x'.repeat(12_698))).toBe('12.4 of 64 KB');
      expect(TIMEOUT_OPTIONS.map((o) => o.value)).toEqual(['1', '2', '5', '10', '15', '20', '30']);
      expect(TIMEOUT_OPTIONS[0].label).toBe('1 minute');
      expect(TIMEOUT_OPTIONS[2].label).toBe('5 minutes');
      expect(timeoutLabel(5)).toBe('5 min');
    });

    it('applies the same rules as Imaging Core', () => {
      expect(validateScript(script('A', 'WinPE', 1), [])).toEqual([]);
      expect(validateScript(script('A', 'WinPE', 1), [script('a', 'WindowsSetup', 1)])).toContain('Script names must be unique. Rename "A" and save again.');
      expect(validateScript(script('A', 'WinPE', 1, { content: '  ' }), [])).toContain('"A" is empty. Add the script text.');
      expect(validateScript(script('A', 'WinPE', 1, { content: 'x'.repeat(65_537) }), [])).toContain('"A" is larger than 64 KB. Shorten it.');
      expect(validateScript(script('A', 'WinPE', 1, { timeoutMinutes: 45 }), [])).toContain('The timeout of "A" must be 1 to 30 minutes.');
      expect(validateScript(script('', 'WinPE', 1), [])).toContain('Every script needs a name of 1 to 100 characters.');
    });

    it('marks a changed script as a content change', () => {
      const saved = draft({ content: { ...emptyContent(), scripts: [script('A', 'WinPE', 1)] } });
      expect(draftChanges(saved, draft({ content: { ...saved.content, scripts: [script('A', 'WinPE', 1, { content: 'exit 1' })] } })).contentChanged).toBe(true);
      expect(draftChanges(saved, draft({ content: { ...saved.content, scripts: [script('A', 'WinPE', 1)] } })).contentChanged).toBe(false);
    });
  });
});
