import React from 'react';
import ReactDOM from 'react-dom/client';
import { PublicClientApplication } from '@azure/msal-browser';
import { MsalProvider } from '@azure/msal-react';
import App from './App.tsx';
import './index.css';

/**
 * MSAL configuration — client ID and tenant ID are injected at build time
 * via Vite environment variables (FR-040b, FR-067: no hardcoded credentials).
 */
const msalInstance = new PublicClientApplication({
  auth: {
    clientId:   import.meta.env.VITE_ENTRA_CLIENT_ID as string,
    authority:  import.meta.env.VITE_ENTRA_AUTHORITY  as string,
    redirectUri: window.location.origin,
  },
  cache: {
    cacheLocation: 'sessionStorage',
    storeAuthStateInCookie: false,
  },
});

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <MsalProvider instance={msalInstance}>
      <App />
    </MsalProvider>
  </React.StrictMode>,
);
