import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

const { apiFetch, notify, auth } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  notify: vi.fn(() => 'toast'),
  auth: { canManageDrivers: true, canViewDrivers: true },
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch,
  apiFetchWithRetry: apiFetch,
  extractErrorDetail: vi.fn(async (res: Response, fallback: string) => {
    try {
      const body = await res.json() as { detail?: string };
      return body.detail ?? fallback;
    } catch {
      return fallback;
    }
  }),
  getApiAccessToken: vi.fn(async () => null),
}));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/authContext.tsx', () => ({ useAuth: () => auth }));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/toastContext.tsx', () => ({ useToast: () => ({ notify, update: vi.fn(), dismiss: vi.fn() }) }));

import DriverModelPage, { CHECK_POLL_MS, MODEL_POLL_MS } from '../../../src/cloud-imaging-portal/client/src/pages/DriverModelPage.tsx';
import { FIRMWARE_POLL_MS } from '../../../src/cloud-imaging-portal/client/src/components/drivers/FirmwareTab.tsx';

type Handler = (init?: RequestInit) => { status?: number; body?: unknown };

function serve(routes: Record<string, Handler | { status?: number; body?: unknown }>): void {
  apiFetch.mockImplementation(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${url}`;
    const route = routes[key];
    if (!route) return new Response(JSON.stringify({ detail: `unexpected ${key}` }), { status: 500 });
    const { status = 200, body } = typeof route === 'function' ? route(init) : route;
    return new Response(body === undefined ? null : JSON.stringify(body), { status });
  });
}

const modelId = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';

const modelPage = {
  model: {
    modelId, vendor: 'Hp', displayName: 'HP EliteBook X G2a 14 inch Notebook Next Gen AI PC', matchKeys: ['8EF5'], architecture: 'X64', releases: ['Windows11_24H2'],
    state: 'Active', included: 31, skipped: 9, maxDownloadBytes: 2_410_000_000, status: { waitingForApproval: 0, failedItems: 0, syncOff: false, downloading: null },
  },
  release: 'Windows11_24H2', checkAvailableAt: null, checkBlockedReason: null, setVersion: 7, builtAt: '2026-10-25T02:41:00Z',
  hasPreviousSet: true, previousSetVersion: 6, pendingVersion: null, pending: null, preparing: null,
  tiles: { offline: 0, offlineAndSpecialize: 0, specialize: 0, skip: 0, downloadPerDeviceBytes: 0 },
  components: [],
};

function bios(overrides: Record<string, unknown> = {}) {
  return {
    platformId: '8EF5', name: 'System BIOS', version: '01.02.04', lastUpdated: '2026-07-30', fileName: 'Y88_01020400.bin', critical: true,
    action: 'UpdateAtRestart', source: 'Override', releasedVersion: '01.01.19', rollBack: null, sizeBytes: 57_900_000,
    descriptionUrl: 'https://ftp.hp.com/pub/softpaq/sp160000-160500/sp160123.html', downloadFailure: null, downloading: false,
    ...overrides,
  };
}

function firmware(overrides: Record<string, unknown> = {}) {
  return {
    supported: true, newVersions: 'PreProductionFirst', preProductionLocationName: 'IT Lab',
    rows: [bios()],
    preProduction: [{ platformId: '8EF5', version: '01.02.04', releasedAt: '2026-07-30', releasedVersion: '01.01.19', since: '2026-08-02T02:00:00Z' }],
    ...overrides,
  };
}

function renderFirmware(): void {
  render(
    <MemoryRouter initialEntries={[`/drivers/models/${modelId}?view=firmware`]}>
      <Routes>
        <Route path="/drivers/models/:modelId" element={<DriverModelPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  vi.useRealTimers();
  auth.canManageDrivers = true;
});

describe('Drivers & Firmware: a check started on the model page', () => {
  it('shows the BIOS it found when the run ends, without a reload, and says what it found', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let started = false;
    let finished = false;
    const runId = '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
    const run = { runId, trigger: 'ModelCheck', modelId, modelName: modelPage.model.displayName, startedAt: '2026-11-22T10:00:00Z', finishedAt: null, startedBy: 'olle@contoso.com', vendors: [] };
    serve({
      [`GET /api/drivers/models/${modelId}`]: () => ({ body: { ...modelPage, checkBlockedReason: started && !finished ? 'Running' : null } }),
      [`GET /api/drivers/models/${modelId}/firmware`]: () => ({ body: finished ? firmware({ preProduction: [] }) : firmware({ rows: [], preProduction: [] }) }),
      [`POST /api/drivers/models/${modelId}/check`]: () => { started = true; return { status: 202, body: { runId } }; },
      [`GET /api/drivers/sync/runs/${runId}/progress`]: () => ({ body: { run: { ...run, result: finished ? 'Completed' : 'Running', counts: finished ? { New: 1 } : {} } } }),
    });
    render(
      <MemoryRouter initialEntries={[`/drivers/models/${modelId}?view=firmware`]}>
        <Routes><Route path="/drivers/models/:modelId" element={<DriverModelPage />} /></Routes>
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole('button', { name: /Check for updates/ }));
    await waitFor(() => expect(notify).toHaveBeenCalledWith({ status: 'success', title: `Checking ${modelPage.model.displayName} for updates.` }));
    expect(screen.queryByText('System BIOS')).toBeNull();

    finished = true;
    await act(async () => { await vi.advanceTimersByTimeAsync(CHECK_POLL_MS); });

    expect(await screen.findByText('System BIOS')).toBeTruthy();
    expect(notify).toHaveBeenCalledWith({ status: 'success', title: 'Check for updates finished.', description: `${modelPage.model.displayName}: 1 new` });
  });

  it('reads the Firmware tab again when a sync started elsewhere ends', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let finished = false;
    serve({
      [`GET /api/drivers/models/${modelId}`]: () => ({ body: { ...modelPage, checkBlockedReason: finished ? null : 'Running' } }),
      [`GET /api/drivers/models/${modelId}/firmware`]: () => ({ body: finished ? firmware({ preProduction: [] }) : firmware({ rows: [], preProduction: [] }) }),
    });
    render(
      <MemoryRouter initialEntries={[`/drivers/models/${modelId}?view=firmware`]}>
        <Routes><Route path="/drivers/models/:modelId" element={<DriverModelPage />} /></Routes>
      </MemoryRouter>,
    );
    await screen.findByRole('tab', { name: 'Firmware' });
    expect(screen.queryByText('System BIOS')).toBeNull();

    finished = true;
    await act(async () => { await vi.advanceTimersByTimeAsync(MODEL_POLL_MS); });

    expect(await screen.findByText('System BIOS')).toBeTruthy();
  });
});

describe('Drivers & Firmware: Firmware tab', () => {
  it('shows the pre-production banner and the BIOS row, and Release gives it to every location', async () => {
    const calls: string[] = [];
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: modelPage },
      [`GET /api/drivers/models/${modelId}/firmware`]: { body: firmware() },
      [`POST /api/drivers/models/${modelId}/firmware/8EF5/release`]: () => { calls.push('release'); return { body: firmware({ rows: [bios({ releasedVersion: '01.02.04' })], preProduction: [] }) }; },
    });
    renderFirmware();

    expect(await screen.findByText('BIOS 01.02.04 (2026-07-30)')).toBeTruthy();
    expect(screen.getByText('In pre-production at IT Lab. Other locations get 01.01.19.')).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Firmware' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByText('System BIOS')).toBeTruthy();
    expect(screen.getByText('Y88_01020400.bin')).toBeTruthy();
    expect(within(screen.getByText('System BIOS').closest('td') as HTMLElement).queryByText('Firmware')).toBeNull();
    expect((screen.getByRole('combobox', { name: 'Action for System BIOS' }) as HTMLElement).textContent).toContain('Update at restart');

    fireEvent.click(screen.getByRole('button', { name: 'Release' }));

    await waitFor(() => expect(calls).toEqual(['release']));
    await waitFor(() => expect(screen.queryByText('BIOS 01.02.04 (2026-07-30)')).toBeNull());
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ status: 'success', title: 'BIOS 01.02.04 released.' }));
  });

  it('the details show the mockup fields, the release notes link and the history without a Driver sets column', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: modelPage },
      [`GET /api/drivers/models/${modelId}/firmware`]: { body: firmware({ preProduction: [] }) },
      [`GET /api/drivers/models/${modelId}/firmware/8EF5/versions`]: { body: [
        { componentId: 'firmware-8EF5', version: '01.01.19', firstSet: 0, lastSet: null, from: '2026-06-06T02:00:00Z', to: null, change: 'Sync', changedBy: null, origin: 'Vendor', status: 'Stored', sizeBytes: 57_000_000 },
        { componentId: 'firmware-8EF5', version: '01.01.12', firstSet: 0, lastSet: null, from: '2026-03-20T10:14:00Z', to: '2026-06-06T02:00:00Z', change: 'Onboarding', changedBy: 'anna@contoso.com', origin: 'Vendor', status: 'DownloadsOnRollBack', sizeBytes: 0 },
      ] },
    });
    renderFirmware();

    fireEvent.click(await screen.findByRole('button', { name: 'Show details of System BIOS' }));
    for (const label of ['Platform ID', 'Criticality', 'Size', 'File', 'Released']) {
      expect(screen.getByText(label)).toBeTruthy();
    }
    expect(screen.getByText('Critical')).toBeTruthy();
    expect(screen.getByRole('link', { name: /Release notes/ }).getAttribute('href')).toBe('https://ftp.hp.com/pub/softpaq/sp160000-160500/sp160123.html');

    fireEvent.click(screen.getByRole('button', { name: /Version history/ }));
    const history = await screen.findByText('anna@contoso.com');
    const table = history.closest('table')!;
    expect(within(table).getAllByRole('columnheader').map(h => h.textContent)).toEqual(['Version', 'From', 'To', 'Change', 'By', 'Status']);
    expect(within(table).getByText('Onboard')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Roll back component' })).toBeTruthy();
  });

  it('the history follows a download of the current version, without closing it', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let downloaded = false;
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: modelPage },
      [`GET /api/drivers/models/${modelId}/firmware`]: () => ({ body: firmware({ rows: [bios({ releasedVersion: '01.02.04', downloading: !downloaded, sizeBytes: downloaded ? 60_742_407 : 0 })], preProduction: [] }) }),
      [`GET /api/drivers/models/${modelId}/firmware/8EF5/versions`]: () => ({ body: [
        { componentId: 'firmware-8EF5', version: '01.02.04', firstSet: 0, lastSet: null, from: '2026-11-22T14:30:00Z', to: null, change: 'Sync', changedBy: null, origin: 'Vendor', status: downloaded ? 'Stored' : 'Downloading', sizeBytes: downloaded ? 60_742_407 : 0 },
      ] }),
    });
    renderFirmware();

    fireEvent.click(await screen.findByRole('button', { name: 'Show details of System BIOS' }));
    fireEvent.click(screen.getByRole('button', { name: /Version history/ }));
    const table = (await screen.findByText('Automatic')).closest('table')!;
    expect(within(table).getByText('Downloading')).toBeTruthy();

    downloaded = true;
    await act(async () => { await vi.advanceTimersByTimeAsync(FIRMWARE_POLL_MS); });

    await waitFor(() => expect(within(table).getByText('Stored')).toBeTruthy());
    expect(screen.getByText('61 MB')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Version history/ }).getAttribute('aria-expanded')).toBe('true');
  });

  it('a downloading BIOS says so under its name with a moving edge, and a failed one says why in its icon', async () => {
    let failed = false;
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: modelPage },
      [`GET /api/drivers/models/${modelId}/firmware`]: () => ({ body: firmware({ rows: [bios({ releasedVersion: '01.02.04', sizeBytes: 0, downloading: !failed, downloadFailure: failed ? 'The file does not match its checksum.' : null })], preProduction: [] }) }),
    });
    renderFirmware();

    expect(await screen.findByText('Downloading from HP. Devices get it once it is verified.')).toBeTruthy();
    expect(screen.getByRole('progressbar', { name: 'Downloading System BIOS' })).toBeTruthy();
    expect(screen.queryByText('Y88_01020400.bin')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Show details of System BIOS' }));
    const size = screen.getByText('Size').nextElementSibling!;
    expect(size.textContent).toBe('Downloading');
    expect(size.querySelector('svg')).toBeNull();
    cleanup();

    failed = true;
    renderFirmware();

    expect(await screen.findByText('Download failed. The next sync tries again.')).toBeTruthy();
    expect(screen.getByLabelText('The file does not match its checksum.')).toBeTruthy();
    expect(screen.queryByRole('progressbar')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Show details of System BIOS' }));
    expect(screen.getByText('Size').nextElementSibling!.textContent).toBe('Download failed');
  });

  it('the current version of a skipped BIOS is not downloaded, not something to roll back to', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: modelPage },
      [`GET /api/drivers/models/${modelId}/firmware`]: { body: firmware({ rows: [bios({ action: 'Skip', source: 'Default', releasedVersion: '01.02.04', sizeBytes: 0 })], preProduction: [] }) },
      [`GET /api/drivers/models/${modelId}/firmware/8EF5/versions`]: { body: [
        { componentId: 'firmware-8EF5', version: '01.02.04', firstSet: 0, lastSet: null, from: '2026-11-22T14:30:00Z', to: null, change: 'Sync', changedBy: null, origin: 'Vendor', status: 'NotDownloaded', sizeBytes: 0 },
      ] },
    });
    renderFirmware();

    fireEvent.click(await screen.findByRole('button', { name: 'Show details of System BIOS' }));
    fireEvent.click(screen.getByRole('button', { name: /Version history/ }));
    const table = (await screen.findByText('Automatic')).closest('table')!;

    expect(within(table).getByText('Not downloaded')).toBeTruthy();
    expect(within(table).queryByText('Downloads on roll back')).toBeNull();
  });

  it('choosing Skip saves the action for the platform', async () => {
    const sent: unknown[] = [];
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: modelPage },
      [`GET /api/drivers/models/${modelId}/firmware`]: { body: firmware({ preProduction: [] }) },
      [`PUT /api/drivers/models/${modelId}/firmware/8EF5/action`]: init => { sent.push(JSON.parse(String(init?.body))); return { body: firmware({ rows: [bios({ action: 'Skip', source: 'Default' })], preProduction: [] }) }; },
    });
    renderFirmware();

    fireEvent.click(await screen.findByRole('combobox', { name: 'Action for System BIOS' }));
    fireEvent.click(screen.getByRole('option', { name: 'Skip' }));

    await waitFor(() => expect(sent).toEqual([{ action: 'Skip' }]));
    expect(await screen.findByText('Default')).toBeTruthy();
  });

  it('the More menu of the Firmware tab holds only Remove model', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: modelPage },
      [`GET /api/drivers/models/${modelId}/firmware`]: { body: firmware() },
    });
    renderFirmware();

    fireEvent.click(await screen.findByRole('button', { name: 'More actions' }));

    expect(screen.getAllByRole('menuitem').map(i => i.getAttribute('aria-label'))).toEqual(['Remove model']);
  });

  it('a Dell model says it gets no BIOS updates', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: { ...modelPage, model: { ...modelPage.model, vendor: 'Dell' } } },
      [`GET /api/drivers/models/${modelId}/firmware`]: { body: { supported: false, newVersions: 'AvailableImmediately', preProductionLocationName: null, rows: [], preProduction: [] } },
    });
    renderFirmware();

    expect(await screen.findByText('No BIOS updates for this model')).toBeTruthy();
    expect(screen.getByText('Cloud Imaging updates the BIOS of HP x64 models.')).toBeTruthy();
  });

  it('read-only users see the action as text and no Release', async () => {
    auth.canManageDrivers = false;
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: modelPage },
      [`GET /api/drivers/models/${modelId}/firmware`]: { body: firmware() },
    });
    renderFirmware();

    expect(await screen.findByText('Update at restart')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Release' })).toBeNull();
    expect(screen.queryByRole('combobox', { name: 'Action for System BIOS' })).toBeNull();
  });
});
