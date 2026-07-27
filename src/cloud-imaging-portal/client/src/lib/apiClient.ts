import { InteractionRequiredAuthError, BrowserAuthError } from '@azure/msal-browser';
import { getMsalInstance, getApiScope } from './msal.ts';

/**
 * Silently acquires an access token for the portal backend API. Falls back to an
 * interactive redirect when the token cannot be refreshed silently. Returns `null`
 * when no account is signed in so callers still issue the request (the server
 * responds 401 and the UI surfaces the failure).
 */
async function acquireApiToken(): Promise<string | null> {
  const msalInstance = getMsalInstance();
  const apiScope = getApiScope();
  const account = msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0] ?? null;
  if (!account) return null;

  try {
    const result = await msalInstance.acquireTokenSilent({ account, scopes: [apiScope] });
    return result.accessToken;
  } catch (error) {
    // Silent acquisition failed. The common case is an expired session: the access
    // token and refresh token have both lapsed and MSAL's hidden-iframe renewal is
    // blocked by the browser's third-party-cookie restrictions, which surfaces as a
    // BrowserAuthError (e.g. monitor_window_timeout) rather than an
    // InteractionRequiredAuthError. In both cases the only recovery is an interactive
    // redirect — without it every API call would silently go out unauthenticated and
    // return 401 while the stale account keeps the UI looking "signed in". A top-level
    // redirect is first-party to the login endpoint, so it succeeds where the iframe
    // could not.
    if (error instanceof InteractionRequiredAuthError || error instanceof BrowserAuthError) {
      await msalInstance.acquireTokenRedirect({ account, scopes: [apiScope] });
    }
    return null;
  }
}

/**
 * Authenticated `fetch` wrapper for portal backend (`/api/*`) calls. Attaches a
 * bearer token (FR-030/FR-040) and preserves cookie credentials. Use this in
 * place of the global `fetch` for every same-origin API request.
 */
export async function apiFetch(input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> {
  const token = await acquireApiToken();
  const headers = new Headers(init.headers ?? {});
  if (token && !headers.has('Authorization')) {
    headers.set('Authorization', `Bearer ${token}`);
  }
  return fetch(input, { credentials: 'include', ...init, headers });
}
