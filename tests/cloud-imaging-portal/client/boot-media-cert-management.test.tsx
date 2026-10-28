import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';

const { apiFetch } = vi.hoisted(() => ({
  apiFetch: vi.fn(async () => new Response(null, { status: 204 })),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({ apiFetch }));

vi.mock('../../../src/cloud-imaging-portal/client/src/context/toastContext.tsx', () => ({
  useToast: () => ({ notify: vi.fn(() => 'toast'), update: vi.fn(), dismiss: vi.fn() }),
}));

import { BootMediaCertPanel, type CertMetadata } from '../../../src/cloud-imaging-portal/client/src/components/BootMediaCertPanel.tsx';

const ACTIVE: CertMetadata = {
  thumbprintDisplay: 'AABBCCDDEEFF00112233445566778899AABBCCDD',
  issuedAt: '2026-01-01T00:00:00Z',
  expiresAt: '2028-01-01T00:00:00Z',
  isActive: true,
};

const inDays = (days: number) => new Date(Date.now() + days * 86_400_000).toISOString();

afterEach(() => {
  cleanup();
  apiFetch.mockClear();
});

function lastRequest(): { path: string; body: unknown } {
  const [path, init] = apiFetch.mock.calls.at(-1) as unknown as [string, RequestInit];
  return { path, body: JSON.parse(init.body as string) };
}

describe('BootMediaCertPanel', () => {
  it('offers only Generate when no certificate exists', () => {
    render(<BootMediaCertPanel certMeta={null} onCertChanged={vi.fn()} />);

    expect(screen.getByRole('button', { name: /Generate/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Rotate/ })).toBeNull();
  });

  it('offers only Rotate once a certificate exists', () => {
    render(<BootMediaCertPanel certMeta={ACTIVE} onCertChanged={vi.fn()} />);

    expect(screen.getByRole('button', { name: /Rotate/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Generate|Regenerate/ })).toBeNull();
  });

  it('rotates with a 14 day overlap by default', async () => {
    const onCertChanged = vi.fn();
    render(<BootMediaCertPanel certMeta={ACTIVE} onCertChanged={onCertChanged} />);

    fireEvent.click(screen.getByRole('button', { name: /Rotate/ }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText(/keeps working for 14 days/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Rotate' }));

    await waitFor(() => expect(onCertChanged).toHaveBeenCalled());
    expect(lastRequest()).toEqual({ path: '/api/cert/rotate', body: { confirmed: true, previousAcceptedDays: 14 } });
  });

  it('rotates without an overlap when Stop immediately is chosen', async () => {
    render(<BootMediaCertPanel certMeta={ACTIVE} onCertChanged={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: /Rotate/ }));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('combobox'));
    fireEvent.click(screen.getByRole('option', { name: /Stop immediately/ }));
    expect(within(dialog).getByText(/stops working immediately/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Rotate' }));

    await waitFor(() => expect(apiFetch).toHaveBeenCalled());
    expect(lastRequest().body).toEqual({ confirmed: true, previousAcceptedDays: 0 });
  });

  it('shows a still-accepted previous certificate and stops accepting it after confirmation', async () => {
    const acceptedUntil = inDays(10);
    render(<BootMediaCertPanel certMeta={{ ...ACTIVE, previous: { thumbprintDisplay: '0011', acceptedUntil } }} onCertChanged={vi.fn()} />);

    expect(screen.getByText('Previous certificate still accepted')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Stop accepting/ }));
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Stop accepting' }));

    await waitFor(() => expect(apiFetch).toHaveBeenCalled());
    expect(lastRequest().path).toBe('/api/cert/previous/stop');
  });

  it('hides a previous certificate whose overlap has ended', () => {
    render(<BootMediaCertPanel certMeta={{ ...ACTIVE, previous: { thumbprintDisplay: '0011', acceptedUntil: inDays(-1) } }} onCertChanged={vi.fn()} />);

    expect(screen.queryByText('Previous certificate still accepted')).toBeNull();
  });
});
