import { PublicClientApplication } from '@azure/msal-browser';

/**
 * Shared MSAL instance used by both the React tree ({@link main.tsx} via
 * MsalProvider) and the non-React API client ({@link apiClient.ts}). Centralising
 * it here ensures token acquisition for API calls uses the same cache and account
 * state as interactive sign-in.
 *
 * Client ID and authority are injected at build time via Vite environment
 * variables (FR-040b, FR-067: no hardcoded credentials).
 */
export const msalInstance = new PublicClientApplication({
  auth: {
    clientId: import.meta.env.VITE_ENTRA_CLIENT_ID as string,
    authority: import.meta.env.VITE_ENTRA_AUTHORITY as string,
    redirectUri: window.location.origin,
  },
  cache: {
    cacheLocation: 'sessionStorage',
  },
});

/** Scope requested for the portal backend API (delegated user_impersonation). */
export const API_SCOPE = `api://${import.meta.env.VITE_ENTRA_CLIENT_ID as string}/user_impersonation`;
