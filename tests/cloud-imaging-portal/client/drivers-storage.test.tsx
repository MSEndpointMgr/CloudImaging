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

import DriversPage from '../../../src/cloud-imaging-portal/client/src/pages/DriversPage.tsx';
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

const storage = {
  totalBytes: 3_100_000_000,
  vendors: [{ vendor: 'Hp', bytes: 2_400_000_000 }, { vendor: 'Dell', bytes: 700_000_000 }],
  models: [{ modelId, displayName: 'HP EliteBook 840 G11', vendor: 'Hp', releases: ['Windows11_24H2', 'Windows11_25H2'], sizeBytes: 2_100_000_000, sets: { current: 2, rollBack: 2 } }],
  previousVersionsToKeep: 1,
  previousVersionsBytes: 1_400_000_000,
};

const storageModel = {
  modelId,
  releases: [{
    release: 'Windows11_24H2',
    current: { version: 7, builtAt: '2026-10-22T02:41:00' },
    rollBack: { version: 6, builtAt: '2026-10-08T02:37:00' },
    componentCount: 33,
    bytes: 2_300_000_000,
    components: [
      { lineageKey: 'sp100', componentId: 'sp200', name: 'Intel Wireless LAN Driver', version: '23.80', sizeBytes: 113_000_000, sets: ['7', '6'], sharedWithModels: 2, status: 'Stored', reason: null, downloadedAt: '2026-10-20T02:10:00' },
      { lineageKey: 'sp100', componentId: 'sp150', name: 'Intel Wireless LAN Driver', version: '23.70', sizeBytes: 110_000_000, sets: ['previous'], sharedWithModels: 0, status: 'Stored', reason: null, downloadedAt: '2026-09-20T02:10:00' },
      { lineageKey: 'sp101', componentId: 'sp201', name: 'Realtek Audio Driver', version: '6.0.9', sizeBytes: 10_000_000, sets: ['next'], sharedWithModels: 0, status: 'Failed', reason: 'The file does not match its checksum.', downloadedAt: null },
    ],
  }],
};

function renderStorage(): void {
  render(
    <MemoryRouter initialEntries={['/drivers?tab=storage']}>
      <Routes><Route path="/drivers" element={<DriversPage />} /></Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  auth.canManageDrivers = true;
});

describe('Drivers: Storage tab', () => {
  it('shows the tiles with the shared-files hint, and models with releases, driver sets and size', async () => {
    serve({ 'GET /api/drivers/storage': { body: storage } });

    renderStorage();

    expect(await screen.findByText('3.10 GB')).toBeTruthy();
    expect(screen.getByLabelText('Components shared by several models are stored once, so the total is less than the sum of the models.')).toBeTruthy();
    expect(screen.getAllByText('HP').length).toBe(2);
    expect(screen.getByText('700 MB')).toBeTruthy();
    expect(screen.getAllByRole('columnheader').map(h => h.textContent)).toEqual(['Details', 'Model', 'Vendor', 'Windows releases', 'Driver sets', 'Size']);
    expect(screen.getByText('2 current, 2 roll-back')).toBeTruthy();
    expect(screen.getByText('2.10 GB')).toBeTruthy();
    expect(screen.queryByText(/Freed if removed/)).toBeNull();
  });

  it('shows the Previous versions row with what it uses now, and saves a new number with a toast', async () => {
    const saved: unknown[] = [];
    serve({
      'GET /api/drivers/storage': { body: storage },
      'PUT /api/drivers/storage/settings': init => { saved.push(JSON.parse(String(init?.body))); return { body: { ...storage, previousVersionsToKeep: 2 } }; },
    });
    renderStorage();

    expect(await screen.findByText(/Earlier versions of each component kept in storage, so rolling back to one is instant and does not depend on the vendor\..*Uses 1.40 GB now\./)).toBeTruthy();
    const select = screen.getByRole('combobox', { name: 'Previous versions' });
    expect(select.textContent).toContain('Keep 1 previous version');
    fireEvent.click(select);
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['Keep none', 'Keep 1 previous version', 'Keep 2 previous versions', 'Keep 3 previous versions']);
    fireEvent.click(screen.getByRole('option', { name: 'Keep 2 previous versions' }));

    await waitFor(() => expect(saved).toEqual([{ previousVersionsToKeep: 2 }]));
    await waitFor(() => expect(notify).toHaveBeenCalledWith(expect.objectContaining({ status: 'success' })));
    expect(screen.getByRole('combobox', { name: 'Previous versions' }).textContent).toContain('Keep 2 previous versions');
  });

  it('disables the setting for read-only users', async () => {
    auth.canManageDrivers = false;
    serve({ 'GET /api/drivers/storage': { body: storage } });

    renderStorage();

    expect((await screen.findByRole('combobox', { name: 'Previous versions' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('expands a model into a block per release with both sets, then components with sets, sharing, status and download time', async () => {
    serve({ 'GET /api/drivers/storage': { body: storage }, [`GET /api/drivers/storage/models/${modelId}`]: { body: storageModel } });
    renderStorage();

    fireEvent.click(await screen.findByRole('button', { name: 'Show stored components of HP EliteBook 840 G11' }));

    expect(await screen.findByRole('heading', { name: 'Windows 11, version 24H2' })).toBeTruthy();
    expect(screen.getByText('Set 7')).toBeTruthy();
    expect(screen.getByText('Current, built 2026-10-22 02:41')).toBeTruthy();
    expect(screen.getByText('Roll-back, built 2026-10-08 02:37')).toBeTruthy();
    expect(screen.getByText('33 components, 2.30 GB')).toBeTruthy();
    expect(screen.getByText('Previous version')).toBeTruthy();
    expect(screen.getByText('Next set')).toBeTruthy();
    expect(screen.getByText('3 models')).toBeTruthy();
    expect(screen.getAllByText('This model only').length).toBe(2);
    expect(screen.getAllByText('Stored').length).toBe(2);
    expect(screen.getByText('Failed')).toBeTruthy();
    expect(screen.getByLabelText('The file does not match its checksum.')).toBeTruthy();
    expect(screen.getByText('Not yet')).toBeTruthy();
    expect(screen.getByText('2026-10-20 02:10')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Hide stored components of HP EliteBook 840 G11' }).getAttribute('aria-expanded')).toBe('true');
  });

  it('counts and shows a set in testing', async () => {
    const withTesting = { ...storage, models: [{ ...storage.models[0], sets: { current: 2, rollBack: 2, testing: 1 } }] };
    const modelWithTesting = { ...storageModel, releases: [{ ...storageModel.releases[0], testing: { version: 8, builtAt: '2026-10-28T02:15:00' } }] };
    serve({ 'GET /api/drivers/storage': { body: withTesting }, [`GET /api/drivers/storage/models/${modelId}`]: { body: modelWithTesting } });
    renderStorage();

    expect(await screen.findByText('2 current, 2 roll-back, 1 in testing')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Show stored components of HP EliteBook 840 G11' }));

    expect(await screen.findByText('In testing, built 2026-10-28 02:15')).toBeTruthy();
    expect(screen.getByText('Set 8')).toBeTruthy();
  });
});

function row(overrides: Record<string, unknown> = {}) {
  return {
    modelId, vendor: 'Hp', displayName: 'HP EliteBook 840 G11', matchKeys: ['8C26'], architecture: 'X64', releases: ['Windows11_24H2'],
    state: 'Active', included: 31, skipped: 9, maxDownloadBytes: 2_410_000_000,
    status: { waitingForApproval: 0, failedItems: 0, syncOff: false, downloading: null },
    ...overrides,
  };
}

const withdrawn = { lineageKey: 'sp101', componentId: 'sp201', name: 'Realtek Audio Driver', category: 'Driver - Audio', version: '6.0.9', lastUpdated: '2026-10-01', action: 'Offline', allowedActions: ['Offline', 'Skip'], source: 'Default', ruleName: null, state: 'Withdrawn', updatedByLastSync: false, details: [], descriptionUrl: null, sizeBytes: 10 };

function page(overrides: Record<string, unknown> = {}) {
  return {
    model: row(), release: 'Windows11_24H2', checkAvailableAt: null, checkBlockedReason: null, setVersion: 7, builtAt: '2026-10-25T02:41:00Z',
    hasPreviousSet: true, previousSetVersion: 6, pendingVersion: null, pending: null, preparing: null,
    tiles: { offline: 2, offlineAndSpecialize: 0, specialize: 0, skip: 1, downloadPerDeviceBytes: 113_000_000 },
    components: [withdrawn],
    ...overrides,
  };
}

const impact = { freedBytes: 2_100_000_000, sets: 2, overrides: 3, runningSessions: 0 };

function renderModel(path = `/drivers/models/${modelId}`): void {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/drivers" element={<p>Drivers list</p>} />
        <Route path="/drivers/models/:modelId" element={<DriverModelPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('Drivers: model page roll back and removal', () => {
  it('rolls back the driver set through an overlay that names both sets, and returns focus on cancel', async () => {
    const calls: string[] = [];
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`POST /api/drivers/models/${modelId}/releases/Windows11_24H2/rollback`]: () => { calls.push('rollback'); return { body: page({ setVersion: 6, previousSetVersion: 7 }) }; },
    });
    renderModel();

    const button = await screen.findByRole('button', { name: 'Roll back' });
    fireEvent.focus(button.parentElement!);
    expect(await screen.findByText('Devices get driver set 6 instead of 7 from their next deployment')).toBeTruthy();
    fireEvent.blur(button.parentElement!);
    button.focus();
    fireEvent.click(button);
    const dialog = screen.getByRole('dialog', { name: 'Roll back to driver set 6?' });
    expect(within(dialog).getByText('Devices imaged from now on get driver set 6. Running deployments keep the set they started with. The next sync does not reinstate set 7.')).toBeTruthy();
    expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Cancel' }));
    fireEvent.keyDown(document, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Roll back' }));
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(button);

    fireEvent.click(button);
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Roll back' }));

    await waitFor(() => expect(calls).toEqual(['rollback']));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ status: 'success', title: 'Rolled back to driver set 6.' }));
  });

  it('disables Roll back driver set without a previous set', async () => {
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page({ hasPreviousSet: false, previousSetVersion: null }) } });
    renderModel();

    const button = await screen.findByRole('button', { name: 'Roll back' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    fireEvent.focus(button.parentElement!);
    expect(await screen.findByText('No previous driver set to roll back to')).toBeTruthy();
  });

  it('removes a model after showing what is deleted, freed and kept', async () => {
    const calls: string[] = [];
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`GET /api/drivers/models/${modelId}/removal-impact`]: { body: impact },
      [`DELETE /api/drivers/models/${modelId}`]: () => { calls.push('remove'); return { status: 204 }; },
    });
    renderModel();

    fireEvent.click(await screen.findByRole('button', { name: 'Remove' }));
    const dialog = screen.getByRole('dialog', { name: 'Remove HP EliteBook 840 G11?' });
    expect(within(dialog).getByText('Devices of this model imaged from now on get no drivers from Cloud Imaging. Running deployments keep their driver set.')).toBeTruthy();
    expect(await within(dialog).findByText('Deleted: 2 driver sets, 3 overrides')).toBeTruthy();
    expect(within(dialog).getByText('Freed: 2.10 GB at the next clean-up')).toBeTruthy();
    expect(within(dialog).getByText('Kept: rules and sync history')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));

    expect(await screen.findByText('Drivers list')).toBeTruthy();
    expect(calls).toEqual(['remove']);
  });

  it('the Windows releases dialog lists only releases the vendor publishes, adds and removes them on Save, and keeps the last one', async () => {
    const calls: string[] = [];
    const twoReleases = page({ model: row({ releases: ['Windows11_24H2', 'Windows11_25H2'] }) });
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: twoReleases },
      [`GET /api/drivers/models/${modelId}?release=Windows11_24H2`]: { body: twoReleases },
      [`GET /api/drivers/models/${modelId}?release=Windows11_25H2`]: { body: page({ release: 'Windows11_25H2', model: row({ releases: ['Windows11_24H2', 'Windows11_25H2'] }) }) },
      [`GET /api/drivers/models/${modelId}?release=Windows11_23H2`]: { body: page({ release: 'Windows11_23H2', model: row({ releases: ['Windows11_23H2', 'Windows11_24H2'] }) }) },
      'GET /api/drivers/catalog/models?vendor=Hp': { body: { fetchedAt: '2026-10-29T08:00:00Z', catalogUrl: 'https://hp', stale: false, warning: null, models: [
        { catalogModelKey: '8C26_X64', vendor: 'Hp', displayName: 'HP EliteBook 840 G11', matchKeys: ['8C26'], architecture: 'X64', releases: ['Windows11_25H2', 'Windows11_24H2', 'Windows11_23H2'], onboardedModelId: modelId },
      ] } },
      'POST /api/drivers/models/preview': { body: { models: [{ catalogModelKey: '8C26_X64', displayName: 'HP EliteBook 840 G11', offline: 10, offlineAndSpecialize: 2, specialize: 0, skip: 5, sizeBytes: 2_380_000_000 }], errors: [] } },
      [`GET /api/drivers/models/${modelId}/removal-impact?release=Windows11_25H2`]: { body: { ...impact, sets: 1, overrides: 0 } },
      [`POST /api/drivers/models/${modelId}/releases`]: init => { calls.push(`add ${(JSON.parse(String(init?.body)) as { release: string }).release}`); return { body: twoReleases }; },
      [`DELETE /api/drivers/models/${modelId}/releases/Windows11_25H2`]: () => { calls.push('remove Windows11_25H2'); return { status: 204 }; },
    });
    renderModel();

    fireEvent.click(await screen.findByRole('combobox', { name: 'Windows release' }));
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['Windows 11, version 24H2', 'Windows 11, version 25H2']);
    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });

    fireEvent.click(screen.getByRole('button', { name: 'Windows releases' }));
    const dialog = await screen.findByRole('dialog', { name: 'Windows releases' });
    const list = await within(dialog).findByRole('list', { name: 'Windows releases' });
    expect(within(list).getAllByRole('checkbox').length).toBe(3);
    expect(within(dialog).queryByText('Windows 10, version 22H2')).toBeNull();
    expect(await within(dialog).findByText('17 components, 12 included after your rules')).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(within(dialog).getByRole('checkbox', { name: 'Windows 11, version 23H2' }));
    fireEvent.click(within(dialog).getByRole('checkbox', { name: 'Windows 11, version 25H2' }));
    expect(await within(dialog).findByText('Deletes 1 driver set and 0 overrides. Devices installing it get no drivers.')).toBeTruthy();
    expect(within(dialog).getByText('Adds')).toBeTruthy();
    expect(within(dialog).getByText('Removes')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(calls).toEqual(['add Windows11_23H2', 'remove Windows11_25H2']));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(notify).toHaveBeenCalledWith({ status: 'success', title: 'Windows releases of HP EliteBook 840 G11 saved.' });
  });

  it('the Windows releases dialog keeps the last release ticked', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`GET /api/drivers/models/${modelId}?release=Windows11_24H2`]: { body: page() },
      'GET /api/drivers/catalog/models?vendor=Hp': { body: { fetchedAt: null, catalogUrl: 'https://hp', stale: false, warning: null, models: [
        { catalogModelKey: '8C26_X64', vendor: 'Hp', displayName: 'HP EliteBook 840 G11', matchKeys: ['8C26'], architecture: 'X64', releases: ['Windows11_24H2'], onboardedModelId: modelId },
      ] } },
    });
    renderModel();

    fireEvent.click(await screen.findByRole('button', { name: 'Windows releases' }));
    const dialog = await screen.findByRole('dialog', { name: 'Windows releases' });
    const only = await within(dialog).findByRole('checkbox', { name: 'Windows 11, version 24H2' }) as HTMLButtonElement;
    expect(only.disabled).toBe(true);
    expect(within(dialog).getByText('The last release stays. Use Remove to remove the model.')).toBeTruthy();
  });

  it('removing a model removes the whole model', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page({ model: row({ releases: ['Windows11_24H2', 'Windows11_25H2'] }) }) },
      [`GET /api/drivers/models/${modelId}/removal-impact`]: { body: impact },
    });
    renderModel();

    fireEvent.click(await screen.findByRole('button', { name: 'Remove' }));
    expect(await screen.findByRole('dialog', { name: 'Remove HP EliteBook 840 G11?' })).toBeTruthy();
    expect(screen.queryByRole('group', { name: 'What to remove' })).toBeNull();
  });

  it('shows the Withdrawn badge in text and removes a withdrawn component from its details', async () => {
    const calls: string[] = [];
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`DELETE /api/drivers/models/${modelId}/releases/Windows11_24H2/components/sp101`]: () => { calls.push('remove'); return { status: 204 }; },
    });
    renderModel();

    const badge = await screen.findByText('Withdrawn');
    expect(badge.getAttribute('title')).toBe('The vendor no longer publishes this component.');
    fireEvent.click(screen.getByRole('button', { name: 'Show details of Realtek Audio Driver' }));
    fireEvent.click(screen.getByRole('button', { name: 'Remove Realtek Audio Driver' }));
    const dialog = screen.getByRole('dialog', { name: 'Remove Realtek Audio Driver?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(calls).toEqual(['remove']));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('hides roll back, removal and release controls from read-only users', async () => {
    auth.canManageDrivers = false;
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page() } });
    renderModel();

    await screen.findByText('Withdrawn');
    expect(screen.queryByRole('button', { name: 'Roll back' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove' })).toBeNull();
    fireEvent.click(screen.getByRole('combobox', { name: 'Windows release' }));
    expect(screen.queryByRole('group', { name: 'Add to this model' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove release' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Show details of Realtek Audio Driver' }));
    expect(screen.queryByRole('button', { name: 'Remove Realtek Audio Driver' })).toBeNull();
  });
});
