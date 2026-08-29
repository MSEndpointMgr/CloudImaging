import { describe, it, expect } from 'vitest';
import {
  OS_IMAGE_EXTENSIONS,
  WIM_ONLY_EXTENSIONS,
  fileAccept,
  getFileExtension,
  isAllowedImageFile,
  validateImageFile,
} from '../../../src/cloud-imaging-portal/client/src/lib/imageFileValidation.ts';

/**
 * OS images may be uploaded as either .wim or .iso — Imaging Core API extracts
 * sources\install.wim/install.esd from an uploaded ISO at publish time. Boot and recovery images
 * have no coherent standalone ISO form (a boot image is the WinPE boot.wim Media Builder/
 * self-update replace in place; a recovery image is a standalone Winre.wim pulled from a
 * reference machine) and so are restricted to .wim only.
 */
describe('imageFileValidation', () => {
  it('getFileExtension returns the lowercase extension including the dot', () => {
    expect(getFileExtension('install.WIM')).toBe('.wim');
    expect(getFileExtension('media.iso')).toBe('.iso');
    expect(getFileExtension('noextension')).toBe('');
  });

  it('OS images accept both .wim and .iso', () => {
    expect(isAllowedImageFile('install.wim', OS_IMAGE_EXTENSIONS)).toBe(true);
    expect(isAllowedImageFile('media.iso', OS_IMAGE_EXTENSIONS)).toBe(true);
    expect(isAllowedImageFile('malware.exe', OS_IMAGE_EXTENSIONS)).toBe(false);
  });

  it('boot/recovery images accept only .wim, rejecting .iso', () => {
    expect(isAllowedImageFile('boot.wim', WIM_ONLY_EXTENSIONS)).toBe(true);
    expect(isAllowedImageFile('boot.iso', WIM_ONLY_EXTENSIONS)).toBe(false);
  });

  it('validateImageFile returns an error message naming the allowed extensions', () => {
    const file = new File([new Uint8Array(0)], 'media.iso');
    const error = validateImageFile(file, WIM_ONLY_EXTENSIONS);
    expect(error).toContain('.wim');
    expect(error).not.toBeNull();
  });

  it('validateImageFile returns null for an allowed file', () => {
    const file = new File([new Uint8Array(0)], 'boot.wim');
    expect(validateImageFile(file, WIM_ONLY_EXTENSIONS)).toBeNull();
  });

  it('fileAccept joins the allowed extensions for the file-picker accept attribute', () => {
    expect(fileAccept(WIM_ONLY_EXTENSIONS)).toBe('.wim');
    expect(fileAccept(OS_IMAGE_EXTENSIONS)).toBe('.iso,.wim');
  });
});
