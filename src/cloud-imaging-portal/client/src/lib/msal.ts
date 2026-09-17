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
    system: {
      // Left at MSAL's default. The 3s override that used to live here was compensating for
      // silent renewal never being able to succeed, which was a redirectUri bug rather than a
      // timing one (see getSilentRedirectUri).
    },
  });
  return instance;
}

/**
 * Redirect URI for silent (hidden iframe) token requests.
 *
 * Must be a page that renders nothing. Silent requests inherit the app-root redirectUri
 * otherwise, so Entra returns the response into the iframe, the SPA boots inside it and fires
 * its own silent request, and MSAL aborts the whole thing with block_iframe_reload. Register
 * this URL as an additional SPA redirect URI on the portal app registration.
 */
export function getSilentRedirectUri(): string {
  return `${window.location.origin}/blank.html`;
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
