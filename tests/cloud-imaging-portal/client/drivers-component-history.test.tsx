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
const base = `/api/drivers/models/${modelId}/releases/Windows11_24H2/components/sp100`;

function component(overrides: Record<string, unknown> = {}) {
  return {
    lineageKey: 'sp100', componentId: 'sp212', name: 'Intel Wireless LAN Driver', category: 'Driver - Network', version: '23.80', lastUpdated: '2026-10-20',
    action: 'Offline', allowedActions: ['Offline', 'Skip'], source: 'Default', ruleName: null, state: 'Active', updatedByLastSync: false,
    details: [], descriptionUrl: null, sizeBytes: 113_000_000, rollBack: null,
    ...overrides,
  };
}

function page(row = component()) {
  return {
    model: {
      modelId, vendor: 'Hp', displayName: 'HP EliteBook 840 G11', matchKeys: ['8C26'], architecture: 'X64', releases: ['Windows11_24H2'], state: 'Active',
      included: 1, skipped: 0, maxDownloadBytes: 113_000_000, status: { waitingForApproval: 0, failedItems: 0, syncOff: false, downloading: null },
    },
    release: 'Windows11_24H2', checkAvailableAt: null, checkBlockedReason: null, setVersion: 53, builtAt: null, hasPreviousSet: false, previousSetVersion: null,
    pendingVersion: null, pending: null, preparing: null,
    tiles: { offline: 1, offlineAndSpecialize: 0, specialize: 0, skip: 0, downloadPerDeviceBytes: 113_000_000 },
    components: [row],
  };
}

const versions = [
  { componentId: 'sp212', version: '23.80', firstSet: 52, lastSet: null, from: '2026-10-20T02:00:00', to: null, changedBy: 'Sync', origin: 'Vendor', status: 'Stored', sizeBytes: 113_000_000 },
  { componentId: 'sp211', version: '23.70', firstSet: 41, lastSet: 51, from: '2026-09-01T02:00:00', to: '2026-10-20T02:00:00', changedBy: 'Sync', origin: 'Vendor', status: 'Stored', sizeBytes: 110_000_000 },
  { componentId: 'sp210', version: '23.60', firstSet: 30, lastSet: 40, from: '2026-08-01T02:00:00', to: '2026-09-01T02:00:00', changedBy: 'Onboarding by olle@contoso.com', origin: 'Vendor', status: 'DownloadsOnRollBack', sizeBytes: 790_000_000 },
  { componentId: 'sp209', version: '23.50', firstSet: 20, lastSet: 29, from: '2026-07-01T02:00:00', to: '2026-08-01T02:00:00', changedBy: 'Sync', origin: 'Vendor', status: 'Unavailable', sizeBytes: 100_000_000 },
];

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={[`/drivers/models/${modelId}`]}>
      <Routes><Route path="/drivers/models/:modelId" element={<DriverModelPage />} /></Routes>
    </MemoryRouter>,
  );
}

async function expand(): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: 'Show details of Intel Wireless LAN Driver' }));
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  sent.length = 0;
  auth.canManageDrivers = true;
});

describe('Drivers: component version history and roll back', () => {
  it('shows the read-only version history with sets, dates, who changed it and the status', async () => {
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page() }, [`GET ${base}/versions`]: { body: versions } });
    renderPage();
    await expand();

    expect(await screen.findByRole('heading', { name: 'Version history' })).toBeTruthy();
    expect(screen.getAllByRole('columnheader').map(h => h.textContent)).toEqual(expect.arrayContaining(['Version', 'Driver sets', 'From', 'To', 'Changed by', 'Status']));
    expect(screen.getByText('52 to 53')).toBeTruthy();
    expect(screen.getByText('41 to 51')).toBeTruthy();
    expect(screen.getByText('Now')).toBeTruthy();
    expect(screen.getAllByText('2026-10-20 02:00', { selector: 'td' }).length).toBe(2);
    expect(screen.getByText('Onboarding by olle@contoso.com')).toBeTruthy();
    expect(screen.getAllByText('Stored').length).toBe(2);
    expect(screen.getByText('Downloads on roll back')).toBeTruthy();
    expect(screen.getByText('No longer available from HP')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'Roll back component' }).length).toBe(1);
  });

  it('the dialog checks the vendor, lists earlier versions with their status, preselects the newest available one and rolls back', async () => {
    let resolveCheck: (value: Response) => void = () => undefined;
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`GET ${base}/versions`]: { body: versions },
      [`PUT ${base}/rollback`]: { body: component({ source: 'RolledBack', version: '23.70', rollBack: { version: '23.70', until: 'NewerVersion', skippedVersion: '23.80', changedBy: 'olle@contoso.com', changedAt: '2026-10-25T10:00:00Z' } }) },
    });
    const fallback = apiFetch.getMockImplementation()!;
    apiFetch.mockImplementation(async (url: string, init?: RequestInit) => url === `${base}/versions?check=true`
      ? new Promise<Response>(resolve => { resolveCheck = resolve; })
      : fallback(url, init));
    renderPage();
    await expand();

    const opener = await screen.findByRole('button', { name: 'Roll back component' });
    await waitFor(() => expect((opener as HTMLButtonElement).disabled).toBe(false));
    opener.focus();
    fireEvent.click(opener);
    const dialog = screen.getByRole('dialog', { name: 'Roll back Intel Wireless LAN Driver?' });
    const versionSelect = within(dialog).getByRole('combobox', { name: 'Version' }) as HTMLButtonElement;
    expect(versionSelect.textContent).toContain('Checking HP');
    expect(versionSelect.disabled).toBe(true);

    resolveCheck(new Response(JSON.stringify(versions), { status: 200 }));
    await waitFor(() => expect(versionSelect.disabled).toBe(false));
    expect(versionSelect.textContent).toContain('23.70 (driver sets 41 to 51)');
    expect(within(dialog).getByText('This version is stored, so the next driver set uses it at once.')).toBeTruthy();
    fireEvent.click(versionSelect);
    const options = screen.getAllByRole('option');
    expect(options.map(o => o.getAttribute('title'))).toEqual(['23.70 (driver sets 41 to 51)', '23.60 (driver sets 30 to 40)', '23.50 (driver sets 20 to 29)']);
    expect(options[1].textContent).toContain('Downloads 790 MB');
    expect(options[2].getAttribute('aria-disabled')).toBe('true');
    expect(options[2].textContent).toContain('No longer available from HP');
    fireEvent.click(options[1]);
    expect(within(dialog).getByText("This version is no longer stored, so it is downloaded again from HP (790 MB) and checked against HP's checksum before the next driver set uses it.")).toBeTruthy();
    const newer = within(dialog).getByRole('radio', { name: /HP publishes a version newer than 23.80/ }) as HTMLInputElement;
    expect(newer.checked).toBe(true);
    fireEvent.click(within(dialog).getByRole('radio', { name: 'I undo the roll back' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Roll back' }));

    await waitFor(() => expect(sent.find(c => c.key === `PUT ${base}/rollback`)?.body).toEqual({ version: '23.60', until: 'Undone' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('shows a 409 reason in the dialog without closing it, and Escape returns focus', async () => {
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page() },
      [`GET ${base}/versions`]: { body: versions },
      [`GET ${base}/versions?check=true`]: { body: versions },
      [`PUT ${base}/rollback`]: { status: 409, body: { detail: 'Version 23.70 could not be downloaded again from HP: ftp.hp.com answered 404.' } },
    });
    renderPage();
    await expand();
    const opener = await screen.findByRole('button', { name: 'Roll back component' });
    await waitFor(() => expect((opener as HTMLButtonElement).disabled).toBe(false));
    opener.focus();
    fireEvent.click(opener);
    const dialog = screen.getByRole('dialog');
    await waitFor(() => expect((within(dialog).getByRole('combobox', { name: 'Version' }) as HTMLButtonElement).disabled).toBe(false));

    fireEvent.click(within(dialog).getByRole('button', { name: 'Roll back' }));

    expect(await within(dialog).findByRole('alert')).toBeTruthy();
    expect(within(dialog).getByRole('alert').textContent).toBe('Version 23.70 could not be downloaded again from HP: ftp.hp.com answered 404.');
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  it('a rolled back row shows Rolled back with who, the skipped version and when it moves forward, and can be undone', async () => {
    const rolled = component({ source: 'RolledBack', version: '23.70', rollBack: { version: '23.70', until: 'NewerVersion', skippedVersion: '23.80', changedBy: 'olle@contoso.com', changedAt: '2026-10-25T10:00:00Z' } });
    serve({
      [`GET /api/drivers/models/${modelId}`]: { body: page(rolled) },
      [`GET ${base}/versions`]: { body: versions },
      [`DELETE ${base}/rollback`]: { body: component() },
    });
    renderPage();

    expect(await screen.findByText('Rolled back')).toBeTruthy();
    expect(screen.getByLabelText('Rolled back to 23.70 by olle@contoso.com on 2026-10-25, skipping 23.80. Moves forward when HP publishes a version newer than 23.80.')).toBeTruthy();
    await expand();
    fireEvent.click(await screen.findByRole('button', { name: 'Undo roll back' }));

    // A policy change, so it is confirmed in an overlay that says what devices get next.
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/uses the version HP publishes now instead of 23.70/)).toBeTruthy();
    expect(sent.some(c => c.key === `DELETE ${base}/rollback`)).toBe(false);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Undo roll back' }));

    await waitFor(() => expect(sent.some(c => c.key === `DELETE ${base}/rollback`)).toBe(true));
    expect(screen.queryByRole('button', { name: 'Roll back component' })).toBeNull();
  });

  it('read-only users see the history but neither button', async () => {
    auth.canManageDrivers = false;
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: page() }, [`GET ${base}/versions`]: { body: versions } });
    renderPage();
    await expand();

    expect(await screen.findByRole('heading', { name: 'Version history' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Roll back component' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Undo roll back' })).toBeNull();
  });
});
