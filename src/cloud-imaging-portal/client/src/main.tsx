import React from 'react';
import ReactDOM from 'react-dom/client';
import { MsalProvider } from '@azure/msal-react';
import { loadRuntimeConfig } from './lib/runtimeConfig.ts';
import { createMsalInstance } from './lib/msal.ts';
import { consumeReturnPath } from './lib/apiClient.ts';
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
    await msalInstance.handleRedirectPromise({ navigateToLoginRequestUrl: false });

    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0) {
      msalInstance.setActiveAccount(accounts[0]);
    }

    // We opted out of MSAL restoring the deep link (it does so with a second full page
    // navigation). Rewrite the URL in place instead, before React Router reads it, so the
    // user lands back where they were with no extra load.
    const returnPath = consumeReturnPath();
    if (returnPath && returnPath !== window.location.pathname + window.location.search + window.location.hash) {
      window.history.replaceState(null, '', returnPath);
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
