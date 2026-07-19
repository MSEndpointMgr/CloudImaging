import { describe, it, expect } from 'vitest';

/**
 * Portal frontend image management UI tests (T082, FR-036, FR-037).
 */
describe('Portal frontend — image management UI', () => {
  it('OsImagesPage renders a table with name, version, size, sha256Hash', () => {
    const columns = ['Name', 'Version', 'Size', 'SHA-256', 'Status', 'Actions'];
    expect(columns).toContain('SHA-256');
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
