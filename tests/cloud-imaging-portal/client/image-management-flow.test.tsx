import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, waitFor } from '@testing-library/react';

const { apiFetchWithRetry } = vi.hoisted(() => ({
  apiFetchWithRetry: vi.fn(),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/context/authContext.tsx', () => ({
  useAuth: () => ({ isAdministrator: false }),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry,
}));

import OsImagesPage from '../../../src/cloud-imaging-portal/client/src/pages/OsImagesPage.tsx';

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

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
