import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

const { apiFetch } = vi.hoisted(() => ({ apiFetch: vi.fn() }));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch,
  apiFetchWithRetry: apiFetch,
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
  getApiAccessToken: vi.fn(async () => null),
}));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/authContext.tsx', () => ({
  useAuth: () => ({ isAdministrator: false, canViewSetupProfiles: false, canUseImaging: false, canManageDrivers: false }),
}));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/toastContext.tsx', () => ({ useToast: () => ({ notify: vi.fn(), update: vi.fn(), dismiss: vi.fn() }) }));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/userPreferencesContext.tsx', () => ({ useUserPreferences: () => ({ preferredLocationId: null, preferredLocationName: null }) }));

import SessionsPage from '../../../src/cloud-imaging-portal/client/src/pages/SessionsPage.tsx';
import { formatDateTime } from '../../../src/cloud-imaging-portal/client/src/lib/utils.ts';

const step = (stepName: string, startedAt: string, completedAt: string, warningCount = 0) => ({ stepName, status: 'Completed', startedAt, completedAt, warningCount });

// Registered hours before imaging started: Started must come from the first step, not createdAt.
const clean = {
  sessionId: '0b4f8a52-3c1d-4f6e-9a1b-7c2d5e8f9a10',
  state: 'SessionCompleted',
  deviceSerialNumber: '5CG6235B45',
  deviceManufacturer: 'HP',
  deviceModel: 'EliteBook X G2a',
  locationId: 'loc-1',
  locationName: 'Stockholm',
  overallProgressPercent: 100,
  currentStep: 'ApplyRecoveryImage',
  createdAt: '2026-11-22T20:00:00Z',
  terminalAt: '2026-11-23T00:26:55Z',
  steps: [
    step('ApplyImage', '2026-11-23T00:08:10Z', '2026-11-23T00:20:00Z'),
    step('FormatDisk', '2026-11-23T00:02:04Z', '2026-11-23T00:03:00Z'),
  ],
};
const warned = {
  ...clean,
  sessionId: '1c5f9b63-4d2e-4a7f-8b2c-8d3e6f9a0b21',
  deviceSerialNumber: '8F3K2Y3',
  deviceManufacturer: 'Dell Inc.',
  deviceModel: 'Latitude 5450',
  locationId: null,
  locationName: null,
  createdAt: '2026-11-22T15:00:00Z',
  terminalAt: '2026-11-22T17:17:30Z',
  steps: [
    step('FormatDisk', '2026-11-22T16:05:00Z', '2026-11-22T16:06:00Z'),
    step('InstallDrivers', '2026-11-22T16:30:00Z', '2026-11-22T16:50:00Z', 2),
  ],
};
// Not a completed session, so not on the Success tab.
const running = { ...clean, sessionId: '2d6a0c74-5e3f-4b8a-9c3d-9e4f7a0b1c32', deviceSerialNumber: 'PF4XK2LM', state: 'SessionInProgress', terminalAt: null };

function serve(): void {
  apiFetch.mockImplementation(async (url: string) => {
    const routes: Record<string, unknown> = {
      '/api/images': [],
      '/api/sessions': [clean, warned, running],
      '/api/preflight-overrides': { overrides: [], preFlightEnabled: false },
    };
    return url in routes
      ? new Response(JSON.stringify(routes[url]), { status: 200, headers: { 'Content-Type': 'application/json' } })
      : new Response(null, { status: 404 });
  });
}

async function openSuccessTab(): Promise<HTMLElement[]> {
  render(<MemoryRouter><SessionsPage /></MemoryRouter>);
  fireEvent.click(await screen.findByRole('button', { name: /^Success/ }));
  await screen.findByText('5CG6235B45');
  return screen.getAllByRole('row').slice(1);
}

const serials = (): string[] => screen.getAllByRole('row').slice(1).map(row => within(row).getAllByRole('cell')[1].textContent ?? '');

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('Devices page: Success tab', () => {
  it('shows when imaging started and finished, how long it took and the state of every row', async () => {
    serve();
    const [first, second] = await openSuccessTab();

    expect(screen.getAllByRole('columnheader').map(head => head.textContent)).toEqual(['Details', 'Serial', 'Model', 'Location', 'State', 'Started', 'Finished', 'Duration']);
    expect(screen.queryByText('Progress')).toBeNull();
    expect(screen.queryByText('PF4XK2LM')).toBeNull();

    // Newest finish first.
    const cells = within(first).getAllByRole('cell').map(cell => cell.textContent);
    expect(cells.slice(1)).toEqual(['5CG6235B45', 'HP EliteBook X G2a', 'Stockholm', 'Completed', formatDateTime('2026-11-23T00:02:04Z'), formatDateTime('2026-11-23T00:26:55Z'), '24m 51s']);
    expect(first.querySelector('time[datetime="2026-11-23T00:02:04Z"]')).not.toBeNull();
    expect(first.querySelector(`time[datetime="${clean.createdAt}"]`)).toBeNull();

    const warnedCells = within(second).getAllByRole('cell').map(cell => cell.textContent);
    expect(warnedCells.slice(3)).toEqual(['-', 'With warnings', formatDateTime('2026-11-22T16:05:00Z'), formatDateTime('2026-11-22T17:17:30Z'), '1h 12m']);
  });

  it('names the steps whose items failed in the warning badge tooltip', async () => {
    serve();
    await openSuccessTab();

    fireEvent.focus(screen.getByText('With warnings'));
    expect(screen.getByRole('tooltip').textContent).toBe('2 items failed in Install drivers.');
  });

  it('sorts by duration, state and start', async () => {
    serve();
    await openSuccessTab();
    expect(serials()).toEqual(['5CG6235B45', '8F3K2Y3']);

    fireEvent.click(screen.getByRole('button', { name: /^Duration/ }));
    fireEvent.click(screen.getByRole('button', { name: /^Duration/ }));
    expect(serials()).toEqual(['8F3K2Y3', '5CG6235B45']);

    fireEvent.click(screen.getByRole('button', { name: /^State/ }));
    expect(serials()).toEqual(['5CG6235B45', '8F3K2Y3']);

    fireEvent.click(screen.getByRole('button', { name: /^Started/ }));
    expect(serials()).toEqual(['8F3K2Y3', '5CG6235B45']);
  });

  it('expands a row from the chevron in the first column', async () => {
    serve();
    await openSuccessTab();

    const toggle = screen.getByRole('button', { name: 'Show progress details for 5CG6235B45' });
    expect(toggle.closest('td')?.className).toContain('pr-0');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');

    fireEvent.click(toggle);
    const hide = screen.getByRole('button', { name: 'Hide progress details for 5CG6235B45' });
    expect(hide.getAttribute('aria-expanded')).toBe('true');
    expect(document.getElementById(hide.getAttribute('aria-controls') ?? '')).not.toBeNull();
  });
});
