import React from 'react';
import ReactDOM from 'react-dom/client';
import { MsalProvider } from '@azure/msal-react';
import { loadRuntimeConfig } from './lib/runtimeConfig.ts';
import { createMsalInstance } from './lib/msal.ts';
import { consumeReturnPath, isSafeReturnPath } from './lib/apiClient.ts';
import App from './App.tsx';
import './index.css';

/**
 * Async bootstrap: fetch runtime portal configuration (Entra client ID/authority)
 * from the backend's public /api/config endpoint, construct and initialise MSAL
 * from it, then render. This keeps the prebuilt bundle tenant-agnostic: the same
 * artifact deploys to any customer without a rebuild (FR-041/FR-042).
 */
async function bootstrap(): Promise<void> {
  const root = ReactDOM.createRoot(document.getElementById('root')!);
  try {
    await loadRuntimeConfig();
    const msalInstance = createMsalInstance();
    await msalInstance.initialize();

    // Process a pending sign-in response *before* reading accounts, so the account we make
    // active below is the one this redirect just produced rather than the stale entry it
    // replaced. MsalProvider reuses this same resolved response, so calling it here is free.
    //
    // navigateToLoginRequestUrl: false is the important part. Left at its default of true,
    // MSAL restores the originating deep link by navigating a second time, so returning from
    // Entra cost two full page loads: one back to the redirect URI, then another to the page
    // the user actually came from, with the app booting and tearing down in between. We
    // restore that path below with replaceState instead.
    const redirectResult = await msalInstance.handleRedirectPromise({ navigateToLoginRequestUrl: false });

    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0) {
      msalInstance.setActiveAccount(accounts[0]);
    }

    // Always clear the stored path, but only act on it when this load genuinely is the return
    // leg of a redirect (redirectResult is non-null exactly then). A redirect can start and
    // never finish, and restoring a leftover path on an ordinary refresh would silently send
    // the user to a page they didn't ask for.
    const returnPath = consumeReturnPath();
    const current = window.location.pathname + window.location.search + window.location.hash;
    if (redirectResult && returnPath && returnPath !== current && isSafeReturnPath(returnPath)) {
      try {
        window.history.replaceState(null, '', returnPath);
      } catch {
        // replaceState rejects anything it considers cross-origin. Landing on the app root is
        // a perfectly good outcome; failing to boot the portal over it is not.
      }
    }

    root.render(
      <React.StrictMode>
        <MsalProvider instance={msalInstance}>
          <App />
        </MsalProvider>
      </React.StrictMode>,
    );
  } catch (err) {
    const detail = err instanceof Error ? err.message : String(err);
    root.render(
      <div className="mx-auto max-w-2xl p-8">
        <h1 className="text-xl font-semibold text-red-600">Portal configuration error</h1>
        <p className="mt-2 text-sm text-muted-foreground">
          The portal could not load its Entra ID configuration from the backend.
          Confirm the portal backend is reachable at <code>/api/config</code>.
        </p>
        <pre className="mt-2 whitespace-pre-wrap text-sm text-muted-foreground">{detail}</pre>
      </div>,
    );
  }
}

void bootstrap();
