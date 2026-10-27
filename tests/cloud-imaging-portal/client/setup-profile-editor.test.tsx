import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

const { apiFetch, apiFetchWithRetry, notify } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry: vi.fn(),
  notify: vi.fn(() => 'toast'),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch,
  apiFetchWithRetry,
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
  getApiAccessToken: vi.fn(async () => null),
}));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/toastContext.tsx', () => ({ useToast: () => ({ notify, update: vi.fn() }) }));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/authContext.tsx', () => ({
  useAuth: () => ({ canManageSetupProfiles: true, canViewSetupProfiles: true, isAdministrator: false }),
}));

import SetupProfileEditorPage from '../../../src/cloud-imaging-portal/client/src/pages/SetupProfileEditorPage.tsx';

const ANSWER_FILE = '<?xml version="1.0"?>\n<unattend xmlns="urn:schemas-microsoft-com:unattend">\n  <settings pass="oobeSystem"/>\n</unattend>';
const ANSWER_SHA = 'f'.repeat(64);
const SCRIPT_SHA = 'e'.repeat(64);

const locations = [
  { locationId: 'osl', name: 'Oslo Office' },
  { locationId: 'ber', name: 'Bergen Office' },
  { locationId: 'cph', name: 'Copenhagen Office' },
];

const script = (id: string, name: string, order: number, locationIds: string[] | null, runs = 'WinPE') => ({
  id, name, runs, order, timeoutMinutes: 5, contentSha256: SCRIPT_SHA, sizeBytes: 6,
  locations: locationIds === null ? { all: true, locationIds: [] } : { all: false, locationIds },
  lastModifiedAt: '2026-10-22T10:00:00Z', lastModifiedBy: 'ops@contoso.com',
});

const profile = {
  profileId: 'p1',
  name: 'Stockholm',
  targeting: { allDevices: true, locationIds: [] },
  registryValueCount: 0,
  scriptCount: 0,
  hasAnswerFile: true,
  failureHandling: 'CompleteWithWarnings',
  currentVersion: 8,
  lastModifiedAt: '2026-10-22T10:00:00Z',
  lastModifiedBy: 'ops@contoso.com',
  createdAt: '2026-10-01T10:00:00Z',
  createdBy: 'ops@contoso.com',
  content: {
    failureHandling: 'CompleteWithWarnings',
    registryValues: [],
    scripts: [] as ReturnType<typeof script>[],
    answerFile: { contentSha256: ANSWER_SHA, passes: ['oobeSystem'], sizeBytes: ANSWER_FILE.length, lastModifiedAt: '2026-10-22T10:00:00Z', lastModifiedBy: 'ops@contoso.com' },
  },
};

const nordics = {
  ...profile,
  name: 'Nordics',
  targeting: { allDevices: false, locationIds: ['osl', 'ber', 'cph'] },
  content: {
    ...profile.content,
    answerFile: null,
    scripts: [
      script('s1', 'Wake on LAN', 1, null),
      script('s2', 'Oslo print server', 2, ['osl']),
      script('s3', 'Nordic certificates', 3, ['ber', 'osl', 'cph']),
      script('s4', 'Bergen only', 4, ['ber']),
      script('s5', 'Wired 802.1X', 1, null, 'WindowsSetup'),
    ],
  },
};

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

function serve(current: object = profile) {
  apiFetchWithRetry.mockImplementation(async (url: string) => {
    if (url === '/api/setup-profiles') return json([current]);
    if (url === '/api/locations') return json(locations);
    if (url === '/api/setup-profiles/p1') return json(current);
    if (url === `/api/setup-profiles/p1/content/${ANSWER_SHA}`) return new Response(ANSWER_FILE, { status: 200 });
    if (url === `/api/setup-profiles/p1/content/${SCRIPT_SHA}`) return new Response('exit 0', { status: 200 });
    if (url === '/api/setup-profiles/p1/versions/8') return json({ version: 8, createdAt: profile.lastModifiedAt, createdBy: 'ops@contoso.com', isCurrent: true, inUseCount: 0, content: (current as typeof profile).content, changeSummary: {} });
    if (url === '/api/setup-profiles/p1/versions') {
      return json([{ version: 8, createdAt: profile.lastModifiedAt, createdBy: 'ops@contoso.com', isCurrent: true, inUseCount: 0, changeSummary: { registryValues: { added: 0, removed: 0, changed: 0 }, scripts: { added: 0, removed: 0, changed: 0 }, answerFile: 'Added', failureHandling: null, names: { registryValues: [], scripts: [] }, total: 1 } }]);
    }
    if (url === '/api/setup-profiles/p1/history') return json([]);
    return json({}, 404);
  });
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/setup-profiles/:profileId" element={<SetupProfileEditorPage />} />
        <Route path="/setup-profiles/:profileId/versions/:version" element={<SetupProfileEditorPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('Portal frontend: setup profile editor', () => {
  afterEach(() => { cleanup(); vi.clearAllMocks(); });

  it('keeps Save disabled while the answer file the server refused is unchanged', async () => {
    serve();
    apiFetch.mockResolvedValue(json({ detail: 'The answer file has 1 problem.', errors: [{ code: 'autoLogon', line: 3, message: 'AutoLogon signs in a local account before Autopilot can run. Remove the AutoLogon element.' }] }, 400));
    renderAt('/setup-profiles/p1');

    fireEvent.change(await screen.findByLabelText('Profile name'), { target: { value: 'Stockholm HQ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    expect(screen.getByText('The answer file has problems. Fix them before saving.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('opens the current version read-only from History, like any other version', async () => {
    serve();
    renderAt('/setup-profiles/p1');

    fireEvent.click(await screen.findByRole('button', { name: 'History' }));
    fireEvent.click(await screen.findByRole('button', { name: 'View version 8' }));

    expect(await screen.findByText('Version 8, read-only. This is the current version.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Restore' })).toBeNull();
    expect(screen.queryByLabelText('Profile name')).toBeNull();
  });

  it('lets a viewer open the answer file read-only', async () => {
    serve();
    renderAt('/setup-profiles/p1/versions/8');

    fireEvent.click(await screen.findByRole('button', { name: 'View the answer file' }));

    const dialog = within(await screen.findByRole('dialog'));
    const editor = (await dialog.findByDisplayValue((value) => value.includes('<settings pass="oobeSystem"/>'))) as HTMLTextAreaElement;
    expect(editor.value).toBe(ANSWER_FILE);
    expect(editor.readOnly).toBe(true);
    expect(dialog.getByText('Answer file')).toBeTruthy();
    expect(dialog.queryByRole('button', { name: 'Done' })).toBeNull();
    expect(dialog.queryByRole('button', { name: /Upload file/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove the answer file' })).toBeNull();
    fireEvent.click(dialog.getAllByRole('button', { name: 'Close' }).at(-1)!);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  describe('scripts card', () => {
    const rowNames = () => screen.getAllByTestId(/^script-row-/).map((row) => row.getAttribute('data-testid')!.replace('script-row-', ''));
    const orders = () => screen.getAllByRole('button', { name: /^Change the order of / }).map((b) => b.textContent);

    it('names the phases, shows each script\'s locations, and has no move buttons', async () => {
      serve(nordics);
      renderAt('/setup-profiles/p1');

      expect(await screen.findByText('PowerShell, run as SYSTEM in the order below within each phase. A device runs the scripts for all locations and the ones for its own location.')).toBeTruthy();
      const oslo = screen.getByTestId('script-row-Oslo print server');
      expect(oslo.textContent).toContain('Oslo Office');
      expect(oslo.textContent).toContain('Offline');
      expect(screen.getByTestId('script-row-Wired 802.1X').textContent).toContain('Specialize');
      expect(screen.getByTestId('script-row-Wake on LAN').textContent).toContain('All locations');
      expect(screen.queryByRole('button', { name: /up$/ })).toBeNull();
      expect(screen.getByText('5 scripts')).toBeTruthy();
    });

    it('filters by name and by the location a device starts from, with the count in the footer', async () => {
      serve(nordics);
      renderAt('/setup-profiles/p1');
      await screen.findByTestId('script-row-Bergen only');

      fireEvent.change(screen.getByLabelText('Search scripts'), { target: { value: 'oslo' } });
      expect(rowNames()).toEqual(['Oslo print server']);
      expect(screen.getByText('1 of 5 scripts')).toBeTruthy();
      fireEvent.change(screen.getByLabelText('Search scripts'), { target: { value: '' } });

      fireEvent.click(screen.getByLabelText('Show the scripts for'));
      fireEvent.click(await screen.findByRole('option', { name: 'Oslo Office' }));

      expect(rowNames()).toEqual(['Wake on LAN', 'Oslo print server', 'Nordic certificates', 'Wired 802.1X']);
      expect(screen.getByText('4 of 5 scripts run on devices from Oslo Office')).toBeTruthy();
    });

    it('moves a script to a typed order on Enter and cancels on Escape', async () => {
      serve(nordics);
      renderAt('/setup-profiles/p1');
      await screen.findByTestId('script-row-Bergen only');

      fireEvent.click(screen.getByRole('button', { name: 'Change the order of Bergen only, now 4' }));
      const input = screen.getByLabelText('Order of Bergen only');
      fireEvent.change(input, { target: { value: '2' } });
      fireEvent.keyDown(input, { key: 'Enter' });

      expect(rowNames()).toEqual(['Wake on LAN', 'Bergen only', 'Oslo print server', 'Nordic certificates', 'Wired 802.1X']);
      expect(orders()).toEqual(['1', '2', '3', '4', '1']);
      expect(screen.getByText('1 unsaved change.')).toBeTruthy();

      fireEvent.click(screen.getByRole('button', { name: 'Change the order of Wake on LAN, now 1' }));
      fireEvent.change(screen.getByLabelText('Order of Wake on LAN'), { target: { value: '4' } });
      fireEvent.keyDown(screen.getByLabelText('Order of Wake on LAN'), { key: 'Escape' });
      expect(rowNames()[0]).toBe('Wake on LAN');
    });

    it('moves a script one row with the arrow keys on its drag handle, within its phase', async () => {
      serve(nordics);
      renderAt('/setup-profiles/p1');
      await screen.findByTestId('script-row-Bergen only');

      fireEvent.keyDown(screen.getByRole('button', { name: 'Move Oslo print server. Use the up and down arrow keys.' }), { key: 'ArrowUp' });
      expect(rowNames().slice(0, 2)).toEqual(['Oslo print server', 'Wake on LAN']);

      fireEvent.keyDown(screen.getByRole('button', { name: 'Move Bergen only. Use the up and down arrow keys.' }), { key: 'ArrowDown' });
      expect(rowNames().slice(3)).toEqual(['Bergen only', 'Wired 802.1X']);
    });

    it('moves a script before or after a row it is dropped on, within its phase only', async () => {
      serve(nordics);
      renderAt('/setup-profiles/p1');
      await screen.findByTestId('script-row-Bergen only');
      const handle = (name: string) => screen.getByRole('button', { name: `Move ${name}. Use the up and down arrow keys.` });
      const dataTransfer = { setData: vi.fn(), effectAllowed: '' };

      fireEvent.dragStart(handle('Bergen only'), { dataTransfer });
      fireEvent.dragOver(screen.getByTestId('script-row-Wired 802.1X'), { clientY: -1 });
      fireEvent.dragOver(screen.getByTestId('script-row-Wake on LAN'), { clientY: -1 });
      fireEvent.drop(screen.getByTestId('script-row-Wake on LAN'));
      expect(rowNames()).toEqual(['Bergen only', 'Wake on LAN', 'Oslo print server', 'Nordic certificates', 'Wired 802.1X']);

      fireEvent.dragStart(handle('Nordic certificates'), { dataTransfer });
      fireEvent.dragOver(screen.getByTestId('script-row-Oslo print server'), { clientY: -1 });
      fireEvent.drop(screen.getByTestId('script-row-Oslo print server'));
      expect(rowNames()).toEqual(['Bergen only', 'Wake on LAN', 'Nordic certificates', 'Oslo print server', 'Wired 802.1X']);

      fireEvent.dragStart(handle('Wired 802.1X'), { dataTransfer });
      fireEvent.drop(screen.getByTestId('script-row-Wake on LAN'));
      expect(rowNames().at(-1)).toBe('Wired 802.1X');
      fireEvent.dragEnd(handle('Wired 802.1X'));
    });

    it('lets a viewer open a script read-only, without handles or edit actions', async () => {
      serve(nordics);
      renderAt('/setup-profiles/p1/versions/8');
      await screen.findByTestId('script-row-Bergen only');

      expect(screen.queryByRole('button', { name: /^Move / })).toBeNull();
      expect(screen.queryByRole('button', { name: /^Edit / })).toBeNull();
      fireEvent.click(screen.getByRole('button', { name: 'View Oslo print server' }));

      expect(await screen.findByDisplayValue('exit 0')).toBeTruthy();
      const dialog = within(screen.getByRole('dialog'));
      expect(dialog.getByText('Oslo Office')).toBeTruthy();
      expect(dialog.queryByRole('button', { name: 'Done' })).toBeNull();
      fireEvent.click(dialog.getAllByRole('button', { name: 'Close' }).at(-1)!);
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    it('blocks the save when removing a location leaves a script with no locations', async () => {
      serve({ ...nordics, targeting: { allDevices: false, locationIds: ['osl', 'ber'] } });
      renderAt('/setup-profiles/p1');
      await screen.findByTestId('script-row-Bergen only');

      fireEvent.click(await screen.findByRole('checkbox', { name: /Bergen Office/ }));

      expect(screen.getByTestId('script-row-Bergen only').textContent).toContain('No locations');
      expect(screen.getByText('"Bergen only" has no locations left. Choose a location or delete the script.')).toBeTruthy();
      expect((screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true);
    });

    it('opens a script, loads its text, and shows the locations it can choose from', async () => {
      serve(nordics);
      renderAt('/setup-profiles/p1');

      fireEvent.click(await screen.findByRole('button', { name: 'Edit Nordic certificates' }));

      expect(await screen.findByDisplayValue('exit 0')).toBeTruthy();
      const dialog = within(screen.getByRole('dialog'));
      expect(dialog.getByText('3 of 3 selected')).toBeTruthy();
      expect(dialog.getByText('Only locations this profile targets')).toBeTruthy();
      expect(apiFetchWithRetry).toHaveBeenCalledWith(`/api/setup-profiles/p1/content/${SCRIPT_SHA}`, expect.anything());
    });
  });
});
