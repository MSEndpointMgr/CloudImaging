import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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

import DriverModelPage from '../../../src/cloud-imaging-portal/client/src/pages/DriverModelPage.tsx';

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
  auth.canManageDrivers = true;
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
