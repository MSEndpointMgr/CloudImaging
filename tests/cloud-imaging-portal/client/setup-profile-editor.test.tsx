import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
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
}));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/toastContext.tsx', () => ({ useToast: () => ({ notify, update: vi.fn() }) }));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/authContext.tsx', () => ({
  useAuth: () => ({ canManageSetupProfiles: true, canViewSetupProfiles: true, isAdministrator: false }),
}));

import SetupProfileEditorPage from '../../../src/cloud-imaging-portal/client/src/pages/SetupProfileEditorPage.tsx';

const ANSWER_FILE = '<?xml version="1.0"?>\n<unattend xmlns="urn:schemas-microsoft-com:unattend">\n  <settings pass="oobeSystem"/>\n</unattend>';

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
    scripts: [],
    answerFile: { content: ANSWER_FILE, passes: ['oobeSystem'], sizeBytes: ANSWER_FILE.length, lastModifiedAt: '2026-10-22T10:00:00Z', lastModifiedBy: 'ops@contoso.com' },
  },
};

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

function serve() {
  apiFetchWithRetry.mockImplementation(async (url: string) => {
    if (url === '/api/setup-profiles') return json([profile]);
    if (url === '/api/locations') return json([]);
    if (url === '/api/setup-profiles/p1') return json(profile);
    if (url === '/api/setup-profiles/p1/versions/8') return json({ version: 8, createdAt: profile.lastModifiedAt, createdBy: 'ops@contoso.com', isCurrent: true, inUseCount: 0, content: profile.content, changeSummary: {} });
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
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    expect(screen.getByText('The answer file has problems. Fix them before saving.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Save changes' }) as HTMLButtonElement).disabled).toBe(true);
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
});
