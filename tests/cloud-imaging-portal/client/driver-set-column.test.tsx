import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

const { apiFetch, notify, auth } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  notify: vi.fn(() => 'toast'),
  auth: { isAdministrator: false, canViewSetupProfiles: false, canUseImaging: true, canManageDrivers: false },
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
vi.mock('../../../src/cloud-imaging-portal/client/src/context/userPreferencesContext.tsx', () => ({ useUserPreferences: () => ({ preferredLocationId: null, preferredLocationName: null }) }));

import SessionsPage from '../../../src/cloud-imaging-portal/client/src/pages/SessionsPage.tsx';

type Handler = (init?: RequestInit) => { status?: number; body?: unknown };

const calls: { key: string; body: unknown }[] = [];

function serve(routes: Record<string, Handler | { status?: number; body?: unknown }>): void {
  apiFetch.mockImplementation(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${url}`;
    calls.push({ key, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const route = routes[key];
    if (!route) return new Response(JSON.stringify({ detail: `unexpected ${key}` }), { status: 500 });
    const { status = 200, body } = typeof route === 'function' ? route(init) : route;
    return new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
  });
}

const matched = { sessionId: '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10', state: 'SessionAssigned', deviceSerialNumber: '5CG1111', deviceManufacturer: 'HP', deviceModel: 'EliteBook 840', locationId: null, locationName: null, overallProgressPercent: 0, currentStep: null, createdAt: '2026-10-25T08:00:00Z', architecture: 'x64' };
const unmatched = { ...matched, sessionId: '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed', deviceSerialNumber: '5CG2222', deviceModel: 'ProBook 9' };
const elite = '2c9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
const zbook = '3d9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';
const images = [
  { imageId: 'img-1', name: 'Windows 11', version: '24H2 Enterprise', architecture: 'x64' },
  { imageId: 'img-2', name: 'Windows 11', version: '23H2 Enterprise', architecture: 'x64' },
];
const preview = {
  release: 'Windows11_24H2',
  architecture: 'X64',
  options: [
    { modelId: zbook, vendor: 'Hp', displayName: 'HP ZBook Firefly 14', maxDownloadBytes: 1_100_000_000 },
    { modelId: elite, vendor: 'Hp', displayName: 'HP EliteBook 840 G11', maxDownloadBytes: 2_410_000_000 },
  ],
  sessions: [
    { sessionId: matched.sessionId, matchedModelId: elite, skipReason: null, matchKey: '8C26' },
    { sessionId: unmatched.sessionId, matchedModelId: null, skipReason: 'NoMatchingModel', matchKey: '8A3B' },
  ],
};

function base(overrides: Record<string, Handler | { status?: number; body?: unknown }> = {}): Record<string, Handler | { status?: number; body?: unknown }> {
  return {
    'GET /api/images': { body: images },
    'GET /api/sessions': { body: [matched, unmatched] },
    'GET /api/preflight-overrides': { body: { overrides: [], preFlightEnabled: false } },
    'GET /api/drivers/models': { body: [{ modelId: elite, state: 'Active' }] },
    'POST /api/drivers/assignment-preview': { body: preview },
    ...overrides,
  };
}

function renderPage(): void {
  render(<MemoryRouter><SessionsPage /></MemoryRouter>);
}

async function chooseImage(label = /24H2 Enterprise/): Promise<void> {
  fireEvent.click(await screen.findByRole('combobox', { name: 'OS image to assign' }));
  fireEvent.click(screen.getByRole('option', { name: label }));
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  calls.length = 0;
  auth.canUseImaging = true;
  auth.canManageDrivers = false;
});

describe('Devices page: Driver set column, sets in testing', () => {
  const withTesting = { ...preview, options: [...preview.options, { modelId: elite, vendor: 'Hp', displayName: 'HP EliteBook 840 G11', maxDownloadBytes: 2_500_000_000, testing: true }] };

  it('Operators get the set in testing after the match, and choosing it sends a Testing choice', async () => {
    auth.canManageDrivers = true;
    serve(base({
      'POST /api/drivers/assignment-preview': { body: withTesting },
      'POST /api/sessions/bulk-assign': { body: { assigned: 2, skipped: 0, assignedIds: [], skippedIds: [] } },
    }));
    renderPage();
    await chooseImage();

    fireEvent.click(await screen.findByRole('combobox', { name: 'Driver set for 5CG1111' }));
    expect(screen.getAllByRole('option').map(o => o.getAttribute('title'))).toEqual(['HP EliteBook 840 G11', 'HP EliteBook 840 G11 (testing)', 'HP ZBook Firefly 14', 'No drivers']);
    fireEvent.click(screen.getByRole('option', { name: /^HP EliteBook 840 G11 \(testing\)/ }));

    fireEvent.click(screen.getByRole('button', { name: /Start Imaging \(2\)/ }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getAllByRole('listitem').map(li => li.textContent)).toContain('1 device: HP EliteBook 840 G11 (testing) (chosen manually)');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Start Imaging' }));

    await waitFor(() => expect(calls.some(c => c.key === 'POST /api/sessions/bulk-assign')).toBe(true));
    expect(calls.find(c => c.key === 'POST /api/sessions/bulk-assign')?.body).toEqual({
      sessionIds: [matched.sessionId, unmatched.sessionId],
      osImageId: 'img-1',
      driverChoices: [{ sessionId: matched.sessionId, kind: 'Testing', modelId: elite }],
    });
  });

  it('Technicians do not get sets in testing', async () => {
    serve(base({ 'POST /api/drivers/assignment-preview': { body: withTesting } }));
    renderPage();
    await chooseImage();

    fireEvent.click(await screen.findByRole('combobox', { name: 'Driver set for 5CG1111' }));
    expect(screen.getAllByRole('option').map(o => o.getAttribute('title'))).toEqual(['HP EliteBook 840 G11', 'HP ZBook Firefly 14', 'No drivers']);
  });

  it('a device whose model only has a set in testing points Operators at it, and Technicians at the plain reason', async () => {
    const onlyTesting = {
      ...preview,
      options: [preview.options[1], { modelId: zbook, vendor: 'Hp', displayName: 'HP ZBook Firefly 14', maxDownloadBytes: 1_000_000_000, testing: true }],
      sessions: [preview.sessions[0], { sessionId: unmatched.sessionId, matchedModelId: null, testingModelId: zbook, skipReason: 'NoDriverSet', matchKey: '8A3B' }],
    };
    auth.canManageDrivers = true;
    serve(base({ 'POST /api/drivers/assignment-preview': { body: onlyTesting } }));
    renderPage();
    await chooseImage();

    expect(await screen.findByLabelText('The driver set of HP ZBook Firefly 14 for Windows 11, version 24H2 is in testing. Choose HP ZBook Firefly 14 (testing) to use it.')).toBeTruthy();
    fireEvent.click(screen.getByRole('combobox', { name: 'Driver set for 5CG2222' }));
    expect(screen.getAllByRole('option').map(o => o.getAttribute('title'))[0]).toBe('HP ZBook Firefly 14 (testing)');
    cleanup();

    auth.canManageDrivers = false;
    serve(base({ 'POST /api/drivers/assignment-preview': { body: onlyTesting } }));
    renderPage();
    await chooseImage();

    expect(await screen.findByLabelText('This model has no driver set for Windows 11, version 24H2 yet.')).toBeTruthy();
  });
});

describe('Devices page: Driver set column', () => {
  it('is hidden when no model is onboarded', async () => {
    serve(base({ 'GET /api/drivers/models': { body: [] } }));
    renderPage();

    await screen.findByText('5CG1111');
    expect(screen.queryByRole('columnheader', { name: 'Driver set' })).toBeNull();
  });

  it('asks for an OS image first, then preselects the match and flags a device without one', async () => {
    serve(base());
    renderPage();

    expect(await screen.findByRole('columnheader', { name: 'Driver set' })).toBeTruthy();
    expect(screen.getAllByText('Choose an OS image first').length).toBe(2);
    await chooseImage();

    const matchedSelect = await screen.findByRole('combobox', { name: 'Driver set for 5CG1111' });
    expect(matchedSelect.textContent).toContain('HP EliteBook 840 G11');
    const unmatchedSelect = screen.getByRole('combobox', { name: 'Driver set for 5CG2222' });
    expect(unmatchedSelect.textContent).toContain('No match, choose a set');
    expect(unmatchedSelect.className).toContain('border-amber-500');
    const alert = screen.getByLabelText('System ID 8A3B is not onboarded for Windows 11, version 24H2.');
    expect(alert.getAttribute('tabindex')).toBe('0');
    expect(calls.find(c => c.key === 'POST /api/drivers/assignment-preview')?.body).toEqual({ osImageId: 'img-1', sessionIds: [matched.sessionId, unmatched.sessionId] });
  });

  it('groups the options by vendor with the match first and sizes, No drivers last, and no set versions', async () => {
    serve(base());
    renderPage();
    await chooseImage();

    fireEvent.click(await screen.findByRole('combobox', { name: 'Driver set for 5CG1111' }));

    expect(screen.getAllByRole('group').map(g => g.getAttribute('aria-label'))).toEqual(['HP']);
    expect(screen.getAllByRole('option').map(o => o.getAttribute('title'))).toEqual(['HP EliteBook 840 G11', 'HP ZBook Firefly 14', 'No drivers']);
    expect(screen.getByText('2.41 GB')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/Set \d/);
  });

  it('a change shows the undo button, which restores the match, and choices are cleared when the image changes', async () => {
    serve(base());
    renderPage();
    await chooseImage();

    fireEvent.click(await screen.findByRole('combobox', { name: 'Driver set for 5CG1111' }));
    fireEvent.click(screen.getByRole('option', { name: 'No drivers' }));
    const undo = screen.getByRole('button', { name: 'Use the matched set' });
    fireEvent.click(undo);
    expect(screen.getByRole('combobox', { name: 'Driver set for 5CG1111' }).textContent).toContain('HP EliteBook 840 G11');
    expect(screen.queryByRole('button', { name: 'Use the matched set' })).toBeNull();

    fireEvent.click(screen.getByRole('combobox', { name: 'Driver set for 5CG2222' }));
    fireEvent.click(screen.getByRole('option', { name: /^HP ZBook Firefly 14/ }));
    expect(screen.getByRole('button', { name: 'Use the matched set' })).toBeTruthy();
    await chooseImage(/23H2 Enterprise/);
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Driver set for 5CG2222' }).textContent).toContain('No match, choose a set'));
  });

  it('sends only the rows that differ from the match, and the overlay lists the driver lines', async () => {
    serve(base({ 'POST /api/sessions/bulk-assign': { body: { assigned: 2, skipped: 0, assignedIds: [], skippedIds: [] } } }));
    renderPage();
    await chooseImage();
    fireEvent.click(await screen.findByRole('combobox', { name: 'Driver set for 5CG2222' }));
    fireEvent.click(screen.getByRole('option', { name: /^HP EliteBook 840 G11/ }));

    fireEvent.click(screen.getByRole('button', { name: /Start Imaging \(2\)/ }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText('Driver set:')).toBeTruthy();
    const lines = within(dialog).getAllByRole('listitem').map(li => li.textContent);
    expect(lines).toEqual(['1 device: HP EliteBook 840 G11', '1 device: HP EliteBook 840 G11 (chosen manually)']);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Start Imaging' }));

    await waitFor(() => expect(calls.some(c => c.key === 'POST /api/sessions/bulk-assign')).toBe(true));
    expect(calls.find(c => c.key === 'POST /api/sessions/bulk-assign')?.body).toEqual({
      sessionIds: [matched.sessionId, unmatched.sessionId],
      osImageId: 'img-1',
      driverChoices: [{ sessionId: unmatched.sessionId, kind: 'Model', modelId: elite }],
    });
  });

  it('a 409 shows the detail, reads the preview again and returns the affected rows to their match', async () => {
    serve(base({
      'POST /api/sessions/bulk-assign': { status: 409, body: { detail: 'The chosen driver model is no longer available for 1 device. Choose again.', errors: [{ sessionId: unmatched.sessionId, reason: 'The chosen driver model was removed.' }] } },
    }));
    renderPage();
    await chooseImage();
    fireEvent.click(await screen.findByRole('combobox', { name: 'Driver set for 5CG2222' }));
    fireEvent.click(screen.getByRole('option', { name: /^HP ZBook Firefly 14/ }));
    const previews = () => calls.filter(c => c.key === 'POST /api/drivers/assignment-preview').length;
    const before = previews();

    fireEvent.click(screen.getByRole('button', { name: /Start Imaging \(2\)/ }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Start Imaging' }));

    await waitFor(() => expect(notify).toHaveBeenCalledWith(expect.objectContaining({ status: 'error', description: 'The chosen driver model is no longer available for 1 device. Choose again.' })));
    await waitFor(() => expect(previews()).toBeGreaterThan(before));
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Driver set for 5CG2222' }).textContent).toContain('No match, choose a set'));
  });

  it('a preview error offers Retry, and Start imaging still works without choices', async () => {
    serve(base({
      'POST /api/drivers/assignment-preview': { status: 500, body: { detail: 'boom' } },
      'POST /api/sessions/bulk-assign': { body: { assigned: 2, skipped: 0, assignedIds: [], skippedIds: [] } },
    }));
    renderPage();
    await chooseImage();

    expect((await screen.findAllByText('Driver sets could not be loaded')).length).toBe(2);
    expect(screen.getAllByRole('button', { name: 'Retry' }).length).toBe(2);
    fireEvent.click(screen.getByRole('button', { name: /Start Imaging \(2\)/ }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Start Imaging' }));

    await waitFor(() => expect(calls.find(c => c.key === 'POST /api/sessions/bulk-assign')?.body).toEqual({ sessionIds: [matched.sessionId, unmatched.sessionId], osImageId: 'img-1' }));
  });

  it('says so when the OS image has no Windows release', async () => {
    serve(base({ 'POST /api/drivers/assignment-preview': { body: { ...preview, release: null, options: [], sessions: preview.sessions.map(s => ({ ...s, matchedModelId: null, skipReason: 'NoWindowsRelease' })) } } }));
    renderPage();
    await chooseImage();

    expect((await screen.findAllByText('The OS image has no Windows release')).length).toBe(2);
  });

  it('is not shown to users who cannot start imaging', async () => {
    auth.canUseImaging = false;
    serve(base());
    renderPage();

    await screen.findByText('5CG1111');
    expect(screen.queryByRole('columnheader', { name: 'Driver set' })).toBeNull();
  });
});
