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

type Reply = { status?: number; body?: unknown };
const calls: string[] = [];

function serve(routes: Record<string, Reply | (() => Reply)>): void {
  apiFetch.mockImplementation(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${url}`;
    calls.push(key);
    const route = routes[key];
    if (!route) return new Response(JSON.stringify({ detail: `unexpected ${key}` }), { status: 500 });
    const { status = 200, body } = typeof route === 'function' ? route() : route;
    return new Response(body === undefined ? null : JSON.stringify(body), { status });
  });
}

const settings = {
  schedule: 'Daily', weekdays: 31, repeatWeeks: 1, localTime: '02:00', timeZone: 'Etc/UTC', waitForApprovalOfNewComponents: false,
  nextRunAt: '2026-10-27T02:00:00Z', syncNowAvailableAt: null, lastRunStartedAt: '2026-10-26T14:02:00Z', changedAt: '2026-10-20T08:00:00Z',
  timeZones: [{ ianaId: 'Etc/UTC', displayName: '(UTC) Coordinated Universal Time', covers: ['Etc/UTC'] }],
};

const modelId = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';

function run(overrides: Record<string, unknown> = {}) {
  return {
    runId: 'r1', trigger: 'Manual', modelId: null, modelName: null, startedAt: '2026-10-26T14:02:00Z', finishedAt: null, result: 'Running', startedBy: 'megan.bowen@contoso.com',
    counts: {}, vendors: [{ vendor: 'Hp', processed: true, failureReason: null, newModels: 3 }, { vendor: 'Dell', processed: false, failureReason: 'The catalog could not be downloaded.', newModels: 0 }],
    modelsTotal: 12, modelsChecked: 7, downloadedComponents: 14, downloadedBytes: 1_200_000_000, newSets: 1, ...overrides,
  };
}

function progress(overrides: Record<string, unknown> = {}, runOverrides: Record<string, unknown> = {}) {
  return {
    run: run(runOverrides),
    catalogs: { done: 2, total: 2 },
    models: { done: 7, total: 12 },
    components: { done: 15, total: 31 },
    driverSets: { done: 3, total: 7 },
    found: { Updated: 5, New: 2, Failed: 1 },
    latest: [
      { modelId, modelName: 'HP EliteBook 840 G11', release: 'Windows11_24H2', lineageKey: 'sp100', componentName: 'Intel Wireless LAN Driver', oldVersion: '23.80.0.6', newVersion: '23.90.0.4', outcome: 'Updated', reason: null, at: '2026-10-26T14:08:00Z', vendor: 'Hp', category: 'Driver - Network' },
      { modelId: null, modelName: null, release: null, lineageKey: null, componentName: 'Realtek Audio Driver', oldVersion: null, newVersion: '6.0.9690.1', outcome: 'Failed', reason: 'The file did not match the vendor\'s hash.', at: '2026-10-26T14:07:00Z', vendor: 'Dell', category: 'Audio' },
    ],
    ...overrides,
  };
}

function renderSyncTab(): void {
  render(
    <MemoryRouter initialEntries={['/drivers?tab=sync']}>
      <Routes><Route path="/drivers" element={<DriversPage />} /></Routes>
    </MemoryRouter>,
  );
}

function routes(runs: unknown[], progressReply: Reply | (() => Reply)): Record<string, Reply | (() => Reply)> {
  return {
    'GET /api/drivers/sync/settings': { body: settings },
    'GET /api/drivers/vendors': { body: [] },
    'GET /api/drivers/sync/runs': { body: runs },
    'GET /api/drivers/sync/runs/r1/progress': progressReply,
  };
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.clearAllMocks();
  calls.length = 0;
});

describe('Drivers: the Sync running card', () => {
  it('shows who started the sync, each stage with its own count, what it found so far and the latest changes', async () => {
    serve(routes([run()], { body: progress() }));
    renderSyncTab();

    expect(await screen.findByRole('heading', { name: 'Sync running' }, { timeout: 5_000 })).toBeTruthy();
    expect(screen.getByText(/^Sync now by megan\.bowen@contoso\.com, started 2026-10-26 \d\d:02, running for \d+ (min|h \d+ min)\.$/)).toBeTruthy();
    expect(screen.getByRole('progressbar', { name: 'Vendor catalogs' }).getAttribute('aria-valuetext')).toBe('2 of 2 read, 3 new models, Dell failed');
    expect(screen.getByRole('progressbar', { name: 'Models' }).getAttribute('aria-valuetext')).toBe('7 of 12 checked');
    expect(screen.getByRole('progressbar', { name: 'Components' }).getAttribute('aria-valuetext')).toBe('14 of 31 downloaded, 1.20 GB');
    expect(screen.getByRole('progressbar', { name: 'Driver sets' }).getAttribute('aria-valuetext')).toBe('3 of 7 checked, 1 new');
    expect(screen.getByText('Dell: The catalog could not be downloaded.')).toBeTruthy();
    expect(screen.getByText('5 updated, 2 new, 1 failed')).toBeTruthy();
    expect(screen.getByText('23.80.0.6 to 23.90.0.4')).toBeTruthy();
    expect(screen.getByText('6.0.9690.1')).toBeTruthy();
    expect(screen.getByText('Every model that uses it')).toBeTruthy();
    expect(screen.getByLabelText('Failed: The file did not match the vendor\'s hash.')).toBeTruthy();
  });

  it('waits for the catalogs before counting models, and a model check has no catalog stage', async () => {
    serve(routes([run({ trigger: 'ModelCheck', modelId, modelName: 'HP EliteBook 840 G11' })], {
      body: progress({ catalogs: { done: 0, total: 1 }, models: { done: 0, total: 0 }, components: { done: 0, total: 0 }, driverSets: { done: 0, total: 0 }, found: {}, latest: [] }, { trigger: 'ModelCheck', modelId, modelName: 'HP EliteBook 840 G11', vendors: [] }),
    }));
    renderSyncTab();

    expect(await screen.findByRole('heading', { name: 'Checking HP EliteBook 840 G11' }, { timeout: 5_000 })).toBeTruthy();
    expect(screen.queryByRole('progressbar', { name: 'Vendor catalogs' })).toBeNull();
    expect(screen.getByRole('progressbar', { name: 'Models' }).getAttribute('aria-valuetext')).toBe('Waiting to start');
    expect(screen.getByText('nothing yet')).toBeTruthy();
  });

  it('shows the result once the sync ends, reloads the history, and Dismiss removes the card', async () => {
    const finished = progress({}, { result: 'PartlyFailed', finishedAt: '2026-10-26T14:19:00Z', counts: { Updated: 9, New: 2, Failed: 1 } });
    serve(routes([run()], { body: finished }));
    renderSyncTab();

    expect(await screen.findByRole('heading', { name: 'Sync finished' }, { timeout: 5_000 })).toBeTruthy();
    expect(screen.getByText(/^Partly failed at 2026-10-26 \d\d:19, after 17 min: 9 updated, 2 new, 1 failed\. 14 components downloaded, 1\.20 GB\.$/)).toBeTruthy();
    await waitFor(() => expect(calls.filter(c => c === 'GET /api/drivers/sync/runs').length).toBeGreaterThan(1));
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));

    expect(screen.queryByRole('heading', { name: 'Sync finished' })).toBeNull();
  });

  it('says what a sync with nothing new checked, in seconds', async () => {
    const finished = progress({ models: { done: 1, total: 1 }, components: { done: 0, total: 0 }, driverSets: { done: 1, total: 1 }, found: {}, latest: [] }, {
      result: 'Completed', startedAt: '2026-10-26T14:02:00Z', finishedAt: '2026-10-26T14:02:02Z', counts: {}, downloadedComponents: 0, downloadedBytes: 0, newSets: 0,
    });
    serve(routes([run()], { body: finished }));
    renderSyncTab();

    expect(await screen.findByRole('heading', { name: 'Sync finished' }, { timeout: 5_000 })).toBeTruthy();
    expect(screen.getByText(/^Completed at 2026-10-26 \d\d:02, after 2 s: 1 model checked, no newer versions, nothing downloaded\.$/)).toBeTruthy();
  });

  it('lists the onboardings queued behind the running sync', async () => {
    serve(routes([run()], { body: progress({ queued: [{ modelId, modelName: 'HP EliteBook 840 G11', queuedBy: 'olle@contoso.com', queuedAt: '2026-10-26T14:05:00Z' }] }) }));
    renderSyncTab();

    expect(await screen.findByText('Up next', {}, { timeout: 5_000 })).toBeTruthy();
    expect(screen.getByText('Onboarding HP EliteBook 840 G11')).toBeTruthy();
    expect(screen.getByText(/^Queued by olle@contoso\.com at \d\d:05$/)).toBeTruthy();
  });

  it('reads the progress again every 5 seconds while the sync runs', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let reads = 0;
    serve(routes([run()], () => {
      reads++;
      return { body: progress({ models: { done: reads, total: 12 } }) };
    }));
    renderSyncTab();

    expect(await screen.findByRole('heading', { name: 'Sync running' }, { timeout: 5_000 })).toBeTruthy();
    await vi.advanceTimersByTimeAsync(5_000);

    await waitFor(() => expect(screen.getByRole('progressbar', { name: 'Models' }).getAttribute('aria-valuetext')).toBe('2 of 12 checked'));
  });

  it('starting a sync shows the card for the new run at once', async () => {
    serve({
      ...routes([], { body: progress() }),
      'POST /api/drivers/sync/runs': { status: 202, body: { runId: 'r1' } },
    });
    renderSyncTab();

    fireEvent.click(await screen.findByRole('button', { name: /^Sync$/ }));

    const card = (await screen.findByRole('heading', { name: 'Sync running' }, { timeout: 5_000 })).closest('div.rounded-xl') as HTMLElement;
    expect(within(card).getByRole('progressbar', { name: 'Models' })).toBeTruthy();
  });
});
