import { PublicClientApplication } from '@azure/msal-browser';
import { getRuntimeConfig, getApiScope } from './runtimeConfig.ts';

/**
 * Shared MSAL instance used by both the React tree ({@link main.tsx} via
 * MsalProvider) and the non-React API client ({@link apiClient.ts}). Centralising
 * it here ensures token acquisition for API calls uses the same cache and account
 * state as interactive sign-in.
 *
 * The instance is constructed at runtime from configuration fetched during
 * bootstrap ({@link loadRuntimeConfig}) rather than build-time Vite env, so a
 * single prebuilt SPA bundle works for any tenant (FR-040b, FR-041, FR-067: no
 * hardcoded credentials).
 */
let instance: PublicClientApplication | null = null;

/** Creates the shared MSAL instance from loaded runtime config. Call once, after {@link loadRuntimeConfig}. */
export function createMsalInstance(): PublicClientApplication {
  const cfg = getRuntimeConfig();
  instance = new PublicClientApplication({
    auth: {
      clientId: cfg.clientId,
      authority: cfg.authority,
      redirectUri: window.location.origin,
    },
    cache: {
      cacheLocation: 'sessionStorage',
    },
  });
  return instance;
}

/** Returns the shared MSAL instance. Throws if {@link createMsalInstance} has not run. */
export function getMsalInstance(): PublicClientApplication {
  if (!instance) {
    throw new Error('MSAL not initialised. Call createMsalInstance() during bootstrap.');
  }
  return instance;
}

/** Scope requested for the portal backend API (delegated user_impersonation). */
export { getApiScope };
