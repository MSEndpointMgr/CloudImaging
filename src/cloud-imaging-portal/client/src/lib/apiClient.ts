import type { AccountInfo } from '@azure/msal-browser';
import { getMsalInstance, getApiScope } from './msal.ts';

/**
 * A single shared in-flight interactive-redirect promise. `msalInstance.acquireTokenRedirect`
 * throws `BrowserAuthError('interaction_in_progress')` if called again while a redirect is
 * already starting, which happens routinely here because pages fire several `apiFetch` calls
 * concurrently (e.g. `Promise.all`). Without de-duplication, the *second* concurrent caller's
 * unhandled `interaction_in_progress` error used to reject that request outright instead of
 * gracefully waiting for the one real redirect to navigate the page away — which is what
 * produced the "stalls, then shows a 401/403 error" experience for an expired session instead
 * of a clean redirect to sign-in.
 */
let redirectInFlight: Promise<never> | null = null;

/** Never resolves — used while the browser is navigating away to the sign-in page. */
function pendingForever(): Promise<never> {
  return new Promise<never>(() => { /* intentionally never settles; navigation is in flight */ });
}

/**
 * Starts (or joins) a single interactive sign-in redirect. Callers should `await` the
 * returned promise and treat it as "this request can never complete on this page load" —
 * it only resolves once the browser has navigated away.
 */
function triggerInteractiveRedirect(account: AccountInfo | null): Promise<never> {
  redirectInFlight ??= getMsalInstance()
    .acquireTokenRedirect({ account: account ?? undefined, scopes: [getApiScope()] })
    // Swallow errors here (e.g. a raced 'interaction_in_progress' from an overlapping
    // call) — either way a redirect is already underway, so just keep waiting for it.
    .catch(() => undefined)
    .then(pendingForever);
  return redirectInFlight;
}

function resolveAccount(): AccountInfo | null {
  const msalInstance = getMsalInstance();
  return msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0] ?? null;
}

/**
 * Silently acquires an access token for the portal backend API. Falls back to an
 * interactive redirect when the token cannot be refreshed silently. Returns `null`
 * when no account is signed in at all (nothing to redirect with; the server responds
 * 401 and `apiFetch`'s own 401 handling takes over from there).
 */
async function acquireApiToken(): Promise<string | null> {
  const account = resolveAccount();
  if (!account) return null;

  try {
    const result = await getMsalInstance().acquireTokenSilent({ account, scopes: [getApiScope()] });
    return result.accessToken;
  } catch {
    // Silent acquisition failed. The common case is an expired session: the access
    // token and refresh token have both lapsed and MSAL's hidden-iframe renewal is
    // blocked by the browser's third-party-cookie restrictions (surfaces as
    // BrowserAuthError e.g. monitor_window_timeout) or the session truly requires
    // interaction (InteractionRequiredAuthError). Either way the only recovery is an
    // interactive redirect — awaiting it here means this call never falls through to
    // an unauthenticated fetch that would otherwise flash a confusing 401/403 error in
    // the instant before the browser navigates to sign-in.
    return triggerInteractiveRedirect(account);
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
  const response = await fetch(input, { credentials: 'include', ...init, headers });

  // Defensive fallback: the server rejected the token as missing/expired/invalid even
  // though MSAL believed it had (or could silently refresh) a valid one — e.g. a token
  // that expires between acquisition and the request landing. Force the same
  // interactive redirect rather than letting the caller render a raw auth-failure
  // response, so a stale tab recovers the same way whether MSAL or the server is the
  // one that first noticed the session is gone.
  if (response.status === 401) {
    return triggerInteractiveRedirect(resolveAccount());
  }

  return response;
}
