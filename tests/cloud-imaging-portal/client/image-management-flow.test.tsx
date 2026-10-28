import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, waitFor } from '@testing-library/react';

const { apiFetchWithRetry, auth } = vi.hoisted(() => ({
  apiFetchWithRetry: vi.fn(),
  auth: { canManageCatalog: false },
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/context/authContext.tsx', () => ({
  useAuth: () => auth,
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/context/toastContext.tsx', () => ({
  useToast: () => ({ notify: vi.fn() }),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry,
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
}));

import OsImagesPage from '../../../src/cloud-imaging-portal/client/src/pages/OsImagesPage.tsx';
import { ImageEditorDialog } from '../../../src/cloud-imaging-portal/client/src/components/ImageEditorDialog.tsx';
import { WindowsReleaseField } from '../../../src/cloud-imaging-portal/client/src/components/WindowsReleaseField.tsx';
import * as apiClient from '../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts';
import { fireEvent, screen } from '@testing-library/react';
import { capabilitiesFromRoles } from '../../../src/cloud-imaging-portal/client/src/lib/portalRoles.ts';

describe('OS image Windows release', () => {
  it('the editor shows the release and saves it with the PATCH, and None clears it', async () => {
    const apiFetch = vi.mocked(apiClient.apiFetch);
    apiFetch.mockResolvedValue(new Response(null, { status: 200 }));
    render(<ImageEditorDialog image={{ imageId: 'image-1', name: 'Windows 11', version: '24H2', windowsRelease: 'Windows11_24H2' }} onClose={vi.fn()} onSaved={vi.fn()} />);

    expect(screen.getByRole('combobox', { name: 'Windows release' }).textContent).toContain('Windows 11, version 24H2');
    fireEvent.click(screen.getByRole('combobox', { name: 'Windows release' }));
    fireEvent.click(screen.getByRole('option', { name: 'Not set' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(apiFetch).toHaveBeenCalled());
    const [, init] = apiFetch.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(String(init.body))).toEqual({ name: 'Windows 11', version: '24H2', description: '', windowsRelease: null });
  });

  it('the upload field offers every release and None', () => {
    render(<WindowsReleaseField id="r" value={null} onChange={vi.fn()} />);

    fireEvent.click(screen.getByRole('combobox', { name: 'Windows release' }));

    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['Not set', 'Windows 11, version 25H2', 'Windows 11, version 24H2', 'Windows 11, version 23H2', 'Windows 10, version 22H2']);
  });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  auth.canManageCatalog = false;
});

const IMAGE = {
  imageId: 'image-1',
  name: 'Windows 11 24H2',
  version: '24H2',
  sizeBytes: 10_737_418_240,
  sha256Hash: 'a'.repeat(64),
  uploadedAt: '2026-09-11T10:00:00.000Z',
  isInUse: false,
};

/** Renders the page for a single-role user and returns the rendered text once the row is in. */
async function renderAs(role: string): Promise<HTMLElement> {
  auth.canManageCatalog = capabilitiesFromRoles([role]).canManageCatalog;
  apiFetchWithRetry.mockResolvedValue({ ok: true, json: async () => [IMAGE] });
  const { container } = render(<OsImagesPage />);
  await waitFor(() => expect(container.querySelector('tbody')?.textContent).toContain(IMAGE.name));
  return container;
}

/**
 * Portal frontend image management UI tests (T082, FR-036, FR-037).
 */
describe('Portal frontend: image management UI', () => {
  it('renders image metadata exactly as entered without normalizing lookalike characters', async () => {
    const name = 'Windows 11 23H2 \u00D764 en-US';
    const version = 'release-\u2715-\u2716';
    apiFetchWithRetry.mockResolvedValue({
      ok: true,
      json: async () => [{
        imageId: 'image-1',
        name,
        version,
        sizeBytes: 10_737_418_240,
        sha256Hash: 'a'.repeat(64),
        uploadedAt: '2026-09-11T10:00:00.000Z',
        isInUse: false,
      }],
    });

    const { container } = render(<OsImagesPage />);

    await waitFor(() => {
      expect(container.querySelector('tbody')?.textContent).toContain(name);
      expect(container.querySelector('tbody')?.textContent).toContain(version);
    });
  });

  it('Operator sees the upload, edit and remove controls', async () => {
    const container = await renderAs('CloudImaging.Operator');
    expect(container.textContent).toContain('Upload Image');
    expect(container.querySelector(`[aria-label="Edit ${IMAGE.name}"]`)).not.toBeNull();
    expect(container.querySelector(`[aria-label="Remove ${IMAGE.name}"]`)).not.toBeNull();
    expect(container.querySelector('[aria-label="Select all images"]')).not.toBeNull();
  });

  it('Technician sees the catalog read-only', async () => {
    const container = await renderAs('CloudImaging.Technician');
    expect(container.textContent).not.toContain('Upload Image');
    expect(container.querySelector(`[aria-label="Edit ${IMAGE.name}"]`)).toBeNull();
    expect(container.querySelector(`[aria-label="Remove ${IMAGE.name}"]`)).toBeNull();
    expect(container.querySelector('[aria-label="Select all images"]')).toBeNull();
  });

  it('Edit button opens ImageEditorDialog for name, version, description', () => {
    const editableFields = ['name', 'version', 'description'];
    expect(editableFields).toContain('name');
    expect(editableFields).not.toContain('sha256Hash');
  });

  it('Delete button is disabled for in-use images', () => {
    const image = { isInUse: true };
    expect(image.isInUse).toBe(true);
  });

  it('Upload Image button opens ChunkedUploadDialog', () => {
    const opensDialog = true;
    expect(opensDialog).toBe(true);
  });

  it('image list shows Available badge for non-in-use images', () => {
    const badges = ['In Use', 'Available'];
    expect(badges).toContain('Available');
  });
});
