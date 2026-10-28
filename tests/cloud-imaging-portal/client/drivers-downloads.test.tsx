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

import DriversPage from '../../../src/cloud-imaging-portal/client/src/pages/DriversPage.tsx';
import { DOWNLOADS_POLL_MS } from '../../../src/cloud-imaging-portal/client/src/components/drivers/DownloadsTab.tsx';

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

const inProgress = {
  modelId, modelName: 'HP EliteBook 840 G11', release: 'Windows11_24H2', setVersion: 54, components: 31, componentsDone: 12,
  bytes: 2_500_000_000, bytesDone: 1_100_000_000, status: 'Downloading', failed: 0, reasons: [], startedAt: '2026-10-25T02:00:00Z',
};
const failed = { ...inProgress, modelId: 'b', modelName: 'HP ProBook 450 G10', setVersion: 12, status: 'Failed', failed: 1, reasons: ['Realtek Audio Driver: The file does not match its checksum.'] };
const finished = { modelId, modelName: 'HP EliteBook 840 G11', release: 'Windows11_24H2', setVersion: 53, newComponents: 4, reusedComponents: 27, sizeBytes: 2_410_000_000, readyAt: '2026-10-22T02:41:00' };

function renderDownloads(): void {
  render(
    <MemoryRouter initialEntries={['/drivers?tab=downloads']}>
      <Routes><Route path="/drivers" element={<DriversPage />} /></Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  vi.useRealTimers();
  auth.canManageDrivers = true;
});

describe('Drivers: Downloads tab', () => {
  it('shows the in-progress count on the tab, progress with component counts and sizes, status with the failure reason, and finished sets', async () => {
    serve({ 'GET /api/drivers/downloads': { body: { inProgress: [inProgress, failed], finished: [finished] } } });

    renderDownloads();

    await screen.findByText('In progress');
    const tab = screen.getByRole('tab', { name: /Downloads/ });
    await waitFor(() => expect(within(tab).getByText('2')).toBeTruthy());
    expect(tab.textContent).toContain('in progress');
    expect(screen.getByText('Files queued by completing onboarding, an action change, a sync or a check for updates. A new driver set reaches devices only when all its components are downloaded.')).toBeTruthy();
    const bars = screen.getAllByRole('progressbar');
    expect(bars[0].getAttribute('aria-valuetext')).toBe('12 of 31 components');
    expect(screen.getAllByText('12 of 31 components').length).toBe(2);
    expect(screen.getAllByText('1.1 of 2.5 GB').length).toBe(2);
    expect(screen.getByText('Set 54')).toBeTruthy();
    expect(screen.getByText('Downloading')).toBeTruthy();
    expect(screen.getByText('1 failed')).toBeTruthy();
    expect(screen.getByLabelText('Realtek Audio Driver: The file does not match its checksum.')).toBeTruthy();
    expect(screen.getByText('Finished in the last 7 days')).toBeTruthy();
    expect(screen.getByText('4 new, 27 already stored')).toBeTruthy();
    expect(screen.getByText('Ready')).toBeTruthy();
    expect(screen.getByText('2026-10-22 02:41')).toBeTruthy();
  });

  it('keeps the table header and shows the empty state when nothing is in progress', async () => {
    serve({ 'GET /api/drivers/downloads': { body: { inProgress: [], finished: [] } } });

    renderDownloads();

    expect(await screen.findByText('No downloads in progress')).toBeTruthy();
    expect(screen.getByText('Every driver set is ready. Downloads start after completing onboarding, an action change or a sync.')).toBeTruthy();
    expect(screen.getAllByRole('columnheader').map(h => h.textContent)).toEqual(['Model', 'Windows', 'Driver set', 'Progress', 'Size', 'Status', 'Started']);
    expect(within(screen.getByRole('tab', { name: /Downloads/ })).queryByText('0')).toBeNull();
  });

  it('polls while something is in progress', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let calls = 0;
    serve({ 'GET /api/drivers/downloads': () => { calls += 1; return { body: { inProgress: calls < 3 ? [inProgress] : [], finished: [] } }; } });

    renderDownloads();
    await screen.findByText('Set 54');
    const before = calls;

    await act(async () => { await vi.advanceTimersByTimeAsync(DOWNLOADS_POLL_MS); });

    await waitFor(() => expect(calls).toBeGreaterThan(before));
  });
});
