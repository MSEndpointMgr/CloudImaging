import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';

const api = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry: vi.fn(),
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => api);

import { ProfileHistoryDialog } from '../../../src/cloud-imaging-portal/client/src/components/setup-profiles/ProfileHistoryDialog.tsx';
import {
  changesLabel,
  inUseTooltip,
  readOnlyVersionBanner,
  restoreImpact,
  sectionChangeText,
  targetingHistory,
  versionFields,
  versionsFootnote,
  type SetupProfileVersionSummary,
} from '../../../src/cloud-imaging-portal/client/src/lib/setupProfiles.ts';

/**
 * History: one row per version with a generic count and the details behind a chevron, Current and
 * In use status, and Restore that saves an old version's content as a new current version.
 */
const none = { added: 0, removed: 0, changed: 0 };
const version = (overrides: Partial<SetupProfileVersionSummary> = {}): SetupProfileVersionSummary => ({
  version: 6,
  createdAt: '2026-10-21T14:30:00Z',
  createdBy: 'anna@contoso.com',
  changeSummary: { registryValues: none, scripts: { ...none, changed: 1 }, answerFile: 'NoChange', failureHandling: null, names: { registryValues: [], scripts: ['Register device in asset inventory'] }, total: 1 },
  isCurrent: false,
  inUseCount: 2,
  ...overrides,
});

describe('Portal frontend: setup profile history', () => {
  afterEach(() => { cleanup(); vi.clearAllMocks(); });

  it('describes each section of an expanded version as labelled fields', () => {
    expect(versionFields(version())).toEqual([
      { label: 'Registry values', value: 'No change' },
      { label: 'Scripts', value: '"Register device in asset inventory" changed' },
      { label: 'Answer file', value: 'No change' },
      { label: 'Failure handling', value: 'No change' },
    ]);
    const fields = versionFields(version({
      changeSummary: { registryValues: { added: 1, removed: 0, changed: 2 }, scripts: { ...none, reordered: true }, answerFile: 'Changed', failureHandling: { from: 'CompleteWithWarnings', to: 'FailDeployment' }, names: { registryValues: ['A', 'B', 'C'], scripts: [] }, total: 6 },
      restoredFrom: 5,
      duplicatedFrom: { profileId: 'p', profileName: 'Oslo', version: 3 },
    }));
    expect(fields.map((f) => f.value)).toEqual(['1 added, 2 changed: "A", "B", "C"', 'Reordered', 'Changed', 'Complete with warnings to Fail the deployment', 'Version 5', 'Oslo, version 3']);
    expect(fields.map((f) => f.label)).toContain('Restored from');
    expect(fields.map((f) => f.label)).toContain('Duplicated from');
  });

  it('counts names past the ten that are kept', () => {
    const names = Array.from({ length: 10 }, (_, i) => `V${String(i)}`);
    expect(sectionChangeText({ added: 12, removed: 0, changed: 0 }, names)).toMatch(/"V9", and 2 more added$/);
  });

  it('words counts, status, footnote, restore and the read-only banner', () => {
    expect(changesLabel(1)).toBe('1 change');
    expect(changesLabel(2)).toBe('2 changes');
    expect(inUseTooltip(1)).toBe('1 running deployment uses this version');
    expect(inUseTooltip(3)).toBe('3 running deployments use this version');
    expect(versionsFootnote(7, 25)).toBe('7 versions. The 25 most recent are kept, plus older ones running deployments still use.');
    expect(versionsFootnote(1, null)).toBe('1 version. The most recent are kept, plus older ones running deployments still use.');
    expect(restoreImpact(6, 7)).toBe("Saves version 6's registry values, scripts, answer file and failure handling as version 8. Devices imaged from now on get it; running deployments keep the version they started with. Targeting is not changed.");
    expect(readOnlyVersionBanner(5, 8)).toBe('Version 5, read-only. The current version is 8.');
    expect(readOnlyVersionBanner(8, 8)).toBe('Version 8, read-only. This is the current version.');
  });

  it('keeps version entries out of the Targeting tab', () => {
    const entries = [
      { kind: 'VersionSaved' as const, at: 'a', change: 'Saved version 2' },
      { kind: 'TargetingChanged' as const, at: 'b', change: 'Added Gothenburg Office' },
      { kind: 'Duplicated' as const, at: 'c', change: 'Duplicated from Oslo, version 3' },
      { kind: 'Renamed' as const, at: 'd', change: 'Renamed from Oslo' },
      { kind: 'Created' as const, at: 'e', change: 'Created' },
    ];
    expect(targetingHistory(entries).map((e) => e.change)).toEqual(['Added Gothenburg Office', 'Renamed from Oslo', 'Created']);
  });

  it('lists versions with status, shows details behind the chevron and restores for managers', async () => {
    api.apiFetchWithRetry.mockImplementation(async (url: string) => new Response(JSON.stringify(url.endsWith('/versions')
      ? [version({ version: 7, isCurrent: true, inUseCount: 0 }), version()]
      : [{ kind: 'TargetingChanged', at: '2026-10-21T14:31:00Z', by: 'anna@contoso.com', change: 'Added Gothenburg Office' }]), { status: 200 }));
    api.apiFetch.mockResolvedValue(new Response(JSON.stringify({ profileId: 'p1', currentVersion: 8 }), { status: 200 }));
    const onRestored = vi.fn();

    render(<ProfileHistoryDialog profile={{ profileId: 'p1', name: 'Stockholm and Gothenburg', currentVersion: 7 }} retention={25} canRestore onClose={() => undefined} onView={() => undefined} onRestored={onRestored} />);

    expect(screen.getByText('History of Stockholm and Gothenburg')).toBeTruthy();
    expect(await screen.findByText('Current')).toBeTruthy();
    expect(screen.getByText('In use')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Restore version 7' })).toBeNull();
    expect(screen.getByText('2 versions. The 25 most recent are kept, plus older ones running deployments still use.')).toBeTruthy();

    fireEvent.click(screen.getAllByRole('button', { name: 'Show changes' })[1]);
    expect(screen.getByText('"Register device in asset inventory" changed')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Restore version 6' }));
    const dialog = screen.getByRole('dialog', { name: 'Restore version 6?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Restore' }));
    await vi.waitFor(() => expect(onRestored).toHaveBeenCalled());
    expect(JSON.parse((api.apiFetch.mock.calls[0][1] as RequestInit).body as string)).toEqual({ version: 6, baseVersion: 7 });

    fireEvent.click(screen.getByRole('tab', { name: 'Targeting' }));
    expect(screen.getByText('Added Gothenburg Office')).toBeTruthy();
  });

  it('offers no Restore without the manage capability', async () => {
    api.apiFetchWithRetry.mockImplementation(async (url: string) => new Response(JSON.stringify(url.endsWith('/versions') ? [version({ version: 7, isCurrent: true, inUseCount: 0 }), version()] : []), { status: 200 }));

    render(<ProfileHistoryDialog profile={{ profileId: 'p1', name: 'Lab', currentVersion: 7 }} retention={null} canRestore={false} onClose={() => undefined} onView={() => undefined} onRestored={() => undefined} />);

    expect(await screen.findByRole('button', { name: 'View version 6' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Restore/ })).toBeNull();
  });
});
