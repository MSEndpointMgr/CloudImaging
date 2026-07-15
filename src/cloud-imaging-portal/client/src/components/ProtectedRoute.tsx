import { useIsAuthenticated, useMsalAuthentication } from '@azure/msal-react';
import { InteractionType } from '@azure/msal-browser';

interface Props {
  children: React.ReactNode;
}

/**
 * Wraps routes that require authentication (FR-030).
 * Unauthenticated users are redirected to the Entra ID sign-in page.
 */
export function ProtectedRoute({ children }: Props): React.ReactElement | null {
  const isAuthenticated = useIsAuthenticated();

  // Trigger interactive redirect login if not authenticated
  useMsalAuthentication(InteractionType.Redirect, {
    scopes: [`api://${import.meta.env.VITE_ENTRA_CLIENT_ID}/access_as_user`],
  });

  if (!isAuthenticated) {
    // Auth redirect is in progress; render nothing until the redirect completes
    return null;
  }

  return <>{children}</>;
}
