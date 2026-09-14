import { describe, it, expect, beforeEach, vi } from 'vitest';

/**
 * Regression tests for the interactive sign-in redirect ceiling.
 *
 * Every redirect reloads the SPA, so the in-memory de-duplication inside apiClient only ever
 * sees one attempt and can never recognise a loop that spans page loads. A deployment whose
 * portal backend rejects every token (its ENTRA_CLIENT_ID / ENTRA_TENANT_ID not matching the
 * app registration the SPA signs in against) answers 401 on the first API call of every load,
 * which restarted sign-in on every load: the browser bounced between the portal and Entra
 * until Entra's own loop protection replied "We couldn't sign you in. Please try again."
 *
 * Each iteration below re-imports the module to model a fresh page load; sessionStorage
 * deliberately survives that, which is the whole point of where the counter lives.
 */

const REDIRECT_BUDGET_KEY = 'cloudimaging.auth.redirectAttempts';
const MODULE = '../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts';

const account = { homeAccountId: 'test-account' };
const acquireTokenRedirect = vi.fn(() => new Promise<void>(() => { /* navigation never settles */ }));
const acquireTokenSilent = vi.fn(() => Promise.resolve({ accessToken: 'token' }));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/msal.ts', () => ({
  getApiScope: () => 'api://00000000-0000-0000-0000-000000000000/user_impersonation',
  getMsalInstance: () => ({
    getActiveAccount: () => account,
    getAllAccounts: () => [account],
    acquireTokenSilent,
    acquireTokenRedirect,
  }),
}));

/** Models one page load: a fresh module instance calling the API once. */
async function simulatePageLoad(status: number): Promise<typeof import('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts')> {
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ status } as Response)));
  vi.resetModules();
  const mod = await import(MODULE);
  void mod.apiFetch('/api/branding');
  // Let acquireTokenSilent and the fetch promise settle before asserting.
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));
  return mod;
}

describe('Portal frontend: sign-in redirect ceiling', () => {
  beforeEach(() => {
    sessionStorage.clear();
    acquireTokenRedirect.mockClear();
    acquireTokenSilent.mockClear();
  });

  it('still redirects for the ordinary expired-session case', async () => {
    const mod = await simulatePageLoad(401);
    expect(acquireTokenRedirect).toHaveBeenCalledTimes(1);
    expect(mod.isSignInLoopDetected()).toBe(false);
  });

  it('stops redirecting and reports a loop once the budget is spent', async () => {
    await simulatePageLoad(401);
    await simulatePageLoad(401);
    await simulatePageLoad(401);
    expect(acquireTokenRedirect).toHaveBeenCalledTimes(3);

    const mod = await simulatePageLoad(401);
    // The fourth load must not navigate to Entra again; it surfaces the failure instead.
    expect(acquireTokenRedirect).toHaveBeenCalledTimes(3);
    expect(mod.isSignInLoopDetected()).toBe(true);
  });

  it('restores the full budget once the server accepts a token', async () => {
    await simulatePageLoad(401);
    await simulatePageLoad(401);

    // A 403 proves the token validated; the user simply holds no portal role.
    await simulatePageLoad(403);
    expect(sessionStorage.getItem(REDIRECT_BUDGET_KEY)).toBeNull();

    acquireTokenRedirect.mockClear();
    await simulatePageLoad(401);
    await simulatePageLoad(401);
    await simulatePageLoad(401);
    const mod = await simulatePageLoad(401);
    expect(acquireTokenRedirect).toHaveBeenCalledTimes(3);
    expect(mod.isSignInLoopDetected()).toBe(true);
  });

  it('notifies subscribers so the UI can replace the app with an explanation', async () => {
    await simulatePageLoad(401);
    await simulatePageLoad(401);
    await simulatePageLoad(401);

    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ status: 401 } as Response)));
    vi.resetModules();
    const mod = await import(MODULE);
    const listener = vi.fn();
    mod.subscribeToSignInLoop(listener);
    void mod.apiFetch('/api/branding');
    await new Promise((resolve) => setTimeout(resolve, 0));
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(listener).toHaveBeenCalled();
  });

  it('clearRedirectBudget lets an operator retry by hand', async () => {
    await simulatePageLoad(401);
    await simulatePageLoad(401);
    await simulatePageLoad(401);
    const mod = await simulatePageLoad(401);
    expect(mod.isSignInLoopDetected()).toBe(true);

    mod.clearRedirectBudget();
    expect(sessionStorage.getItem(REDIRECT_BUDGET_KEY)).toBeNull();

    acquireTokenRedirect.mockClear();
    await simulatePageLoad(401);
    expect(acquireTokenRedirect).toHaveBeenCalledTimes(1);
  });
});
