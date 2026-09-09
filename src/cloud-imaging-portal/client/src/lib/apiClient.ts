import type { AccountInfo } from '@azure/msal-browser';
import { getMsalInstance, getApiScope } from './msal.ts';

/**
 * A single shared in-flight interactive-redirect promise. `msalInstance.acquireTokenRedirect`
 * throws `BrowserAuthError('interaction_in_progress')` if called again while a redirect is
 * already starting, which happens routinely here because pages fire several `apiFetch` calls
 * concurrently (e.g. `Promise.all`). Without de-duplication, the *second* concurrent caller's
 * unhandled `interaction_in_progress` error used to reject that request outright instead of
 * gracefully waiting for the one real redirect to navigate the page away, which is what
 * produced the "stalls, then shows a 401/403 error" experience for an expired session instead
 * of a clean redirect to sign-in.
 */
let redirectInFlight: Promise<never> | null = null;

/**
 * Where to send the user back to once an interactive sign-in redirect completes.
 *
 * MSAL's own `navigateToLoginRequestUrl` (default `true`) restores the deep link by
 * navigating a *second* time, from the redirect URI back to the originating URL. That
 * produced a visible double page load: one navigation returning from Entra, then another
 * straight afterwards, with the app booting and tearing down in between. We opt out of it
 * (see {@link triggerInteractiveRedirect}) and instead stash the path here, so bootstrap can
 * restore it with `history.replaceState` before React renders. One navigation, one load.
 */
const RETURN_PATH_KEY = 'cloudimaging.auth.returnPath';

function rememberReturnPath(): void {
  const { pathname, search, hash } = window.location;
  // Nothing to restore for the app root; leaving the key unset keeps bootstrap's fast path.
  if (pathname === '/' && !search && !hash) return;
  try {
    sessionStorage.setItem(RETURN_PATH_KEY, `${pathname}${search}${hash}`);
  } catch {
    // Storage unavailable (private mode quota). Losing the deep link is acceptable;
    // failing sign-in over it is not.
  }
}

/**
 * Returns the path the user was on when an interactive sign-in started, always clearing it.
 *
 * Only call this on the return leg of a redirect. The key is written when a redirect *starts*,
 * but a redirect can start and never finish (MSAL throws, the user presses Back, the tab is
 * closed mid-flow). Restoring it on an ordinary page load would silently rewrite the URL to a
 * path the user never asked for, so bootstrap clears it unconditionally and only uses the
 * value when MSAL confirms a redirect response was actually processed.
 */
export function consumeReturnPath(): string | null {
  try {
    const path = sessionStorage.getItem(RETURN_PATH_KEY);
    sessionStorage.removeItem(RETURN_PATH_KEY);
    return path;
  } catch {
    return null;
  }
}

/** Never resolves. Used while the browser is navigating away to the sign-in page. */
function pendingForever(): Promise<never> {
  return new Promise<never>(() => { /* intentionally never settles; navigation is in flight */ });
}

/**
 * Starts (or joins) a single interactive sign-in redirect. Callers should `await` the
 * returned promise and treat it as "this request can never complete on this page load":
 * it only resolves once the browser has navigated away.
 */
function triggerInteractiveRedirect(account: AccountInfo | null): Promise<never> {
  if (!redirectInFlight) {
    rememberReturnPath();
    redirectInFlight = getMsalInstance()
      .acquireTokenRedirect({
        account: account ?? undefined,
        scopes: [getApiScope()],
      })
      .catch(() => {
        // A rejection here (e.g. a raced 'interaction_in_progress') means this attempt did not
        // navigate. Release the latch so a later call can retry, otherwise every subsequent 401
        // joins this dead promise and the app wedges for the rest of the page's life.
        redirectInFlight = null;
      })
      .then(pendingForever);
  }
  return redirectInFlight;
}

function resolveAccount(): AccountInfo | null {
  const msalInstance = getMsalInstance();
  return msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0] ?? null;
}

/**
 * Verifies up front that the cached session can still produce an API token, and starts the
 * interactive redirect immediately if it can't.
 *
 * Without this, staleness was only ever discovered lazily, by whichever data fetch happened
 * to run first after the user came back to a tab that had been open past the refresh token's
 * 24-hour lifetime. That meant the page rendered, fired its queries, and *then* sat on a dead
 * silent-renewal iframe before anything visible happened. Calling this at bootstrap and
 * whenever the tab regains focus moves the detection to a point where showing a sign-in
 * redirect is expected rather than surprising.
 *
 * Returns true when the session is usable. Returns false when there is no signed-in account
 * to check; when the session is stale this never returns, because the browser navigates away.
 */
export async function ensureSessionFresh(): Promise<boolean> {
  const account = resolveAccount();
  if (!account) return false;
  try {
    await getMsalInstance().acquireTokenSilent({ account, scopes: [getApiScope()] });
    return true;
  } catch {
    return triggerInteractiveRedirect(account);
  }
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
    // interactive redirect. Awaiting it here means this call never falls through to
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
  // though MSAL believed it had (or could silently refresh) a valid one, e.g. a token
  // that expires between acquisition and the request landing. Force the same
  // interactive redirect rather than letting the caller render a raw auth-failure
  // response, so a stale tab recovers the same way whether MSAL or the server is the
  // one that first noticed the session is gone.
  if (response.status === 401) {
    return triggerInteractiveRedirect(resolveAccount());
  }

  return response;
}

const TRANSIENT_RETRY_DELAYS_MS = [300, 900, 2000];

function isTransientStatus(status: number): boolean {
  // 502/503/504 (and the less common 500/429) are what a cold-starting Function App /
  // App Service returns for the first request or two right after a deploy restart.
  // NOT evidence that the underlying data is gone. 401/403/404 are left alone (real
  // auth outcomes / genuinely-unconfigured resources) so this never masks a real error.
  return status === 500 || status === 429 || status === 502 || status === 503 || status === 504;
}

/**
 * Same as `apiFetch`, but retries a few times (short backoff) on a transient failure:
 * a network error, or a 5xx/429 response. Every future release restarts the Portal
 * Backend / Operator API / Imaging Core API for a few seconds; without this, anyone who
 * loads a page during that window sees data (e.g. branding) silently fall back to
 * defaults even though nothing was actually lost.
 */
export async function apiFetchWithRetry(input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> {
  let lastError: unknown;
  for (let attempt = 0; attempt <= TRANSIENT_RETRY_DELAYS_MS.length; attempt++) {
    try {
      const res = await apiFetch(input, init);
      if (res.ok || !isTransientStatus(res.status) || attempt === TRANSIENT_RETRY_DELAYS_MS.length) {
        return res;
      }
    } catch (err) {
      lastError = err;
      if (attempt === TRANSIENT_RETRY_DELAYS_MS.length) throw err;
    }
    await new Promise((resolve) => setTimeout(resolve, TRANSIENT_RETRY_DELAYS_MS[attempt]));
  }
  // Unreachable in practice (loop always returns/throws above); satisfies control-flow analysis.
  throw lastError instanceof Error ? lastError : new Error('apiFetchWithRetry exhausted retries');
}

/**
 * Extracts a human-readable message from a failed API response body. The portal server
 * wraps upstream (Operator API / Imaging Core API) errors as an RFC 7807-ish ProblemDetails
 * JSON object (`{ type, title, status, detail }`); older/plain-text upstream error bodies
 * are also handled by falling back to the raw text. Without this, callers that just did
 * `await res.text()` on a ProblemDetails response would display the raw
 * `{"type":"...","detail":"..."}` JSON blob to the operator instead of a readable message.
 * Never throws — always returns a usable string.
 */
export async function extractErrorDetail(res: Response, fallback: string): Promise<string> {
  const text = await res.text().catch(() => '');
  if (!text) return fallback;
  try {
    const parsed = JSON.parse(text) as { detail?: unknown; title?: unknown };
    if (typeof parsed.detail === 'string' && parsed.detail.length > 0) return parsed.detail;
    if (typeof parsed.title === 'string' && parsed.title.length > 0) return parsed.title;
  } catch {
    // Not JSON — the raw text itself is the message (a plain-text error body).
  }
  return text;
}
