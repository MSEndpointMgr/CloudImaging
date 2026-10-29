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

type Reply = { status?: number; body?: unknown };
type Handler = (init?: RequestInit) => Reply;

const calls: { key: string; body: unknown }[] = [];

function serve(routes: Record<string, Handler | Reply>): void {
  apiFetch.mockImplementation(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${url}`;
    calls.push({ key, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const route = routes[key];
    if (!route) return new Response(JSON.stringify({ detail: `unexpected ${key}` }), { status: 500 });
    const { status = 200, body } = typeof route === 'function' ? route(init) : route;
    return new Response(body === undefined ? null : JSON.stringify(body), { status });
  });
}

const modelId = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';
const base = `/api/drivers/models/${modelId}/releases/Windows11_24H2`;

function component(lineageKey: string, name: string, overrides: Record<string, unknown> = {}) {
  return {
    lineageKey, componentId: `c-${lineageKey}`, name, category: 'Driver - Network', version: '1.0', lastUpdated: '2026-10-20',
    action: 'Offline', allowedActions: ['Offline', 'Skip'], source: 'Default', ruleName: null, state: 'Active',
    updatedByLastSync: false, details: [], descriptionUrl: null, sizeBytes: 10, ...overrides,
  };
}

function page(overrides: Record<string, unknown> = {}) {
  return {
    model: { modelId, vendor: 'Hp', displayName: 'HP EliteBook 840 G11', matchKeys: ['8C26'], architecture: 'X64', releases: ['Windows11_24H2'], state: 'Active', included: 3, skipped: 1, maxDownloadBytes: 30, status: { waitingForApproval: 0, failedItems: 0, syncOff: false, downloading: null } },
    release: 'Windows11_24H2', checkAvailableAt: null, checkBlockedReason: null, setVersion: 53, builtAt: null, hasPreviousSet: true, pendingVersion: null, pending: null, preparing: null,
    tiles: { offline: 3, offlineAndSpecialize: 0, specialize: 0, skip: 1, downloadPerDeviceBytes: 30 },
    components: [
      component('sp100', 'Intel Wireless LAN Driver'),
      component('sp101', 'Realtek Audio Driver', { source: 'Override', action: 'Skip' }),
      component('sp102', 'HP Services Scan', { source: 'Rule', ruleName: 'Skip HP Services Scan', action: 'Skip' }),
      component('sp103', 'HP Hotkey Service', { source: 'Locked', action: 'Skip', allowedActions: ['Skip'], state: 'Withdrawn' }),
    ],
    ...overrides,
  };
}

const rules = [
  { ruleId: 'r1', name: 'Skip HP Services Scan', scope: 'Hp', match: 'NamePattern', value: 'HP Services Scan*', action: 'Skip', changedAt: '2026-10-25T09:00:00Z', changedBy: 'olle@contoso.com', hits: 10 },
  { ruleId: 'r2', name: 'Enabling', scope: 'AllVendors', match: 'Category', value: 'Driver - Enabling', action: 'Skip', changedAt: '2026-10-25T09:00:00Z', changedBy: 'olle@contoso.com', hits: 0 },
];
const categories = [
  { vendor: 'Hp', category: 'Driver - Network', critical: false },
  { vendor: 'Hp', category: 'Driver - Storage', critical: true },
];
const vendors = [
  { vendor: 'Hp', supported: true, syncEnabled: true, onboardedModels: 1, matchKeyType: 'system ID' },
  { vendor: 'Lenovo', supported: false, syncEnabled: false, onboardedModels: 0, matchKeyType: null },
];
const rulesRoutes = {
  'GET /api/drivers/rules': { body: rules },
  'GET /api/drivers/categories': { body: categories },
  'GET /api/drivers/vendors': { body: vendors },
};

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
  calls.length = 0;
  auth.canManageDrivers = true;
});

describe('Drivers: component actions on the model page', () => {
  it('the Action dropdown lists only the allowed actions, and a locked row is disabled with the lock tooltip', async () => {
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page() } });
    renderAt(`/drivers/models/${modelId}`);

    const select = await screen.findByRole('combobox', { name: 'Action for Intel Wireless LAN Driver' });
    fireEvent.click(select);

    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['Offline', 'Skip']);
    expect((screen.getByRole('combobox', { name: 'Action for HP Hotkey Service' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByLabelText("The vendor's data only allows Skip for this component.")).toBeTruthy();
  });

  it('Source shows Locked, Override with Undo override, the rule name over Rule, and Default', async () => {
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page() } });
    renderAt(`/drivers/models/${modelId}`);

    await screen.findByText('Skip HP Services Scan');
    const rows = screen.getAllByRole('row');
    const sourceOf = (name: string) => within(rows.find(r => r.textContent?.includes(name))!).getAllByRole('cell')[5];
    expect(sourceOf('Intel Wireless LAN Driver').textContent).toBe('Default');
    expect(sourceOf('Realtek Audio Driver').textContent).toContain('Override');
    expect(within(sourceOf('Realtek Audio Driver')).getByRole('button', { name: 'Undo override of Realtek Audio Driver' })).toBeTruthy();
    expect(sourceOf('HP Services Scan').textContent).toBe('Skip HP Services ScanRule');
    expect(sourceOf('HP Hotkey Service').textContent).toBe('Locked');
  });

  it('a locked component with nothing to approve or remove leaves the table for the footer list', async () => {
    const body = page();
    body.components[3] = component('sp103', 'HP Hotkey Service', { source: 'Locked', action: 'Skip', allowedActions: ['Skip'] });
    serve({ [`GET /api/drivers/models/${modelId}`]: { body } });
    renderAt(`/drivers/models/${modelId}`);

    await screen.findByText('3 components');
    expect(screen.queryByRole('combobox', { name: 'Action for HP Hotkey Service' })).toBeNull();
    expect(screen.getByRole('button', { name: '1 locked to Skip' })).toBeTruthy();
  });

  it('choosing an action sets an override and Undo override removes it, reloading the page each time', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`PUT ${base}/overrides/sp100`]: { body: component('sp100', 'Intel Wireless LAN Driver', { action: 'Skip', source: 'Override' }) },
      [`DELETE ${base}/overrides/sp101`]: { body: component('sp101', 'Realtek Audio Driver') },
    });
    renderAt(`/drivers/models/${modelId}`);

    fireEvent.click(await screen.findByRole('combobox', { name: 'Action for Intel Wireless LAN Driver' }));
    fireEvent.click(screen.getByRole('option', { name: 'Skip' }));
    await waitFor(() => expect(calls.some(c => c.key === `PUT ${base}/overrides/sp100`)).toBe(true));
    fireEvent.click(screen.getByRole('button', { name: 'Undo override of Realtek Audio Driver' }));

    await waitFor(() => expect(calls.some(c => c.key === `DELETE ${base}/overrides/sp101`)).toBe(true));
    expect(calls.find(c => c.key === `PUT ${base}/overrides/sp100`)!.body).toEqual({ action: 'Skip' });
    await waitFor(() => expect(calls.filter(c => c.key === `GET /api/drivers/models/${modelId}`).length).toBe(3));
  });

  it('the waiting changes banner shows the count and build time, and Build now calls route 33', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page({ pending: { changes: 3, buildAt: '2026-10-25T14:32:00Z' } }) },
      [`POST ${base}/build`]: { status: 202 },
    });
    renderAt(`/drivers/models/${modelId}`);

    expect(await screen.findByText(/^3 changes are waiting\. Driver set 54 is built at \d\d:\d\d, 2 minutes after the last change\.$/)).toBeTruthy();
    expect(screen.getByText('Devices keep getting driver set 53 until set 54 is built and its components are downloaded.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Build now/ }));

    await waitFor(() => expect(calls.some(c => c.key === `POST ${base}/build`)).toBe(true));
  });

  it('read-only users see actions as text and no undo or Build now', async () => {
    auth.canManageDrivers = false;
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page({ pending: { changes: 1, buildAt: '2026-10-25T14:32:00Z' } }) } });
    renderAt(`/drivers/models/${modelId}`);

    await screen.findByText(/^1 change is waiting/);
    expect(screen.queryByRole('combobox', { name: /Action for/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Undo override/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Build now/ })).toBeNull();
  });
});

describe('Drivers: Rules tab', () => {
  it('lists rules with Applies to, Matches, Action and Hits, and the precedence info', async () => {
    serve(rulesRoutes);
    renderAt('/drivers?tab=rules');

    await screen.findByText('Skip HP Services Scan');
    expect(screen.getAllByRole('columnheader').map(h => h.textContent)).toEqual(['Name', 'Applies to', 'Matches', 'Action', 'Hits', 'Actions']);
    expect(screen.getByText('Name: HP Services Scan*')).toBeTruthy();
    expect(screen.getByText('Category: Driver - Enabling')).toBeTruthy();
    expect(screen.getByText('All vendors')).toBeTruthy();
    expect(screen.getByText('10')).toBeTruthy();
    expect(screen.getByLabelText(/A model override wins over rules, and vendor rules win over rules for all vendors\./)).toBeTruthy();
  });

  it('the rule dialog shows field problems before saving, then adds the rule and reloads', async () => {
    serve({ ...rulesRoutes, 'POST /api/drivers/rules': { status: 201, body: { ...rules[0], ruleId: 'r3', name: 'Skip telemetry' } } });
    renderAt('/drivers?tab=rules');
    const add = await screen.findByRole('button', { name: 'Add rule' });
    fireEvent.click(add);
    const dialog = await screen.findByRole('dialog', { name: 'Add rule' });

    fireEvent.click(within(dialog).getByRole('button', { name: 'Add rule' }));
    expect(within(dialog).getByText('Enter a name of 1 to 100 characters.')).toBeTruthy();
    expect(within(dialog).getByText('Enter a pattern of 1 to 200 characters. Use * as a wildcard.')).toBeTruthy();
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'skip hp services scan' } });
    expect(within(dialog).getByText('A rule named "skip hp services scan" already exists.')).toBeTruthy();

    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Skip telemetry' } });
    fireEvent.change(within(dialog).getByLabelText('Name pattern'), { target: { value: '*Telemetry*' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add rule' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(calls.find(c => c.key === 'POST /api/drivers/rules')!.body).toEqual({ name: 'Skip telemetry', scope: 'AllVendors', match: 'NamePattern', value: '*Telemetry*', action: 'Skip' });
    expect(calls.filter(c => c.key === 'GET /api/drivers/rules').length).toBe(2);
  });

  it('the rule dialog closes on Escape and returns focus to Add rule', async () => {
    serve(rulesRoutes);
    renderAt('/drivers?tab=rules');
    const add = await screen.findByRole('button', { name: 'Add rule' });
    add.focus();
    fireEvent.click(add);
    await screen.findByRole('dialog', { name: 'Add rule' });

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(add);
  });

  it('Suggested rules lists suggestions and adds one only when Add is pressed', async () => {
    serve({
      ...rulesRoutes,
      'GET /api/drivers/rules/suggestions': { body: [{ ruleId: '00000000-0000-0000-0000-000000000000', name: 'HP App', scope: 'Hp', match: 'NamePattern', value: 'HP App', action: 'Skip', changedAt: '0001-01-01T00:00:00Z', changedBy: null, hits: 1 }] },
      'POST /api/drivers/rules': { status: 201, body: { ...rules[0], ruleId: 'r4', name: 'HP App' } },
    });
    renderAt('/drivers?tab=rules');
    fireEvent.click(await screen.findByRole('button', { name: 'Suggested rules' }));
    const dialog = await screen.findByRole('dialog', { name: 'Suggested rules' });

    expect(await within(dialog).findByText('Skip, HP, Name: HP App. Matches 1 component.')).toBeTruthy();
    expect(calls.some(c => c.key === 'POST /api/drivers/rules')).toBe(false);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add HP App' }));

    await waitFor(() => expect(calls.find(c => c.key === 'POST /api/drivers/rules')!.body).toEqual({ name: 'HP App', scope: 'Hp', match: 'NamePattern', value: 'HP App', action: 'Skip' }));
    expect(await within(dialog).findByText('Every suggested rule is added')).toBeTruthy();
  });

  it('deleting a rule asks first and names its impact', async () => {
    serve({ ...rulesRoutes, 'DELETE /api/drivers/rules/r1': { status: 204 } });
    renderAt('/drivers?tab=rules');

    fireEvent.click(await screen.findByRole('button', { name: 'Delete Skip HP Services Scan' }));
    const confirm = screen.getByRole('dialog', { name: 'Delete Skip HP Services Scan?' });
    expect(within(confirm).getByText(/The 10 components this rule decides go back to the next rule or their vendor default\./)).toBeTruthy();
    fireEvent.click(within(confirm).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(calls.some(c => c.key === 'DELETE /api/drivers/rules/r1')).toBe(true));
  });

  it('the Deployment failure card applies a category on change', async () => {
    serve({ ...rulesRoutes, 'PUT /api/drivers/critical-categories/Hp': { body: categories.map(c => ({ ...c, critical: true })) } });
    renderAt('/drivers?tab=rules');

    expect(await screen.findByText('Choose the driver categories whose failure fails the whole deployment. Other failures complete with warnings.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'HP critical categories: Driver - Storage' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Driver - Network' }));

    await waitFor(() => expect(calls.find(c => c.key === 'PUT /api/drivers/critical-categories/Hp')!.body).toEqual({ categories: ['Driver - Storage', 'Driver - Network'] }));
    expect(await screen.findByRole('button', { name: 'HP critical categories: Driver - Network, Driver - Storage' })).toBeTruthy();
  });

  it('read-only users see rules without Add, Suggested, edit or delete, and the categories cannot change', async () => {
    auth.canManageDrivers = false;
    serve(rulesRoutes);
    renderAt('/drivers?tab=rules');

    await screen.findByText('Skip HP Services Scan');
    expect(screen.queryByRole('button', { name: 'Add rule' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Suggested rules' })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Edit / })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Delete / })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'HP critical categories: Driver - Storage' }));
    expect((screen.getByRole('checkbox', { name: 'Driver - Storage' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
