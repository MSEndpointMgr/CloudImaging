import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

const { apiFetch, notify, auth, uploadBlocks } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  notify: vi.fn(() => 'toast'),
  auth: { canManageDrivers: true, canViewDrivers: true },
  uploadBlocks: vi.fn(),
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
vi.mock('../../../src/cloud-imaging-portal/client/src/services/chunkedUploadService.ts', () => ({ uploadBlocks }));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/authContext.tsx', () => ({ useAuth: () => auth }));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/toastContext.tsx', () => ({ useToast: () => ({ notify, update: vi.fn(), dismiss: vi.fn() }) }));

import DriverModelPage from '../../../src/cloud-imaging-portal/client/src/pages/DriverModelPage.tsx';

type Handler = (init?: RequestInit) => { status?: number; body?: unknown };

const sent: { key: string; body: unknown }[] = [];

function serve(routes: Record<string, Handler | { status?: number; body?: unknown }>): void {
  apiFetch.mockImplementation(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${url}`;
    sent.push({ key, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const route = routes[key];
    if (!route) return new Response(JSON.stringify({ detail: `unexpected ${key}` }), { status: 500 });
    const { status = 200, body } = typeof route === 'function' ? route(init) : route;
    return new Response(body === undefined ? null : JSON.stringify(body), { status });
  });
}

const modelId = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';
const otherId = '9a0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';
const customId = '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
const uploadId = '2c9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';

function row(id = modelId, name = 'HP EliteBook 840 G11') {
  return {
    modelId: id, vendor: 'Hp', displayName: name, matchKeys: ['8C26'], architecture: 'X64', releases: ['Windows11_24H2'], state: 'Active',
    included: 2, skipped: 0, maxDownloadBytes: 1, status: { waitingForApproval: 0, failedItems: 0, syncOff: false, downloading: null },
  };
}

function component(lineageKey: string, name: string, overrides: Record<string, unknown> = {}) {
  return {
    lineageKey, componentId: lineageKey, name, category: 'Driver - Network', version: '1.0', lastUpdated: '2026-10-01', action: 'Offline',
    allowedActions: ['Offline', 'Skip'], source: 'Default', ruleName: null, state: 'Active', updatedByLastSync: false, details: [], descriptionUrl: null, sizeBytes: 10,
    ...overrides,
  };
}

function page(components = [component('sp100', 'Intel Wireless LAN Driver'), component('sp101', 'Realtek Audio Driver')], notices: unknown[] = [], model = row()) {
  return {
    model, release: 'Windows11_24H2', checkAvailableAt: null, checkBlockedReason: null, setVersion: 7, builtAt: null, hasPreviousSet: false, previousSetVersion: null,
    pendingVersion: null, pending: null, preparing: null, notices,
    tiles: { offline: 2, offlineAndSpecialize: 0, specialize: 0, skip: 0, downloadPerDeviceBytes: 20 },
    components,
  };
}

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={[`/drivers/models/${modelId}`]}>
      <Routes><Route path="/drivers/models/:modelId" element={<DriverModelPage />} /></Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  sent.length = 0;
  auth.canManageDrivers = true;
});

describe('Drivers: custom components', () => {
  it('uploads a package, shows the INF count, lists the shared vendor components and shows field errors from Core', async () => {
    uploadBlocks.mockImplementation(async (_session: unknown, _file: File, onProgress: (p: number) => void) => { onProgress(50); return ['MDAwMDAw']; });
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`GET /api/drivers/models/${otherId}`]: { body: page([component('sp100', 'Intel Wireless LAN Driver')], [], row(otherId, 'HP EliteBook 860 G11')) },
      'GET /api/drivers/models': { body: [row(), row(otherId, 'HP EliteBook 860 G11')] },
      'POST /api/drivers/custom-components/uploads/start': { body: { uploadId, uploadUrl: 'https://storage.test/x?sig=1', blockSize: 8 } },
      [`POST /api/drivers/custom-components/uploads/${uploadId}/complete`]: { body: { sha256: 'ab', sizeBytes: 12_000_000, infCount: 14 } },
      'POST /api/drivers/custom-components': { status: 400, body: { detail: 'Enter the silent install command.', errors: [{ field: 'installCommand', detail: 'Enter the silent install command.' }] } },
    });
    renderPage();

    const opener = await screen.findByRole('button', { name: 'Add custom component' });
    opener.focus();
    fireEvent.click(opener);
    const dialog = await screen.findByRole('dialog', { name: 'Add custom component' });
    expect(within(dialog).getByText('Use a driver the vendor has not published yet. It is checked and stored like a vendor component.')).toBeTruthy();
    expect(within(dialog).getByText("For Offline, a ZIP with the driver's INF files. For Specialize, an installer. Drivers must be signed; unsigned drivers fail on the device.")).toBeTruthy();
    expect(within(dialog).getByText('All Windows releases of each model. Offline drivers only install on hardware they match.')).toBeTruthy();
    expect(within(dialog).getByText('The replaced vendor component is skipped on these models until you remove this one.')).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Add component' }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(within(dialog).getByLabelText('Package'), { target: { files: [new File(['zip'], 'intel.zip')] } });
    expect(await within(dialog).findByText('intel.zip, 12 MB, 14 INF files found')).toBeTruthy();
    expect(sent.find(c => c.key === 'POST /api/drivers/custom-components/uploads/start')?.body).toEqual({ fileName: 'intel.zip', sizeBytes: 3 });
    expect(sent.find(c => c.key === `POST /api/drivers/custom-components/uploads/${uploadId}/complete`)?.body).toEqual({ blockIds: ['MDAwMDAw'] });

    fireEvent.click(within(dialog).getByRole('checkbox', { name: 'HP EliteBook 860 G11' }));
    fireEvent.click(within(dialog).getByRole('combobox', { name: 'Replaces' }));
    await waitFor(() => expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['Nothing, add alongside', 'Intel Wireless LAN Driver']));
    fireEvent.click(screen.getByRole('option', { name: 'Intel Wireless LAN Driver' }));
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Intel (pre-release)' } });
    fireEvent.change(within(dialog).getByLabelText('Version'), { target: { value: '24.0' } });
    fireEvent.change(within(dialog).getByLabelText('Category'), { target: { value: 'Driver - Network' } });
    fireEvent.click(within(dialog).getByRole('combobox', { name: 'Action' }));
    fireEvent.click(screen.getByRole('option', { name: 'Specialize' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add component' }));

    expect(await within(dialog).findByText('Enter the silent install command.')).toBeTruthy();
    expect(sent.find(c => c.key === 'POST /api/drivers/custom-components')?.body).toEqual({
      uploadId, name: 'Intel (pre-release)', version: '24.0', action: 'Specialize', category: 'Driver - Network', installCommand: null,
      targets: [{ modelId, replacesLineageKey: 'sp100' }, { modelId: otherId, replacesLineageKey: 'sp100' }],
    });
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  it('shows Custom with its tooltip, Replaced by custom locked at Skip, and removes a custom component through the overlay', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: {
        body: page([
          component('sp100', 'Intel Wireless LAN Driver', { action: 'Skip', source: 'ReplacedByCustom', replacedBy: { customId, name: 'Intel (pre-release)' } }),
          component(`custom-${customId}`, 'Intel (pre-release)', { source: 'Custom', allowedActions: ['Offline'], custom: { customId, replacesName: 'Intel Wireless LAN Driver', uploadedBy: 'olle@contoso.com' } }),
        ]),
      },
      'GET /api/drivers/custom-components': { body: [{ customId, name: 'Intel (pre-release)', targets: [{ modelId, modelName: 'HP EliteBook 840 G11', replacesLineageKey: 'sp100', replacesName: 'Intel Wireless LAN Driver' }] }] },
      [`DELETE /api/drivers/custom-components/${customId}`]: { status: 204 },
    });
    renderPage();

    expect(await screen.findByText('Replaced by custom')).toBeTruthy();
    expect((screen.getByRole('combobox', { name: 'Action for Intel Wireless LAN Driver' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByRole('combobox', { name: 'Action for Intel Wireless LAN Driver' }).textContent).toContain('Skip');
    expect(screen.getByLabelText('Replaces Intel Wireless LAN Driver on this model until you remove it.')).toBeTruthy();
    expect(screen.getByText('Uploaded by olle@contoso.com, Driver - Network')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Show details of Intel (pre-release)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Remove custom component Intel (pre-release)' }));
    const dialog = screen.getByRole('dialog', { name: 'Remove Intel (pre-release)?' });
    expect(await within(dialog).findByText('HP EliteBook 840 G11 returns to Intel Wireless LAN Driver from HP')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(sent.some(c => c.key === `DELETE /api/drivers/custom-components/${customId}`)).toBe(true));
  });

  it('shows the notice banner naming the vendor, and Dismiss calls the dismiss route', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page(undefined, [{ customId, name: 'Intel (pre-release)', vendorComponentName: 'Intel Wireless LAN Driver', componentId: 'sp150123', version: '24.1', releasedAt: '2026-10-24' }]) },
      [`POST /api/drivers/custom-components/${customId}/notices/${modelId}/dismiss`]: { status: 204 },
    });
    renderPage();

    expect(await screen.findByText('HP has published a new Intel Wireless LAN Driver since the custom component was added.')).toBeTruthy();
    expect(screen.getByText("sp150123, version 24.1, released 2026-10-24. Test it, then remove the custom component to use HP's driver.")).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));

    await waitFor(() => expect(sent.some(c => c.key === `POST /api/drivers/custom-components/${customId}/notices/${modelId}/dismiss`)).toBe(true));
    fireEvent.click(screen.getByRole('button', { name: 'Remove custom component' }));
    expect(screen.getByRole('dialog', { name: 'Remove Intel (pre-release)?' })).toBeTruthy();
  });
});
