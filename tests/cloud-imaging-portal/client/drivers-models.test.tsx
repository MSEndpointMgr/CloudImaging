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
const vendors = [
  { vendor: 'Hp', supported: true, syncEnabled: false, onboardedModels: 1, matchKeyType: 'system ID' },
  { vendor: 'Dell', supported: false, syncEnabled: false, onboardedModels: 0, matchKeyType: null },
];

function row(overrides: Record<string, unknown> = {}) {
  return {
    modelId, vendor: 'Hp', displayName: 'HP EliteBook 840 G11', matchKeys: ['8C26'], architecture: 'X64', releases: ['Windows11_24H2'],
    state: 'Active', included: 31, skipped: 9, maxDownloadBytes: 2_410_000_000,
    status: { waitingForApproval: 0, failedItems: 0, syncOff: false, downloading: null },
    ...overrides,
  };
}

function page(overrides: Record<string, unknown> = {}) {
  return {
    model: row(), release: 'Windows11_24H2', checkAvailableAt: null, checkBlockedReason: null, setVersion: 7, builtAt: '2026-10-25T02:41:00Z',
    hasPreviousSet: true, pendingVersion: null, pending: null, preparing: null,
    tiles: { offline: 2, offlineAndSpecialize: 0, specialize: 0, skip: 1, downloadPerDeviceBytes: 113_000_000 },
    components: [
      { lineageKey: 'sp100', componentId: 'sp200', name: 'Intel Wireless LAN Driver', category: 'Driver - Network', version: '23.80.0.4', lastUpdated: '2026-10-20', action: 'Offline', allowedActions: ['Offline', 'Skip'], source: 'Default', ruleName: null, state: 'Active', updatedByLastSync: true, details: [{ label: 'Offline injection', value: 'Yes' }, { label: 'Released by HP', value: '2026-10-20' }], descriptionUrl: 'https://ftp.hp.com/pub/softpaq/sp200.html', sizeBytes: 113_000_000 },
      { lineageKey: 'sp101', componentId: 'sp201', name: 'Realtek Audio Driver', category: 'Driver - Audio', version: '6.0.9', lastUpdated: '2026-10-01', action: 'Offline', allowedActions: ['Offline', 'Skip'], source: 'Default', ruleName: null, state: 'WaitingForApproval', updatedByLastSync: false, details: [], descriptionUrl: null, sizeBytes: 10 },
      { lineageKey: 'sp102', componentId: 'sp202', name: 'HP Services Scan', category: 'Software - Enabling', version: '1.0', lastUpdated: null, action: 'Skip', allowedActions: ['Skip'], source: 'Locked', ruleName: null, state: 'Active', updatedByLastSync: false, details: [], descriptionUrl: null, sizeBytes: 5 },
    ],
    ...overrides,
  };
}

function renderAt(path: string): void {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/drivers" element={<DriversPage />} />
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

describe('Drivers: Models tab', () => {
  it('lists models with name and match keys, vendor, releases, counts, size and status icon labels', async () => {
    serve({
      'GET /api/drivers/models': { body: [row(), row({ modelId: 'b', displayName: 'HP ProBook 450 G10', state: 'Draft' }), row({ modelId: 'c', displayName: 'HP ZBook', status: { waitingForApproval: 0, failedItems: 0, syncOff: false, downloading: { done: 12, total: 33 } } }), row({ modelId: 'd', displayName: 'HP Elite Mini', status: { waitingForApproval: 0, failedItems: 1, failureReason: "Intel Graphics: The file's SHA-256 does not match the vendor's catalog.", syncOff: false, downloading: null } })] },
      'GET /api/drivers/vendors': { body: vendors },
    });

    renderAt('/drivers?tab=models');

    await screen.findByText('HP EliteBook 840 G11');
    const headers = screen.getAllByRole('columnheader').map(h => h.textContent);
    expect(headers.slice(0, 7)).toEqual(['Model', 'Vendor', 'Windows releases', 'Included', 'Skipped', 'Size', 'Status']);
    const status = screen.getAllByRole('columnheader')[6];
    expect(status.className).toContain('w-[25%]');
    expect(screen.getByText('Drivers installed during deployment, per device model and Windows release.')).toBeTruthy();
    expect(screen.getAllByText('8C26').length).toBeGreaterThan(0);
    expect(screen.getByText('Draft')).toBeTruthy();
    expect(screen.getByText('Downloading 12 of 33')).toBeTruthy();
    expect(screen.getByText('1 failed')).toBeTruthy();
    expect(screen.getByLabelText("Intel Graphics: The file's SHA-256 does not match the vendor's catalog.")).toBeTruthy();
    expect(screen.getAllByText('2.41 GB').length).toBe(4);
    expect(screen.getAllByLabelText('The most one device downloads. Devices without some hardware can download less.').length).toBeGreaterThan(0);
  });

  it('searches models by name', async () => {
    serve({ 'GET /api/drivers/models': { body: [row(), row({ modelId: 'b', displayName: 'HP ProBook 450 G10', matchKeys: ['8B41'] })] }, 'GET /api/drivers/vendors': { body: vendors } });
    renderAt('/drivers?tab=models');
    await screen.findByText('HP ProBook 450 G10');

    fireEvent.change(screen.getByRole('searchbox', { name: 'Search models' }), { target: { value: '450' } });

    expect(screen.queryByText('HP EliteBook 840 G11')).toBeNull();
    expect(screen.getByText('HP ProBook 450 G10')).toBeTruthy();
  });

  it('shows the empty state with Onboard model, and no write controls for read-only users', async () => {
    serve({ 'GET /api/drivers/models': { body: [] }, 'GET /api/drivers/vendors': { body: vendors } });
    renderAt('/drivers?tab=models');

    expect(await screen.findByText('No models onboarded yet')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'Onboard model' }).length).toBe(2);
    cleanup();

    auth.canManageDrivers = false;
    renderAt('/drivers?tab=models');
    await screen.findByText('No models onboarded yet');
    expect(screen.queryByRole('button', { name: 'Onboard model' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Check for updates/ })).toBeNull();
  });
});

describe('Drivers: Onboard model dialog', () => {
  const list = { fetchedAt: '2026-10-25T14:02:00Z', catalogUrl: 'https://hpia.hpcloud.hp.com/ref/platformList.cab', stale: false, warning: null, models: [
    { catalogModelKey: '8C26_X64', vendor: 'Hp', displayName: 'HP EliteBook 840 G11', matchKeys: ['8C26'], architecture: 'X64', releases: ['Windows11_24H2', 'Windows11_23H2'], onboardedModelId: null },
    { catalogModelKey: '8C27_X64', vendor: 'Hp', displayName: 'HP EliteBook 860 G11', matchKeys: ['8C27'], architecture: 'X64', releases: ['Windows11_24H2'], onboardedModelId: 'x' },
  ] };

  async function open(catalog: { status?: number; body?: unknown }, extra: Record<string, Handler | { status?: number; body?: unknown }> = {}): Promise<void> {
    serve({ 'GET /api/drivers/models': { body: [row()] }, 'GET /api/drivers/vendors': { body: vendors }, 'GET /api/drivers/catalog/models?vendor=Hp': catalog, ...extra });
    renderAt('/drivers?tab=models');
    fireEvent.click(await screen.findByRole('button', { name: 'Onboard model' }));
  }

  it('lists the vendor models, marks onboarded ones, and shows the list date and the Sync is off line', async () => {
    await open({ body: list });
    const dialog = await screen.findByRole('dialog', { name: 'Onboard model' });

    await within(dialog).findByRole('checkbox', { name: 'HP EliteBook 840 G11' });
    expect((within(dialog).getByRole('checkbox', { name: 'HP EliteBook 860 G11' }) as HTMLButtonElement).disabled).toBe(true);
    expect(within(dialog).getByText('Onboarded')).toBeTruthy();
    expect(within(dialog).getByText(/0 selected\. 2 of 2 HP models, list from 2026-10-25/)).toBeTruthy();
    expect(within(dialog).getByText('Sync is off for HP. Completing onboarding turns it on, so these models get updates.')).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Continue' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('disables the search while the model list is being fetched', async () => {
    serve({ 'GET /api/drivers/models': { body: [row()] }, 'GET /api/drivers/vendors': { body: vendors } });
    const base = apiFetch.getMockImplementation()!;
    apiFetch.mockImplementation((url: string, init?: RequestInit) => (url.startsWith('/api/drivers/catalog') ? new Promise<Response>(() => {}) : base(url, init)));
    renderAt('/drivers?tab=models');
    fireEvent.click(await screen.findByRole('button', { name: 'Onboard model' }));
    const dialog = await screen.findByRole('dialog', { name: 'Onboard model' });

    expect((within(dialog).getByRole('searchbox', { name: 'Search models' }) as HTMLInputElement).disabled).toBe(true);
  });

  it('shows the stale warning above the list', async () => {
    await open({ body: { ...list, stale: true, warning: "Showing the model list from 2026-10-14 02:00 UTC. Can't reach catalog at https://hpia.hpcloud.hp.com." } });

    expect(await screen.findByText("Showing the model list from 2026-10-14 02:00 UTC. Can't reach catalog at https://hpia.hpcloud.hp.com.")).toBeTruthy();
  });

  it('shows the failure with Try again, and Continue stays disabled', async () => {
    await open({ status: 503, body: { detail: "Can't reach catalog at https://hpia.hpcloud.hp.com. Make sure Imaging Core can reach it, then try again." } });
    const dialog = await screen.findByRole('dialog', { name: 'Onboard model' });

    expect(await within(dialog).findByText("Couldn't get HP's model list")).toBeTruthy();
    expect(within(dialog).getByText("Can't reach catalog at https://hpia.hpcloud.hp.com. Make sure Imaging Core can reach it, then try again.")).toBeTruthy();
    expect(within(dialog).getByRole('button', { name: 'Try again' })).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Continue' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('previews per model, notes that nothing is downloaded, and Onboard opens the first draft', async () => {
    const onboarded: unknown[] = [];
    await open({ body: list }, {
      'POST /api/drivers/models/preview': { body: { models: [{ catalogModelKey: '8C26_X64', displayName: 'HP EliteBook 840 G11', offline: 26, offlineAndSpecialize: 2, specialize: 3, skip: 9, sizeBytes: 2_410_000_000 }], errors: [] } },
      'POST /api/drivers/models': init => { onboarded.push(JSON.parse(String(init?.body))); return { status: 201, body: { modelIds: [modelId] } }; },
      [`GET /api/drivers/models/${modelId}`]: { body: page({ model: row({ state: 'Draft' }), checkBlockedReason: 'Draft', setVersion: null }) },
    });
    const dialog = await screen.findByRole('dialog', { name: 'Onboard model' });
    fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'HP EliteBook 840 G11' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Continue' }));

    expect(await within(dialog).findByText('26')).toBeTruthy();
    expect(within(dialog).getByText("Nothing is downloaded yet. Next, choose what each component does on the model's page, then complete onboarding.")).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Onboard' }));

    expect(await screen.findByText(/Onboarding is not complete/)).toBeTruthy();
    expect(onboarded).toEqual([{ vendor: 'Hp', catalogModelKeys: ['8C26_X64'], releases: ['Windows11_24H2'] }]);
  });

  it('closes on Escape and returns focus to the button that opened it', async () => {
    await open({ body: list });
    await screen.findByRole('dialog', { name: 'Onboard model' });

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('returns focus to Onboard model when it closes', async () => {
    serve({ 'GET /api/drivers/models': { body: [row()] }, 'GET /api/drivers/vendors': { body: vendors }, 'GET /api/drivers/catalog/models?vendor=Hp': { body: list } });
    renderAt('/drivers?tab=models');
    const opener = await screen.findByRole('button', { name: 'Onboard model' });
    opener.focus();
    fireEvent.click(opener);
    await screen.findByRole('dialog', { name: 'Onboard model' });

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(document.activeElement).toBe(opener);
  });
});

describe('Drivers: model page', () => {
  it('shows the breadcrumb, name with the match key badge, tiles and every component', async () => {
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page() } });
    renderAt(`/drivers/models/${modelId}`);

    expect(await screen.findByRole('heading', { name: 'HP EliteBook 840 G11' })).toBeTruthy();
    expect(within(screen.getByRole('navigation', { name: 'Breadcrumb' })).getByRole('link', { name: 'Models' })).toBeTruthy();
    expect(screen.getByText('Baseboard 8C26')).toBeTruthy();
    expect(screen.queryByText('HP', { exact: true })).toBeNull();
    expect(screen.queryByText(/set 7/)).toBeNull();
    expect(screen.getByText('113 MB')).toBeTruthy();
    expect(screen.getByText('3 of 3 components')).toBeTruthy();
    expect(screen.getByText('New')).toBeTruthy();
    expect(screen.getByText('1 updated by today\'s sync')).toBeTruthy();
    expect(screen.getByLabelText("The vendor's data only allows Skip for this component.")).toBeTruthy();
  });

  it('filters by search and the updated-today toggle, and shows the filtered count', async () => {
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page() } });
    renderAt(`/drivers/models/${modelId}`);
    await screen.findByText('3 of 3 components');

    fireEvent.click(screen.getByRole('button', { name: "1 updated by today's sync" }));

    expect(screen.getByText('1 of 3 components')).toBeTruthy();
    expect(screen.queryByText('Realtek Audio Driver')).toBeNull();
  });

  it('expands a component to its labelled fields and release notes, never a hash or install command', async () => {
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page() } });
    renderAt(`/drivers/models/${modelId}`);

    fireEvent.click(await screen.findByRole('button', { name: 'Show details of Intel Wireless LAN Driver' }));

    expect(screen.getByText('Offline injection')).toBeTruthy();
    expect(screen.getByText('Released by HP')).toBeTruthy();
    expect((screen.getByRole('link', { name: 'Release notes' }) as HTMLAnchorElement).href).toBe('https://ftp.hp.com/pub/softpaq/sp200.html');
    expect(screen.queryByText(/SHA-256|install command/i)).toBeNull();
  });

  it('a draft shows the Draft banner, Check for updates is disabled, and Complete onboarding swaps in the Preparing banner', async () => {
    const completed: string[] = [];
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page({ model: row({ state: 'Draft' }), checkBlockedReason: 'Draft', setVersion: null, hasPreviousSet: false }) },
      [`POST /api/drivers/models/${modelId}/complete`]: () => { completed.push(modelId); return { body: page({ setVersion: null, preparing: { setVersion: 1, done: 0, total: 31 } }) }; },
    });
    renderAt(`/drivers/models/${modelId}`);

    expect(await screen.findByText('Onboarding is not complete. Nothing is downloaded and devices of this model get no drivers until you complete it.')).toBeTruthy();
    expect((screen.getByRole('button', { name: /Check for updates/ }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: /Complete onboarding/ }));

    expect(await screen.findByText('Preparing driver set 1 for Windows 11, version 24H2: 0 of 31 components downloaded.')).toBeTruthy();
    expect(screen.getByText('Devices of this model get no drivers until it is ready.')).toBeTruthy();
    expect(screen.queryByText(/Onboarding is not complete/)).toBeNull();
    expect(completed).toEqual([modelId]);
  });

  it('Check for updates starts a run for this model and confirms with a toast', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`POST /api/drivers/models/${modelId}/check`]: { status: 202, body: { runId: 'r' } },
    });
    renderAt(`/drivers/models/${modelId}`);

    fireEvent.click(await screen.findByRole('button', { name: /Check for updates/ }));

    await waitFor(() => expect(notify).toHaveBeenCalledWith({ status: 'success', title: 'Checking HP EliteBook 840 G11 for updates.' }));
  });
});

describe('Drivers: approvals', () => {
  it('a waiting component has the New badge, the banner counts it, and Approve calls route 14 and reloads', async () => {
    const approved: unknown[] = [];
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`POST /api/drivers/models/${modelId}/releases/Windows11_24H2/approvals`]: init => { approved.push(JSON.parse(String(init?.body))); return { body: page() }; },
    });
    renderAt(`/drivers/models/${modelId}`);

    expect(await screen.findByText('1 new component is waiting for approval. They are not installed until you approve them.')).toBeTruthy();
    expect(screen.getByText('New').className).toContain('amber');
    fireEvent.click(screen.getByRole('button', { name: 'Approve Realtek Audio Driver' }));

    await waitFor(() => expect(approved).toEqual([{ lineageKeys: ['sp101'] }]));
    expect(screen.queryByRole('button', { name: 'Approve Intel Wireless LAN Driver' })).toBeNull();
  });

  it('read-only users see the banner and badge but no Approve button', async () => {
    auth.canManageDrivers = false;
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page() } });
    renderAt(`/drivers/models/${modelId}`);

    expect(await screen.findByText(/new component is waiting for approval/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Approve / })).toBeNull();
  });

  it('the Models tab status counts components waiting for approval in text', async () => {
    serve({
      'GET /api/drivers/models': { body: [row({ status: { waitingForApproval: 2, failedItems: 0, syncOff: false, downloading: null } })] },
      'GET /api/drivers/vendors': { body: vendors },
    });
    renderAt('/drivers?tab=models');

    expect(await screen.findByText('2 waiting for approval')).toBeTruthy();
  });
});

describe('Drivers: Dell models', () => {
  const dellDetails = [
    { label: 'Installer', value: '32-bit' },
    { label: 'Hardware match', value: '2 PCI devices' },
    { label: 'Package type', value: 'LWXP' },
    { label: 'Default action', value: 'Specialize' },
    { label: 'Released by Dell', value: '2026-09-01' },
    { label: 'Criticality', value: 'Recommended' },
    { label: 'Added to this model', value: '2026-10-25' },
    { label: 'Previous version', value: 'None' },
    { label: 'Download size', value: '12 MB' },
  ];

  function dellPage(overrides: Record<string, unknown> = {}) {
    return page({
      model: row({ vendor: 'Dell', displayName: 'Latitude 5450', matchKeys: ['0CB2', '0CB4'] }),
      checkBlockedReason: 'SyncOff',
      components: [{ lineageKey: 'dell-103620', componentId: 'CP0VC', name: 'Realtek USB Audio DCH Driver', category: 'Driver - Audio', version: 'A03', lastUpdated: '2026-09-01', action: 'Specialize', allowedActions: ['Specialize', 'Skip'], source: 'Default', ruleName: null, state: 'Active', updatedByLastSync: false, details: dellDetails, descriptionUrl: null, sizeBytes: 12_000_000 }],
      ...overrides,
    });
  }

  it('shows the Dell detail fields and names Dell, never HP, in the header and tooltips', async () => {
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: dellPage() } });
    renderAt(`/drivers/models/${modelId}`);

    expect(await screen.findByText('System SKU 0CB2, 0CB4')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Show details of Realtek USB Audio DCH Driver' }));
    for (const field of ['Installer', 'Hardware match', 'Package type', 'Criticality', 'Released by Dell']) {
      expect(screen.getByText(field)).toBeTruthy();
    }
    const check = screen.getByRole('button', { name: /Check for updates/ });
    fireEvent.focus(check.parentElement!);
    expect(await screen.findByText('Sync is off for Dell.')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/\bHP\b/);
  });

  it('the vendor filter lists supported vendors from the vendors response', async () => {
    serve({
      'GET /api/drivers/models': { body: [row(), row({ modelId: 'd', vendor: 'Dell', displayName: 'Latitude 5450', matchKeys: ['0CB4'] })] },
      'GET /api/drivers/vendors': { body: [vendors[0], { vendor: 'Dell', supported: true, syncEnabled: true, onboardedModels: 1, matchKeyType: 'system SKU' }, { vendor: 'Lenovo', supported: false, syncEnabled: false, onboardedModels: 0, matchKeyType: null }] },
    });
    renderAt('/drivers?tab=models');
    await screen.findByText('Latitude 5450');

    fireEvent.click(screen.getByRole('combobox', { name: 'Vendor' }));
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['All vendors', 'HP', 'Dell']);
    fireEvent.click(screen.getByRole('option', { name: 'Dell' }));

    expect(screen.queryByText('HP EliteBook 840 G11')).toBeNull();
    expect(screen.getByText('Latitude 5450')).toBeTruthy();
  });
});
