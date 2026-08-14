import { createContext, useContext } from 'react';
import { useMsal, useIsAuthenticated } from '@azure/msal-react';
import type { AccountInfo } from '@azure/msal-browser';
import { getApiScope } from '../lib/msal.ts';

/** Portal application roles carried in a signed-in user's token. */
export type PortalRole = 'CloudImaging.Administrator' | 'CloudImaging.Technician';

interface AuthContextValue {
  account: AccountInfo | null;
  isAuthenticated: boolean;
  /** App roles from the user's token (e.g. CloudImaging.Administrator). */
  roles: string[];
  /** True when the user holds the CloudImaging.Administrator role. */
  isAdministrator: boolean;
  /** True when the user holds any portal role (Administrator or Technician). */
  hasPortalAccess: boolean;
  /** Acquires a silent token for the portal backend API scope. */
  getAccessToken: () => Promise<string>;
  signOut: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

/** Extracts the app-role claim from an MSAL account's ID token. */
export function rolesFromAccount(account: AccountInfo | null): string[] {
  const claim: unknown = account?.idTokenClaims?.['roles'];
  if (Array.isArray(claim)) return claim as string[];
  if (typeof claim === 'string') return [claim];
  return [];
}

export function AuthProvider({ children }: { children: React.ReactNode }): React.ReactElement {
  const { instance, accounts } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const account = accounts[0] ?? null;

  const roles = rolesFromAccount(account);
  const isAdministrator = roles.includes('CloudImaging.Administrator');
  const hasPortalAccess = isAdministrator || roles.includes('CloudImaging.Technician');

  const getAccessToken = async (): Promise<string> => {
    if (!account) throw new Error('Not authenticated');
    const result = await instance.acquireTokenSilent({
      account,
      scopes: [getApiScope()],
    });
    return result.accessToken;
  };

  const signOut = (): void => {
    void instance.logoutRedirect({ account });
  };

  return (
    <AuthContext.Provider value={{ account, isAuthenticated, roles, isAdministrator, hasPortalAccess, getAccessToken, signOut }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
