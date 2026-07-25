import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { getMsalInstance, getApiScope } from './msal.ts';

/**
 * Silently acquires an access token for the portal backend API. Falls back to a
 * redirect when the token cannot be refreshed silently (e.g. consent required).
 * Returns `null` when no account is signed in so callers still issue the request
 * (the server responds 401 and the UI surfaces the failure).
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
    if (error instanceof InteractionRequiredAuthError) {
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
