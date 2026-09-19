import { describe, expect, it } from 'vitest';
import {
  formatImageVersion,
  formatOsImageInventoryName,
} from '../../../src/cloud-imaging-portal/client/src/lib/imageInventoryFormatting.ts';

describe('Image Inventory report formatting', () => {
  it('formats an OS image as name followed by its version in parentheses', () => {
    expect(formatOsImageInventoryName('Windows 11 23H2 x64 en-US', '10.0.22631.0'))
      .toBe('Windows 11 23H2 x64 en-US (10.0.22631.0)');
  });

  it('shows only the OS image name when its version is empty', () => {
    expect(formatOsImageInventoryName('Windows 11', '')).toBe('Windows 11');
    expect(formatOsImageInventoryName('Windows 11', '   ')).toBe('Windows 11 (   )');
  });

  it('preserves user-entered characters exactly when composing report rows', () => {
    expect(formatOsImageInventoryName('Windows \u00D764 \u2715', 'v\u2716Next'))
      .toBe('Windows \u00D764 \u2715 (v\u2716Next)');
    expect(formatImageVersion('release-\u00D7-\u2715-\u2716'))
      .toBe('release-\u00D7-\u2715-\u2716');
  });

  it('does not prepend a v to boot or recovery image versions', () => {
    expect(formatImageVersion('2026.09.11.3')).toBe('2026.09.11.3');
  });

  it('preserves a prefix when it is part of the stored version', () => {
    expect(formatImageVersion('vNext')).toBe('vNext');
  });
});