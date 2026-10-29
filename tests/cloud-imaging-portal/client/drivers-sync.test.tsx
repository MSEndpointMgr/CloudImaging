import React from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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
import { browserTimeZone, daysPhrase, nextSyncLine, runSummary, type DriverSyncRun } from '../../../src/cloud-imaging-portal/client/src/lib/drivers.ts';

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

const zones = [
  { ianaId: 'Etc/UTC', displayName: '(UTC) Coordinated Universal Time', covers: ['Etc/UTC', 'UTC'] },
  { ianaId: 'Europe/London', displayName: '(UTC+00:00) Dublin, Edinburgh, Lisbon, London', covers: ['Europe/London'] },
  { ianaId: 'Europe/Berlin', displayName: '(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna', covers: ['Europe/Berlin', 'Europe/Stockholm'] },
];

function settings(overrides: Record<string, unknown> = {}) {
  return {
    schedule: 'Daily', weekdays: 31, repeatWeeks: 1, localTime: '02:00', timeZone: 'Europe/Berlin', waitForApprovalOfNewComponents: false,
    nextRunAt: '2026-10-26T01:00:00Z', syncNowAvailableAt: null, lastRunStartedAt: '2026-10-25T01:00:00Z', changedAt: '2026-10-20T08:00:00Z',
    timeZones: zones, ...overrides,
  };
}

const vendors = [
  { vendor: 'Hp', supported: true, syncEnabled: true, onboardedModels: 3, matchKeyType: 'system ID' },
  { vendor: 'Lenovo', supported: false, syncEnabled: false, onboardedModels: 0, matchKeyType: null },
];

const modelId = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';
const runs = [
  { runId: 'r1', trigger: 'Scheduled', modelId: null, modelName: null, startedAt: '2026-10-25T01:00:00Z', finishedAt: '2026-10-25T01:20:00Z', result: 'PartlyFailed', startedBy: null, counts: { Updated: 4, Failed: 1 }, vendors: [{ vendor: 'Hp', processed: true, failureReason: null, newModels: 3 }] },
  { runId: 'r2', trigger: 'ModelCheck', modelId, modelName: 'HP EliteBook 840 G11', startedAt: '2026-10-24T12:00:00Z', finishedAt: '2026-10-24T12:01:00Z', result: 'Completed', startedBy: 'olle@contoso.com', counts: {}, vendors: [] },
];

function syncRoutes(extra: Record<string, Handler | Reply> = {}, current = settings()): Record<string, Handler | Reply> {
  return {
    'GET /api/drivers/sync/settings': { body: current },
    'GET /api/drivers/vendors': { body: vendors },
    'GET /api/drivers/sync/runs': { body: runs },
    'PUT /api/drivers/sync/settings': init => ({ body: { ...current, ...JSON.parse(String(init?.body)), timeZones: [] } }),
    ...extra,
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

const puts = () => calls.filter(c => c.key === 'PUT /api/drivers/sync/settings').map(c => c.body as Record<string, unknown>);

beforeEach(() => {
  vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ timeZone: 'Europe/Stockholm' } as Intl.ResolvedDateTimeFormatOptions);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.clearAllMocks();
  calls.length = 0;
  auth.canManageDrivers = true;
});

describe('Drivers: sync helpers', () => {
  it('phrases days as a range for three or more consecutive days, otherwise as a list', () => {
    expect(daysPhrase(31)).toBe('Monday to Friday');
    expect(daysPhrase(1 | 4 | 16)).toBe('Monday, Wednesday and Friday');
    expect(daysPhrase(1)).toBe('Monday');
  });

  it('writes the next-sync line in the schedule\'s time zone with the repeat, and none for Manually only', () => {
    expect(nextSyncLine({ schedule: 'Daily', weekdays: 0, repeatWeeks: 1, timeZone: 'Europe/Berlin', nextRunAt: '2026-10-26T01:00:00Z' })).toBe('Next sync: Monday 2026-10-26 02:00, then every day.');
    expect(nextSyncLine({ schedule: 'SelectedDays', weekdays: 31, repeatWeeks: 1, timeZone: 'Europe/Berlin', nextRunAt: '2026-10-26T01:00:00Z' })).toBe('Next sync: Monday 2026-10-26 02:00, then every week on Monday to Friday.');
    expect(nextSyncLine({ schedule: 'SelectedDays', weekdays: 1 | 4 | 16, repeatWeeks: 1, timeZone: 'Europe/Berlin', nextRunAt: '2026-10-26T01:00:00Z' })).toBe('Next sync: Monday 2026-10-26 02:00, then every week on Monday, Wednesday and Friday.');
    expect(nextSyncLine({ schedule: 'SelectedDays', weekdays: 1, repeatWeeks: 2, timeZone: 'Europe/Berlin', nextRunAt: '2026-10-26T01:00:00Z' })).toBe('Next sync: Monday 2026-10-26 02:00, then every 2 weeks on Monday.');
    expect(nextSyncLine({ schedule: 'Manual', weekdays: 0, repeatWeeks: 1, timeZone: 'Europe/Berlin', nextRunAt: null })).toBeNull();
  });

  it('finds the browser\'s entry through covers, and falls back to UTC', () => {
    expect(browserTimeZone(zones, 'Europe/Stockholm')?.ianaId).toBe('Europe/Berlin');
    expect(browserTimeZone(zones, 'Antarctica/Troll')?.ianaId).toBe('Etc/UTC');
  });

  it('summarises a run, naming the model first for a model check and adding new models', () => {
    expect(runSummary(runs[0] as DriverSyncRun)).toBe('4 updated, 1 failed. HP: 3 new models available');
    expect(runSummary(runs[1] as DriverSyncRun)).toBe('HP EliteBook 840 G11: No changes');
    expect(runSummary({ ...runs[1], trigger: 'Onboarding', downloadedComponents: 19, downloadedBytes: 2_640_000_000 } as DriverSyncRun)).toBe('HP EliteBook 840 G11: Onboarded, 19 components downloaded, 2.64 GB');
  });
});

describe('Drivers: Sync tab', () => {
  it('applies a schedule change at once, with no Save button', async () => {
    serve(syncRoutes());
    renderAt('/drivers?tab=sync');

    fireEvent.click(await screen.findByRole('combobox', { name: 'Schedule' }));
    fireEvent.click(screen.getByRole('option', { name: 'Manually only' }));

    await waitFor(() => expect(puts()).toHaveLength(1));
    expect(puts()[0]).toMatchObject({ schedule: 'Manual', timeZone: 'Europe/Berlin' });
    expect(screen.queryByRole('button', { name: /^Save/ })).toBeNull();
    await waitFor(() => expect(notify).toHaveBeenCalledWith({ status: 'success', title: 'Sync settings saved.' }));
  });

  it('shows the next-sync line, and Selected days preselects Monday to Friday with the repeat select', async () => {
    serve(syncRoutes());
    renderAt('/drivers?tab=sync');

    expect(await screen.findByText('Next sync: Monday 2026-10-26 02:00, then every day.')).toBeTruthy();
    expect(screen.queryByRole('combobox', { name: 'Repeat' })).toBeNull();
    fireEvent.click(screen.getByRole('combobox', { name: 'Schedule' }));
    fireEvent.click(screen.getByRole('option', { name: 'Selected days' }));

    await waitFor(() => expect(puts()[0]).toMatchObject({ schedule: 'SelectedDays', weekdays: 31, repeatWeeks: 1 }));
    expect(await screen.findByRole('combobox', { name: 'Repeat' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Monday' }).getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByRole('button', { name: 'Saturday' }).getAttribute('aria-pressed')).toBe('false');
  });

  it('clearing every day shows the error in place of the next-sync line and saves nothing', async () => {
    serve(syncRoutes({}, settings({ schedule: 'SelectedDays', weekdays: 1 })));
    renderAt('/drivers?tab=sync');

    fireEvent.click(await screen.findByRole('button', { name: 'Monday' }));

    expect(screen.getByRole('alert').textContent).toBe('Choose at least one day. The last saved days still apply.');
    expect(screen.getByRole('group', { name: 'Days' }).getAttribute('aria-invalid')).toBe('true');
    expect(screen.queryByText(/^Next sync:/)).toBeNull();
    expect(puts()).toHaveLength(0);
  });

  it('saves a typed 24-hour time, and puts back a time that is not one', async () => {
    serve(syncRoutes());
    renderAt('/drivers?tab=sync');
    const time = await screen.findByRole('textbox', { name: 'Time' }) as HTMLInputElement;

    fireEvent.change(time, { target: { value: '03:30' } });
    await waitFor(() => expect(puts()[0]).toMatchObject({ localTime: '03:30' }));

    fireEvent.change(time, { target: { value: '25:00' } });
    fireEvent.blur(time);
    expect(time.value).toBe('03:30');
    expect(puts()).toHaveLength(1);
  });

  it('New components is a choice between waiting for approval and using them automatically', async () => {
    serve(syncRoutes());
    renderAt('/drivers?tab=sync');

    fireEvent.click(await screen.findByRole('combobox', { name: 'New components' }));
    fireEvent.click(screen.getByRole('option', { name: 'Wait for approval' }));

    await waitFor(() => expect(puts()[0]).toMatchObject({ waitForApprovalOfNewComponents: true }));
  });

  it('lists time zones by Windows display name, the browser\'s first, and searches them', async () => {
    serve(syncRoutes({}, settings({ timeZone: 'Etc/UTC' })));
    renderAt('/drivers?tab=sync');

    fireEvent.click(await screen.findByRole('combobox', { name: 'Time zone: (UTC) Coordinated Universal Time' }));

    const browserGroup = screen.getByRole('group', { name: 'Your browser' });
    expect(within(browserGroup).getByRole('option').textContent).toBe('(UTC+01:00) Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna');
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search time zones' }), { target: { value: 'london' } });
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['(UTC+00:00) Dublin, Edinburgh, Lisbon, London']);
    fireEvent.click(screen.getByRole('option'));

    await waitFor(() => expect(puts()[0]).toMatchObject({ timeZone: 'Europe/London' }));
  });

  it('a schedule never saved takes the browser\'s time zone', async () => {
    serve(syncRoutes({}, settings({ timeZone: 'Etc/UTC', changedAt: null })));
    renderAt('/drivers?tab=sync');

    await waitFor(() => expect(puts()[0]).toMatchObject({ timeZone: 'Europe/Berlin', schedule: 'Daily', localTime: '02:00' }));
    expect(notify).not.toHaveBeenCalled();
  });

  it('Sync calls route 26, and is disabled until syncNowAvailableAt with the time in its tooltip', async () => {
    serve(syncRoutes({ 'POST /api/drivers/sync/runs': { status: 202, body: { runId: 'r3' } } }));
    renderAt('/drivers?tab=sync');

    expect(await screen.findByText(/Last sync started 2026-10-25/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /^Sync$/ }));
    await waitFor(() => expect(calls.some(c => c.key === 'POST /api/drivers/sync/runs')).toBe(true));
    cleanup();

    serve(syncRoutes({}, settings({ syncNowAvailableAt: new Date(Date.now() + 30 * 60_000).toISOString() })));
    renderAt('/drivers?tab=sync');
    const button = await screen.findByRole('button', { name: /^Sync$/ });
    expect((button as HTMLButtonElement).disabled).toBe(true);
    fireEvent.focus(button.parentElement!);
    expect(await screen.findByText(/^Available again at \d\d:\d\d$/)).toBeTruthy();
  });

  it('the Vendors card has a switch per vendor, Lenovo disabled, and a switch change calls route 2', async () => {
    serve(syncRoutes({ 'PUT /api/drivers/vendors/Hp': { body: { ...vendors[0], syncEnabled: false } } }));
    renderAt('/drivers?tab=sync');

    expect(await screen.findByText('3 onboarded models, matched on system ID.')).toBeTruthy();
    expect(screen.getByText('Not supported yet.')).toBeTruthy();
    expect((screen.getByRole('switch', { name: 'Sync Lenovo' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('switch', { name: 'Sync HP' }));

    await waitFor(() => expect(calls.find(c => c.key === 'PUT /api/drivers/vendors/Hp')!.body).toEqual({ syncEnabled: false }));
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Sync HP' }).getAttribute('aria-checked')).toBe('false'));
  });

  it('History rows have a result badge and summary, a chevron only with items, and items link to the model page', async () => {
    serve(syncRoutes({ 'GET /api/drivers/sync/runs/r1/items': { body: [{ modelId, modelName: 'HP EliteBook 840 G11', release: 'Windows11_24H2', lineageKey: 'sp140001', componentName: 'Intel Wireless LAN Driver', oldVersion: '23.70', newVersion: '23.80', outcome: 'Updated', reason: null }] } }));
    renderAt('/drivers?tab=sync');

    expect(await screen.findByText('Partly failed')).toBeTruthy();
    expect(screen.getByText('4 updated, 1 failed. HP: 3 new models available')).toBeTruthy();
    expect(screen.getByText('HP EliteBook 840 G11: No changes')).toBeTruthy();
    const chevrons = screen.getAllByRole('button', { name: /^Show the changes of the run/ });
    expect(chevrons).toHaveLength(1);
    fireEvent.click(chevrons[0]);

    const link = await screen.findByRole('link', { name: 'Intel Wireless LAN Driver' });
    expect(link.getAttribute('href')).toBe(`/drivers/models/${modelId}?release=Windows11_24H2#sp140001`);
    expect(screen.getByText('23.70 to 23.80')).toBeTruthy();
  });

  it('read-only users see the settings with every control disabled and no Sync button', async () => {
    auth.canManageDrivers = false;
    serve(syncRoutes());
    renderAt('/drivers?tab=sync');

    expect((await screen.findByRole('combobox', { name: 'Schedule' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('combobox', { name: 'New components' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('combobox', { name: 'Newer versions' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('switch', { name: 'Sync HP' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: /^Sync$/ })).toBeNull();
  });
});

describe('Drivers: checks on the Models tab and the model page', () => {
  function row() {
    return { modelId, vendor: 'Hp', displayName: 'HP EliteBook 840 G11', matchKeys: ['8C26'], architecture: 'X64', releases: ['Windows11_24H2'], state: 'Active', included: 3, skipped: 1, maxDownloadBytes: 30, status: { waitingForApproval: 0, failedItems: 0, syncOff: true, downloading: null } };
  }

  it('the Models tab shows Last checked, Sync off, and Check for updates calls route 45', async () => {
    serve({
      'GET /api/drivers/models': { body: [row()] },
      'GET /api/drivers/vendors': { body: vendors },
      'GET /api/drivers/sync/settings': { body: settings() },
      'POST /api/drivers/models/check': { status: 202, body: { runId: 'r' } },
    });
    renderAt('/drivers?tab=models');

    expect(await screen.findByText(/^Last checked 2026-10-25/)).toBeTruthy();
    expect(screen.getByText('Sync off')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Check for updates/ }));

    await waitFor(() => expect(calls.some(c => c.key === 'POST /api/drivers/models/check')).toBe(true));
    expect(notify).toHaveBeenCalledWith({ status: 'success', title: 'Checking all models for updates.' });
  });

  it('the Models tab disables Check for updates until syncNowAvailableAt', async () => {
    serve({
      'GET /api/drivers/models': { body: [row()] },
      'GET /api/drivers/vendors': { body: vendors },
      'GET /api/drivers/sync/settings': { body: settings({ syncNowAvailableAt: new Date(Date.now() + 30 * 60_000).toISOString() }) },
    });
    renderAt('/drivers?tab=models');

    await waitFor(() => expect((screen.getByRole('button', { name: /Check for updates/ }) as HTMLButtonElement).disabled).toBe(true));
  });

  it.each([
    ['SyncOff', 'Sync is off for HP.'],
    ['Running', 'A sync is running.'],
  ])('the model page disables Check for updates when %s, saying why', async (reason, text) => {
    serve({ [`GET /api/drivers/models/${modelId}`]: { body: {
      model: row(), release: 'Windows11_24H2', checkAvailableAt: null, checkBlockedReason: reason, setVersion: 1, builtAt: null, hasPreviousSet: false, pendingVersion: null, pending: null, preparing: null,
      tiles: { offline: 0, offlineAndSpecialize: 0, specialize: 0, skip: 0, downloadPerDeviceBytes: 0 }, components: [],
    } } });
    renderAt(`/drivers/models/${modelId}`);

    const button = await screen.findByRole('button', { name: /Check for updates/ });
    expect((button as HTMLButtonElement).disabled).toBe(true);
    fireEvent.focus(button.parentElement!);
    expect(await screen.findByText(text)).toBeTruthy();
  });
});
