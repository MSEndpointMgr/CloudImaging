import { describe, it, expect } from 'vitest';
import { bootImageStage, demoteFallback } from '../../../src/cloud-imaging-portal/client/src/lib/bootImageStage.ts';

describe('boot image stage', () => {
  it('a fresh upload is pre-production until promoted', () => {
    expect(bootImageStage({ isLatestPublished: false, isProduction: false })).toBe('preProduction');
  });

  it('the promoted image for an architecture is latest', () => {
    expect(bootImageStage({ isLatestPublished: true, isProduction: true })).toBe('latest');
  });

  it('a previously promoted image stays production (available for rollback)', () => {
    expect(bootImageStage({ isLatestPublished: false, isProduction: true })).toBe('production');
  });

  it('entries without the flag predate pre-production and were live', () => {
    expect(bootImageStage({ isLatestPublished: false })).toBe('production');
  });
});

describe('demote fallback', () => {
  const image = (id: string, extra: Partial<{ architecture: string; isLatestPublished: boolean; isProduction: boolean; createdAt: string; promotedAt: string | null }>) => ({
    bootImageId: id, architecture: 'x64', isLatestPublished: false, isProduction: true, createdAt: '2026-01-01T00:00:00Z', promotedAt: null, ...extra,
  });

  it('restores the most recently promoted other production image of the same architecture', () => {
    const current = image('current', { isLatestPublished: true, promotedAt: '2026-10-01T00:00:00Z' });
    const previous = image('previous', { createdAt: '2026-01-01T00:00:00Z', promotedAt: '2026-09-01T00:00:00Z' });
    const newerUpload = image('newer', { createdAt: '2026-08-01T00:00:00Z' });
    const testOnly = image('test', { isProduction: false, createdAt: '2026-10-02T00:00:00Z' });
    const arm = image('arm', { architecture: 'arm64', promotedAt: '2026-09-30T00:00:00Z' });

    expect(demoteFallback([current, previous, newerUpload, testOnly, arm], current)?.bootImageId).toBe('previous');
  });

  it('restores nothing when demoting a non-latest image', () => {
    const latest = image('latest', { isLatestPublished: true });
    const other = image('other', {});
    expect(demoteFallback([latest, other], other)).toBeUndefined();
  });
});
