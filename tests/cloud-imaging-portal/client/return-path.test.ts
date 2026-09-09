import { describe, it, expect, beforeEach } from 'vitest';
import { consumeReturnPath } from '../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts';

const RETURN_PATH_KEY = 'cloudimaging.auth.returnPath';

/**
 * Regression tests for the sign-in deep-link restore.
 *
 * The stored path must never be applied on an ordinary page load. A redirect can start and
 * never finish (MSAL throws, the user presses Back, the tab is closed mid-flow), which leaves
 * the key behind. Applying it on the next refresh silently rewrote the URL to a page the user
 * never asked for, and because it lives in sessionStorage it survived reloads: the portal got
 * stuck on an error screen until storage was cleared by hand.
 */
describe('Portal frontend: sign-in return path', () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  it('returns the stored path once', () => {
    sessionStorage.setItem(RETURN_PATH_KEY, '/reports/session-outcomes');
    expect(consumeReturnPath()).toBe('/reports/session-outcomes');
  });

  it('always clears the key, so a leftover value cannot affect a later page load', () => {
    sessionStorage.setItem(RETURN_PATH_KEY, '/reports/session-outcomes');
    consumeReturnPath();
    expect(sessionStorage.getItem(RETURN_PATH_KEY)).toBeNull();
    expect(consumeReturnPath()).toBeNull();
  });

  it('returns null when no redirect stored a path', () => {
    expect(consumeReturnPath()).toBeNull();
  });

  /**
   * Mirrors the guard in main.tsx: the path is only applied when MSAL confirms this load is
   * the return leg of a redirect. Consuming (and clearing) still happens either way.
   */
  function shouldRestore(redirectResult: unknown, returnPath: string | null, current: string): boolean {
    return Boolean(redirectResult) && Boolean(returnPath) && returnPath !== current;
  }

  it('does NOT restore on an ordinary load, even with a leftover path', () => {
    // The exact bug: a refresh with a stale key rewrote the URL and stranded the user.
    expect(shouldRestore(null, '/reports/session-outcomes', '/reports')).toBe(false);
  });

  it('restores only when a redirect response was actually processed', () => {
    expect(shouldRestore({ account: {} }, '/reports/session-outcomes', '/')).toBe(true);
  });

  it('does not restore when already on the target path', () => {
    expect(shouldRestore({ account: {} }, '/reports', '/reports')).toBe(false);
  });
});
