import { describe, it, expect } from 'vitest';
import {
  EMPTY_TARGETING_MESSAGE,
  SCRIPT_PHASE_SHORT_LABELS,
  SCRIPT_RUNS_OPTIONS,
  STEP_ITEM_KIND_LABELS,
  allDevicesDisabledReason,
  answerFileEditorSizeLabel,
  contentForSave,
  deleteImpact,
  draftChanges,
  duplicateName,
  effectiveScriptLocations,
  emptyContent,
  groupDevicesBySetupProfile,
  itemCountsSummary,
  itemStatusText,
  itemTooltip,
  loadingScriptLabel,
  locationDisabledReason,
  locationFilterFooter,
  locationsTargetedElsewhere,
  moveScriptBefore,
  moveScriptBy,
  moveScriptTo,
  orderedScripts,
  profileForLocation,
  registryValuePath,
  registryValueTooltip,
  renumberScripts,
  scriptSizeLabel,
  scriptTextProblem,
  scriptTooLargeMessage,
  scriptsForLocation,
  scriptsWithoutLocationsProblem,
  sortItemsFailuresFirst,
  targetingProblem,
  TIMEOUT_OPTIONS,
  timeoutLabel,
  uploadProgressLabel,
  upsertScript,
  utf8Length,
  validateScript,
  validateRegistryValue,
  withEffectiveLocations,
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
      ['HKCU\\Control Panel\\Desktop', 'must be relative to the hive'],
      ['HKCU:\\Control Panel', 'must be relative to the hive'],
      ['HKLM:\\SOFTWARE\\X', 'must be relative to the hive'],
      ['HKU\\.DEFAULT\\X', 'must be relative to the hive'],
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
      { tone: 'succeeded', text: '2 Offline scripts succeeded' },
      { tone: 'queued', text: '1 script and 5 driver installers queued for Specialize' },
    ]);
    expect(itemCountsSummary([
      { kind: 'RegistryValue', outcome: 'Succeeded', count: 7 },
      { kind: 'WinPEScript', outcome: 'Failed', count: 1 },
      { kind: 'AnswerFile', outcome: 'Succeeded', count: 1 },
    ])).toEqual([
      { tone: 'failed', text: '1 Offline script failed' },
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
    expect(itemStatusText({ kind: 'WindowsSetupScript', outcome: 'Queued', reason: null, exitCode: null })).toBe('Queued for Specialize');
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

  describe('Start imaging overlay', () => {
    it('groups devices by the profile their location resolves to, without versions', () => {
      const lines = groupDevicesBySetupProfile(
        [{ locationId: 'sto' }, { locationId: 'sto' }, { locationId: 'got' }, { locationId: 'mal' }, { locationId: null }],
        profiles,
      );
      expect(lines).toEqual([
        '1 device: Default profile',
        '1 device: Gothenburg lab',
        '2 devices: Stockholm',
        '1 device started from media without a location: Default profile',
      ]);
      expect(lines.join(' ')).not.toMatch(/version/i);
    });

    it('reads No setup profile when nothing targets a device', () => {
      const located = profiles.filter((p) => !p.targeting.allDevices);
      expect(groupDevicesBySetupProfile([{ locationId: 'sto' }, { locationId: 'mal' }, { locationId: null }], located)).toEqual(['1 device: Stockholm', '2 devices: No setup profile']);
    });

    it('shows nothing when no profiles exist', () => {
      expect(groupDevicesBySetupProfile([{ locationId: 'sto' }], [])).toEqual([]);
    });
  });

  describe('scripts', () => {
    const script = (name: string, runs: SetupScript['runs'], order: number, extra: Partial<SetupScript> = {}): SetupScript =>
      ({ id: null, name, runs, order, timeoutMinutes: 5, locations: { all: true, locationIds: [] }, contentSha256: 'a'.repeat(64), sizeBytes: 6, ...extra });
    const at = (...locationIds: string[]) => ({ locations: { all: false, locationIds } });
    const names = (list: SetupScript[]) => list.map((s) => `${s.runs}:${String(s.order)}:${s.name}`);
    const list = [script('Setup B', 'WindowsSetup', 2), script('PE B', 'WinPE', 2), script('Setup A', 'WindowsSetup', 1), script('PE A', 'WinPE', 1)];
    const eight = orderedScripts([...Array.from({ length: 8 }, (_, i) => script(`O${String(i + 1)}`, 'WinPE', i + 1)), script('S1', 'WindowsSetup', 1)]);

    it('names the phases Offline and Specialize, on the timeline too', () => {
      expect(SCRIPT_PHASE_SHORT_LABELS).toEqual({ WinPE: 'Offline', WindowsSetup: 'Specialize' });
      expect(SCRIPT_RUNS_OPTIONS.map((o) => o.label)).toEqual(['Offline', 'Specialize']);
      expect(SCRIPT_RUNS_OPTIONS[1].description).toBe('During Windows setup, after the restart and before OOBE and Autopilot. Logged on the device only.');
      expect(STEP_ITEM_KIND_LABELS.WinPEScript).toBe('Offline script');
      expect(STEP_ITEM_KIND_LABELS.WindowsSetupScript).toBe('Specialize script');
      expect(itemCountsSummary([{ kind: 'WindowsSetupScript', outcome: 'Failed', count: 2 }])).toEqual([{ tone: 'failed', text: '2 Specialize scripts failed' }]);
    });

    it('lists Offline scripts first, with order restarting in each phase', () => {
      expect(names(orderedScripts(list))).toEqual(['WinPE:1:PE A', 'WinPE:2:PE B', 'WindowsSetup:1:Setup A', 'WindowsSetup:2:Setup B']);
      expect(names(renumberScripts([script('A', 'WinPE', 3), script('B', 'WinPE', 7)]))).toEqual(['WinPE:1:A', 'WinPE:2:B']);
    });

    it('moves a script to a typed order by inserting it and renumbering its phase only', () => {
      const up = moveScriptTo(eight, 7, 3);
      expect(up.filter((s) => s.runs === 'WinPE').map((s) => s.name)).toEqual(['O1', 'O2', 'O8', 'O3', 'O4', 'O5', 'O6', 'O7']);
      expect(up.map((s) => s.order)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 1]);
      expect(moveScriptTo(eight, 1, 5).filter((s) => s.runs === 'WinPE').map((s) => s.name)).toEqual(['O1', 'O3', 'O4', 'O5', 'O2', 'O6', 'O7', 'O8']);
      expect(moveScriptTo(eight, 0, 99).filter((s) => s.runs === 'WinPE').map((s) => s.name).at(-1)).toBe('O1');
      expect(names(moveScriptTo(eight, 8, 1)).at(-1)).toBe('WindowsSetup:1:S1');
    });

    it('places a typed move among the visible rows when a filter is on', () => {
      const visible = [eight[1], eight[4], eight[7]];
      expect(moveScriptTo(eight, 7, 3, visible).filter((s) => s.runs === 'WinPE').map((s) => s.name)).toEqual(['O1', 'O2', 'O3', 'O4', 'O8', 'O5', 'O6', 'O7']);
      expect(moveScriptTo(eight, 1, 8, visible).filter((s) => s.runs === 'WinPE').map((s) => s.name)).toEqual(['O1', 'O3', 'O4', 'O5', 'O6', 'O7', 'O8', 'O2']);
    });

    it('moves by one visible row with the arrow keys, and before a row when dropped', () => {
      expect(moveScriptBy(eight, 2, -1).filter((s) => s.runs === 'WinPE').map((s) => s.name).slice(0, 3)).toEqual(['O1', 'O3', 'O2']);
      expect(moveScriptBy(eight, 0, -1)).toEqual(eight);
      expect(moveScriptBy(eight, 7, 1)).toEqual(eight);
      const visible = [eight[1], eight[4], eight[7]];
      expect(moveScriptBy(eight, 7, -1, visible).filter((s) => s.runs === 'WinPE').map((s) => s.name)).toEqual(['O1', 'O2', 'O3', 'O4', 'O8', 'O5', 'O6', 'O7']);
      expect(moveScriptBefore(eight, 7, 2).filter((s) => s.runs === 'WinPE').map((s) => s.name).slice(0, 4)).toEqual(['O1', 'O2', 'O8', 'O3']);
      expect(moveScriptBefore(eight, 0, 8)).toEqual(eight);
    });

    it('adds a script last in its phase and moves an edited script to the end of a new phase', () => {
      const ordered = orderedScripts(list);
      expect(names(upsertScript(ordered, script('PE C', 'WinPE', 0)))).toEqual(['WinPE:1:PE A', 'WinPE:2:PE B', 'WinPE:3:PE C', 'WindowsSetup:1:Setup A', 'WindowsSetup:2:Setup B']);
      expect(names(upsertScript(ordered, { ...ordered[0], runs: 'WindowsSetup' }, 0))).toEqual(['WinPE:1:PE B', 'WindowsSetup:1:Setup A', 'WindowsSetup:2:Setup B', 'WindowsSetup:3:PE A']);
      expect(names(upsertScript(ordered, { ...ordered[1], name: 'PE B2' }, 1))).toEqual(['WinPE:1:PE A', 'WinPE:2:PE B2', 'WindowsSetup:1:Setup A', 'WindowsSetup:2:Setup B']);
    });

    it('shows sizes, progress and timeouts as the dialogs and table read them', () => {
      expect(scriptSizeLabel(1_887_437)).toBe('1.8 of 24 MB');
      expect(answerFileEditorSizeLabel(104_858)).toBe('0.1 of 2 MB');
      expect(scriptTooLargeMessage(26_528_972)).toBe('This script is 25.3 MB. A script can be at most 24 MB.');
      expect(scriptTooLargeMessage(25_165_825)).toBe('This script is 24.1 MB. A script can be at most 24 MB.');
      expect(uploadProgressLabel(10_276_045, 19_084_082)).toBe('Uploading 9.8 of 18.2 MB');
      expect(loadingScriptLabel(13_002_342)).toBe('Loading the script (12.4 MB)');
      expect(TIMEOUT_OPTIONS.map((o) => o.value)).toEqual(['1', '2', '5', '10', '15', '20', '30']);
      expect(TIMEOUT_OPTIONS[0].label).toBe('1 minute');
      expect(timeoutLabel(5)).toBe('5 min');
    });

    it('measures UTF-8 the way Imaging Core does, and refuses an empty or oversized text', () => {
      expect(utf8Length('a\u00e4\u20ac\u{1F600}')).toBe(new TextEncoder().encode('a\u00e4\u20ac\u{1F600}').length);
      expect(scriptTextProblem(' \n')).toBe('The script is empty. Add the script text.');
      expect(scriptTextProblem('x'.repeat(25_165_824))).toBeNull();
      expect(scriptTextProblem(`\uFEFF${'x'.repeat(25_165_824)}`)).toBeNull();
      expect(scriptTextProblem('x'.repeat(25_165_825))).toBe('This script is 24.1 MB. A script can be at most 24 MB.');
    });

    it('applies the same rules as Imaging Core', () => {
      expect(validateScript(script('A', 'WinPE', 1), [])).toEqual([]);
      expect(validateScript(script('A', 'WinPE', 1), [script('a', 'WindowsSetup', 1)])).toContain('Script names must be unique. Rename "A" and save again.');
      expect(validateScript(script('A', 'WinPE', 1, { timeoutMinutes: 45 }), [])).toContain('The timeout of "A" must be 1 to 30 minutes.');
      expect(validateScript(script('', 'WinPE', 1), [])).toContain('Every script needs a name of 1 to 100 characters.');
      expect(validateScript(script('A', 'WinPE', 1, at()), [])).toContain('Choose at least one location, or run the script at all locations of this profile.');
    });

    it('marks a changed text or changed locations as a content change, and saves references only', () => {
      const saved = draft({ content: { ...emptyContent(), scripts: [script('A', 'WinPE', 1, at('osl', 'ber'))] } });
      expect(draftChanges(saved, draft({ content: { ...saved.content, scripts: [script('A', 'WinPE', 1, { ...at('osl', 'ber'), contentSha256: 'b'.repeat(64) })] } })).contentChanged).toBe(true);
      expect(draftChanges(saved, draft({ content: { ...saved.content, scripts: [script('A', 'WinPE', 1, at('osl'))] } })).contentChanged).toBe(true);
      expect(draftChanges(saved, draft({ content: { ...saved.content, scripts: [script('A', 'WinPE', 1, at('ber', 'osl'))] } })).contentChanged).toBe(false);
      expect(contentForSave(saved.content).scripts[0]).toEqual({ id: null, name: 'A', runs: 'WinPE', order: 1, timeoutMinutes: 5, locations: { all: false, locationIds: ['ber', 'osl'] }, contentSha256: 'a'.repeat(64) });
      expect(contentForSave({ ...emptyContent(), answerFile: { contentSha256: 'c'.repeat(64), sizeBytes: 9, passes: ['oobeSystem'] } }).answerFile).toEqual({ contentSha256: 'c'.repeat(64) });
    });
  });

  describe('scripts per location', () => {
    const script = (name: string, order: number, locations: string[] | null, runs: SetupScript['runs'] = 'WinPE'): SetupScript => ({
      id: name, name, runs, order, timeoutMinutes: 5, contentSha256: 'a'.repeat(64), sizeBytes: 1,
      locations: locations === null ? { all: true, locationIds: [] } : { all: false, locationIds: locations },
    });
    const nordics = { allDevices: false, locationIds: ['osl', 'ber', 'cph'] };
    const all = ['osl', 'ber', 'cph', 'sto'];
    const scripts = [
      script('Wake on LAN', 1, null),
      script('Oslo print server', 2, ['osl']),
      script('Nordic certificates', 3, ['ber', 'osl', 'cph']),
      script('Bergen only', 4, ['ber']),
      script('802.1X', 1, null, 'WindowsSetup'),
    ];

    it('lists exactly what a device from a location runs, in run order', () => {
      expect(scriptsForLocation(scripts, 'osl', nordics, all).map((s) => s.name)).toEqual(['Wake on LAN', 'Oslo print server', 'Nordic certificates', '802.1X']);
      expect(scriptsForLocation(scripts, 'cph', nordics, all).map((s) => s.name)).toEqual(['Wake on LAN', 'Nordic certificates', '802.1X']);
      expect(locationFilterFooter(4, 186, 'Oslo Office')).toBe('4 of 186 scripts run on devices from Oslo Office');
    });

    it('keeps only the locations the profile still targets, and any location for All devices', () => {
      const narrowed = { allDevices: false, locationIds: ['ber'] };
      expect(effectiveScriptLocations(scripts[2], narrowed, all)).toEqual(['ber']);
      expect(effectiveScriptLocations(scripts[0], narrowed, all)).toBeNull();
      expect(effectiveScriptLocations(script('Stockholm', 1, ['sto']), { allDevices: true, locationIds: [] }, all)).toEqual(['sto']);
      expect(withEffectiveLocations({ ...emptyContent(), scripts }, narrowed, all).scripts[2].locations).toEqual({ all: false, locationIds: ['ber'] });
    });

    it('blocks the save when a script has no locations left', () => {
      expect(scriptsWithoutLocationsProblem(scripts, nordics, all)).toBeNull();
      expect(scriptsWithoutLocationsProblem(scripts, { allDevices: false, locationIds: ['cph'] }, all))
        .toBe('"Oslo print server" has no locations left. Choose a location or delete the script.');
    });
  });
});
