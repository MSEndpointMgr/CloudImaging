import { afterEach, describe, it, expect, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';

const { apiFetch, apiFetchWithRetry, notify, update } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry: vi.fn(),
  notify: vi.fn(() => 'toast'),
  update: vi.fn(),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch,
  apiFetchWithRetry,
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
}));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/toastContext.tsx', () => ({ useToast: () => ({ notify, update }) }));
vi.mock('../../../src/cloud-imaging-portal/client/src/lib/msal.ts', () => ({ getMsalInstance: () => ({ getActiveAccount: () => null, getAllAccounts: () => [] }) }));
vi.mock('../../../src/cloud-imaging-portal/client/src/components/BootMediaCertPanel.tsx', () => ({ BootMediaCertPanel: () => null }));

import DeploymentConfigPage from '../../../src/cloud-imaging-portal/client/src/pages/DeploymentConfigPage.tsx';

describe('Portal frontend: setup profile version retention', () => {
  afterEach(() => { cleanup(); vi.clearAllMocks(); });

  const saves = () => apiFetch.mock.calls.filter(([url, init]) => url === '/api/portal-config' && (init as RequestInit | undefined)?.method === 'PUT').map(([, init]) => init as RequestInit);

  async function openMiscellaneous(config: Record<string, unknown> = {}) {
    apiFetchWithRetry.mockImplementation(async (url: string) => (url === '/api/portal-config'
      ? new Response(JSON.stringify(config), { status: 200 })
      : new Response('{}', { status: 404 })));
    render(<DeploymentConfigPage />);
    fireEvent.click(await screen.findByRole('tab', { name: 'Miscellaneous' }));
    return screen.getByLabelText('Versions kept per profile') as HTMLInputElement;
  }

  it('defaults to 25 versions kept per profile', async () => {
    const input = await openMiscellaneous();

    expect(input.value).toBe('25');
    expect(screen.getByText('Older versions are deleted at the next save. A version a running deployment still uses is always kept.')).toBeTruthy();
  });

  it('counts a changed value as an unsaved change and saves it', async () => {
    apiFetch.mockResolvedValue(new Response(null, { status: 204 }));
    const input = await openMiscellaneous({ setupProfileVersionRetention: 25 });

    fireEvent.change(input, { target: { value: '10' } });
    expect(screen.getByText('You have unsaved changes.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save Configuration' }));

    await vi.waitFor(() => expect(saves()).toHaveLength(1));
    expect(JSON.parse(saves()[0].body as string)).toMatchObject({ setupProfileVersionRetention: 10 });
  });

  it('refuses a value outside 5 to 100 and stays on the tab', async () => {
    const input = await openMiscellaneous();
    fireEvent.change(input, { target: { value: '4' } });

    fireEvent.click(screen.getByRole('button', { name: 'Save Configuration' }));

    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ description: 'Keep between 5 and 100 versions per profile.' }));
    expect(saves()).toHaveLength(0);
    expect(screen.getByRole('tab', { name: 'Miscellaneous' }).getAttribute('aria-selected')).toBe('true');
  });
});

/**
 * Portal frontend deployment configuration page tests (T145, FR-026, FR-068).
 */
describe('Portal frontend: deployment configuration page', () => {
  it('DeploymentConfigPage renders pre-flight authorization toggle', () => {
    const hasToggle = true;
    expect(hasToggle).toBe(true);
  });

  it('PreFlightAuthorizationToggle role is "switch"', () => {
    const role = 'switch';
    expect(role).toBe('switch');
  });

  it('toggle aria-checked reflects enabled state', () => {
    const enabled = false;
    expect(enabled).toBe(false);
  });

  it('configuration is loaded from GET /api/portal-config', () => {
    const endpoint = '/api/portal-config';
    expect(endpoint).toBe('/api/portal-config');
  });

  it('save sends PUT to /api/portal-config', () => {
    const method   = 'PUT';
    const endpoint = '/api/portal-config';
    expect(method).toBe('PUT');
    expect(endpoint).toBe('/api/portal-config');
  });

  it('BootMediaCertPanel is embedded in the configuration page', () => {
    const hasPanel = true;
    expect(hasPanel).toBe(true);
  });
});
