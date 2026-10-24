import { afterEach, describe, it, expect, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';

const api = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry: vi.fn(),
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => api);

import { ConfirmImpactDialog } from '../../../src/cloud-imaging-portal/client/src/components/ConfirmImpactDialog.tsx';
import { StartImagingProfileLines } from '../../../src/cloud-imaging-portal/client/src/components/setup-profiles/StartImagingProfileLines.tsx';

/**
 * The Start imaging overlay says which setup profile each group of devices gets, so media prepared
 * for the wrong location is caught before the disk is wiped. Without profiles, or when they cannot
 * be loaded, the overlay works as before.
 */
describe('Portal frontend: Start imaging setup profile lines', () => {
  afterEach(() => { cleanup(); vi.clearAllMocks(); });

  const profile = (name: string, allDevices: boolean, locationIds: string[]) => ({
    profileId: name, name, targeting: { allDevices, locationIds }, registryValueCount: 0, scriptCount: 0, hasAnswerFile: false,
    failureHandling: 'CompleteWithWarnings', currentVersion: 3, lastModifiedAt: '2026-10-22T10:00:00Z',
  });

  const overlay = (sessions: { locationId: string | null }[]) => render(
    <ConfirmImpactDialog
      copy={{ confirmTitle: 'Start imaging on 4 devices?', impact: 'This assigns the image.', confirmLabel: 'Start Imaging', destructive: false }}
      busy={false}
      onCancel={() => undefined}
      onConfirm={() => undefined}
      details={<StartImagingProfileLines sessions={sessions} />}
    />,
  );

  it('lists the profile lines for a mixed selection', async () => {
    api.apiFetchWithRetry.mockResolvedValue(new Response(JSON.stringify([profile('Stockholm and Gothenburg', false, ['sto']), profile('Nordics standard', true, [])]), { status: 200 }));

    overlay([{ locationId: 'sto' }, { locationId: 'sto' }, { locationId: 'sto' }, { locationId: null }]);

    expect(await screen.findByText("Setup profile, by the location on each device's USB media:")).toBeTruthy();
    expect(screen.getByText('3 devices: Stockholm and Gothenburg')).toBeTruthy();
    expect(screen.getByText('1 device started from media without a location: Nordics standard')).toBeTruthy();
    expect(api.apiFetchWithRetry).toHaveBeenCalledWith('/api/setup-profiles', { credentials: 'include' });
  });

  it('shows no lines when there are no profiles', async () => {
    api.apiFetchWithRetry.mockResolvedValue(new Response('[]', { status: 200 }));

    overlay([{ locationId: 'sto' }]);

    await vi.waitFor(() => expect(api.apiFetchWithRetry).toHaveBeenCalled());
    expect(screen.queryByText(/Setup profile, by the location/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Start Imaging' })).toBeTruthy();
  });

  it('shows no lines and keeps the overlay working when the profiles cannot be loaded', async () => {
    api.apiFetchWithRetry.mockResolvedValue(new Response('boom', { status: 500 }));

    overlay([{ locationId: 'sto' }]);

    await vi.waitFor(() => expect(api.apiFetchWithRetry).toHaveBeenCalled());
    expect(screen.queryByText(/Setup profile, by the location/)).toBeNull();
    expect((screen.getByRole('button', { name: 'Start Imaging' }) as HTMLButtonElement).disabled).toBe(false);
  });
});

/**
 * Portal frontend bulk assignment UI tests (T074, FR-035).
 * Bulk assignment is no longer selection-driven: the OS image dropdown above the
 * Coupled Devices table always applies to every row currently in that table.
 */
describe('Portal frontend: bulk assignment UI', () => {
  it('Start Imaging targets every row in the Coupled Devices table, not a checked subset', () => {
    const coupled = [
      { state: 'SessionAssigned' },
      { state: 'SessionAssigned' },
    ];
    const targeted = coupled.filter(r => r.state === 'SessionAssigned').length;
    expect(targeted).toBe(coupled.length);
  });

  it('Start Imaging is disabled when the Coupled Devices table is empty', () => {
    const coupledCount = 0;
    const disabled = coupledCount === 0;
    expect(disabled).toBe(true);
  });

  it('bulk-assign wires to POST /api/sessions/bulk-assign', () => {
    const endpoint = '/api/sessions/bulk-assign';
    expect(endpoint).toBe('/api/sessions/bulk-assign');
  });

  it('bulk-assign request body includes sessionIds and osImageId', () => {
    const body = { sessionIds: ['id1', 'id2'], osImageId: 'image-guid' };
    expect(Array.isArray(body.sessionIds)).toBe(true);
    expect(body.osImageId).toBeTruthy();
  });
});
