import type { AccountInfo } from '@azure/msal-browser';
import { getMsalInstance, getApiScope, getSilentRedirectUri } from './msal.ts';

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
 * Reads and clears the path the user was on when an interactive sign-in started.
 *
 * Call this on EVERY page load. Clearing is the point: the key is written when a redirect
 * *starts*, but a redirect can start and never finish (MSAL throws, the user presses Back, the
 * tab is closed mid-flow), and a value left behind would then be applied to an unrelated later
 * load. Leaving it in place is what previously rewrote the URL on an ordinary refresh and
 * stranded the portal on an error screen until browser storage was cleared by hand.
 *
 * Deciding whether to *use* the returned value is the caller's job, and bootstrap only does so
 * when `handleRedirectPromise()` confirms this load really is the return leg of a redirect.
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

/**
 * True when a stored return path is a plain same-origin path that is safe to restore.
 *
 * The value round-trips through sessionStorage, so it must be treated as untrusted input
 * rather than assumed to be what `rememberReturnPath` wrote. A protocol-relative (`//host`)
 * or absolute value would either navigate the user off-origin or make `replaceState` throw
 * a SecurityError and take the whole bootstrap down with it.
 */
export function isSafeReturnPath(path: string): boolean {
  if (!path.startsWith('/') || path.startsWith('//')) return false;
  try {
    // Resolving against the current origin catches anything that escapes it, including
    // backslash and encoded variants that a naive prefix check would let through.
    return new URL(path, window.location.origin).origin === window.location.origin;
  } catch {
    return false;
  }
}

/** Never resolves. Used while the browser is navigating away to the sign-in page. */
function pendingForever(): Promise<never> {
  return new Promise<never>(() => { /* intentionally never settles; navigation is in flight */ });
}

/**
 * Ceiling on interactive sign-in redirects, and the window it applies over.
 *
 * Every redirect tears the app down and boots it again, so `redirectInFlight` above only
 * de-duplicates *within* one page load and can never see a loop that spans loads. A condition
 * that makes the session look unusable on every single load (the portal backend rejecting every
 * token because its ENTRA_CLIENT_ID/ENTRA_TENANT_ID don't match the ones the SPA signed in with,
 * for instance) therefore bounced the browser between the portal and Entra without limit, until
 * Entra's own loop protection cut in and answered with "We couldn't sign you in. Please try
 * again." on the account picker. The counter has to live in storage that survives the
 * navigation for the app to notice that at all.
 *
 * Three is deliberately above the one redirect a genuinely expired session costs, so the normal
 * recovery path is never interrupted.
 */
const REDIRECT_BUDGET_KEY = 'cloudimaging.auth.redirectAttempts';
const REDIRECT_BUDGET_MAX = 3;
const REDIRECT_BUDGET_WINDOW_MS = 120_000;

let signInLoopDetected = false;
const signInLoopListeners = new Set<() => void>();

/**
 * Why the last silent token acquisition failed, persisted across the redirect it triggers.
 *
 * Both call sites used to swallow the MSAL error entirely, so a loop left no evidence anywhere:
 * the redirect never reaches the backend, so there is nothing in server logs either, and the
 * only visible symptom was the browser bouncing. Keeping the error code (never the token or
 * claims) is what makes the difference between naming the cause and guessing at it.
 */
const SILENT_FAILURE_KEY = 'cloudimaging.auth.lastSilentFailure';

export interface SilentFailureDiagnostic {
  errorCode: string;
  message: string;
  accountCount: number;
  activeAccountMatched: boolean;
  at: string;
}

function recordSilentFailure(err: unknown): void {
  const msalInstance = getMsalInstance();
  const active = msalInstance.getActiveAccount();
  const diagnostic: SilentFailureDiagnostic = {
    errorCode: (err as { errorCode?: string }).errorCode ?? (err as Error)?.name ?? 'unknown',
    message: (err as Error)?.message ?? String(err),
    accountCount: msalInstance.getAllAccounts().length,
    activeAccountMatched: active !== null,
    at: new Date().toISOString(),
  };
  console.warn('Silent token acquisition failed', diagnostic);
  try {
    sessionStorage.setItem(SILENT_FAILURE_KEY, JSON.stringify(diagnostic));
  } catch {
    // Diagnostics are never worth breaking sign-in over.
  }
}

/** Returns the last recorded silent-acquisition failure, for the sign-in loop screen. */
export function getSilentFailureDiagnostic(): SilentFailureDiagnostic | null {
  try {
    const raw = sessionStorage.getItem(SILENT_FAILURE_KEY);
    return raw ? (JSON.parse(raw) as SilentFailureDiagnostic) : null;
  } catch {
    return null;
  }
}

/** True once the redirect budget has been exhausted; sign-in will not be retried again. */
export function isSignInLoopDetected(): boolean {
  return signInLoopDetected;
}

/** Subscribes to loop detection. Shaped for `useSyncExternalStore`. */
export function subscribeToSignInLoop(listener: () => void): () => void {
  signInLoopListeners.add(listener);
  return () => {
    signInLoopListeners.delete(listener);
  };
}

function reportSignInLoop(): void {
  if (signInLoopDetected) return;
  signInLoopDetected = true;
  for (const listener of signInLoopListeners) listener();
}

/** Records a redirect attempt. Returns false once the budget for the window is spent. */
function consumeRedirectBudget(): boolean {
  const now = Date.now();
  try {
    const raw = sessionStorage.getItem(REDIRECT_BUDGET_KEY);
    let first = now;
    let count = 0;
    if (raw) {
      const parsed = JSON.parse(raw) as { count?: unknown; first?: unknown };
      if (
        typeof parsed.first === 'number' &&
        typeof parsed.count === 'number' &&
        now - parsed.first < REDIRECT_BUDGET_WINDOW_MS
      ) {
        first = parsed.first;
        count = parsed.count;
      }
    }
    count += 1;
    sessionStorage.setItem(REDIRECT_BUDGET_KEY, JSON.stringify({ count, first }));
    return count <= REDIRECT_BUDGET_MAX;
  } catch {
    // Storage unavailable (private mode quota). Blocking sign-in over a missing counter would
    // be a worse failure than the loop it guards against.
    return true;
  }
}

/** Restores the full redirect budget. Called once the server accepts a token. */
export function clearRedirectBudget(): void {
  try {
    sessionStorage.removeItem(REDIRECT_BUDGET_KEY);
  } catch {
    // Nothing to clear if storage is unavailable.
  }
}

/**
 * Starts (or joins) a single interactive sign-in redirect. Callers should `await` the
 * returned promise and treat it as "this request can never complete on this page load":
 * it only resolves once the browser has navigated away.
 */
function triggerInteractiveRedirect(account: AccountInfo | null): Promise<never> {
  if (!consumeRedirectBudget()) {
    // Stop navigating and let the UI explain the failure. Callers still never settle, which is
    // the same contract as a redirect, so nothing downstream renders a half-loaded page behind
    // the notice.
    reportSignInLoop();
    return pendingForever();
  }

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
    await getMsalInstance().acquireTokenSilent({
      account,
      scopes: [getApiScope()],
      redirectUri: getSilentRedirectUri(),
    });
    return true;
  } catch (err) {
    recordSilentFailure(err);
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
    const result = await getMsalInstance().acquireTokenSilent({
      account,
      scopes: [getApiScope()],
      redirectUri: getSilentRedirectUri(),
    });
    return result.accessToken;
  } catch (err) {
    // Silent acquisition failed. The common case is an expired session: the access
    // token and refresh token have both lapsed and MSAL's hidden-iframe renewal is
    // blocked by the browser's third-party-cookie restrictions (surfaces as
    // BrowserAuthError e.g. monitor_window_timeout) or the session truly requires
    // interaction (InteractionRequiredAuthError). Either way the only recovery is an
    // interactive redirect. Awaiting it here means this call never falls through to
    // an unauthenticated fetch that would otherwise flash a confusing 401/403 error in
    // the instant before the browser navigates to sign-in.
    recordSilentFailure(err);
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

  // Any other status means the server validated the token, so whatever redirect brought us
  // here did its job. A 403 counts: the token was accepted, the caller just lacks the role.
  clearRedirectBudget();

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
