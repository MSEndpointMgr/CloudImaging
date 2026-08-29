import { describe, it, expect } from 'vitest';
import {
  OS_IMAGE_EXTENSIONS,
  WIM_ONLY_EXTENSIONS,
  getFileExtension,
  isAllowedImageFile,
} from '@/utils/imageFileValidation.js';

/**
 * Portal-server-side (fast-fail, defense-in-depth) counterpart to the client's
 * imageFileValidation.ts. OS images may be uploaded as either .wim or .iso — Imaging Core API
 * extracts sources\install.wim/install.esd from an uploaded ISO at publish time. Boot and
 * recovery images have no coherent standalone ISO form and so are restricted to .wim only.
 */
describe('Portal backend: imageFileValidation', () => {
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
    expect(isAllowedImageFile('recovery.iso', WIM_ONLY_EXTENSIONS)).toBe(false);
  });
});
