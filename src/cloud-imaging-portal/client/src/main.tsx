import React from 'react';
import ReactDOM from 'react-dom/client';
import { MsalProvider } from '@azure/msal-react';
import { loadRuntimeConfig } from './lib/runtimeConfig.ts';
import { createMsalInstance } from './lib/msal.ts';
import App from './App.tsx';
import './index.css';

/**
 * Async bootstrap: fetch runtime portal configuration (Entra client ID/authority)
 * from the backend's public /api/config endpoint, construct and initialise MSAL
 * from it, then render. This keeps the prebuilt bundle tenant-agnostic — the same
 * artifact deploys to any customer without a rebuild (FR-041/FR-042).
 */
async function bootstrap(): Promise<void> {
  const root = ReactDOM.createRoot(document.getElementById('root')!);
  try {
    await loadRuntimeConfig();
    const msalInstance = createMsalInstance();
    await msalInstance.initialize();

    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0) {
      msalInstance.setActiveAccount(accounts[0]);
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
