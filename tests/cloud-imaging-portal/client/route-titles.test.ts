import { describe, it, expect } from 'vitest';
import { resolveSectionTitle } from '../../../src/cloud-imaging-portal/client/src/lib/routeTitles.ts';

/**
 * The app bar heading and the browser tab title are both derived from this map, so a route that
 * resolves to the wrong section is visible in two places at once. An earlier duplicate of this
 * map lived inside `Header` and had silently lost `/reports`, which is why it is now shared.
 */
describe('Portal frontend: route section titles', () => {
  it('resolves each top-level section', () => {
    expect(resolveSectionTitle('/')).toBe('Dashboard');
    expect(resolveSectionTitle('/sessions')).toBe('Devices');
    expect(resolveSectionTitle('/os-images')).toBe('OS Images');
    expect(resolveSectionTitle('/boot-images')).toBe('Boot Images');
    expect(resolveSectionTitle('/recovery-images')).toBe('Recovery Images');
    expect(resolveSectionTitle('/reports')).toBe('Reports');
    expect(resolveSectionTitle('/locations')).toBe('Locations');
    expect(resolveSectionTitle('/branding')).toBe('Branding');
    expect(resolveSectionTitle('/configuration')).toBe('Configuration');
  });

  it('lets nested routes inherit their parent section', () => {
    expect(resolveSectionTitle('/reports/session-outcomes')).toBe('Reports');
    expect(resolveSectionTitle('/reports/failures/abc-123')).toBe('Reports');
  });

  it('does not treat "/" as a prefix of every route', () => {
    expect(resolveSectionTitle('/nope')).toBeNull();
  });

  it('only matches on segment boundaries', () => {
    // A bare `startsWith` would hand these to Reports / Locations.
    expect(resolveSectionTitle('/reports-archive')).toBeNull();
    expect(resolveSectionTitle('/locations-legacy')).toBeNull();
  });

  it('returns null for an unknown route so the caller can fall back to the application name', () => {
    expect(resolveSectionTitle('/totally-unknown')).toBeNull();
    expect(resolveSectionTitle('')).toBeNull();
  });
});
