import { useIsAuthenticated, useMsalAuthentication } from '@azure/msal-react';
import { InteractionType } from '@azure/msal-browser';
import { useAuth } from '../context/authContext.tsx';
import { getApiScope } from '../lib/msal.ts';
import { AccessDenied } from './AccessDenied.tsx';

interface Props {
  children: React.ReactNode;
}

/**
 * Wraps routes that require authentication (FR-030) and a portal role (FR-040a).
 * Unauthenticated users are redirected to the Entra ID sign-in page; signed-in users
 * without an assigned portal role see an access-denied notice instead of an empty portal.
 */
export function ProtectedRoute({ children }: Props): React.ReactElement | null {
  const isAuthenticated = useIsAuthenticated();
  const { hasPortalAccess } = useAuth();

  // Trigger interactive redirect login if not authenticated
  const { error } = useMsalAuthentication(InteractionType.Redirect, {
    scopes: [getApiScope()],
  });

  if (!isAuthenticated) {
    // Surface any sign-in failure instead of showing a blank page (FR-030).
    if (error) {
      return (
        <div className="mx-auto max-w-2xl p-8">
          <h1 className="text-xl font-semibold text-red-600">Sign-in failed</h1>
          <p className="mt-2 font-mono text-sm text-muted-foreground">{error.errorCode}</p>
          <pre className="mt-2 whitespace-pre-wrap text-sm text-muted-foreground">{error.errorMessage}</pre>
        </div>
      );
    }
    // Auth redirect is in progress; render nothing until the redirect completes
    return null;
  }

  if (!hasPortalAccess) {
    return <AccessDenied />;
  }

  return <>{children}</>;
}
