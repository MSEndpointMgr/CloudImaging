import { createContext, useContext } from 'react';
import { useMsal, useIsAuthenticated } from '@azure/msal-react';
import type { AccountInfo } from '@azure/msal-browser';

interface AuthContextValue {
  account: AccountInfo | null;
  isAuthenticated: boolean;
  /** Acquires a silent token for the portal backend API scope. */
  getAccessToken: () => Promise<string>;
  signOut: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

const API_SCOPE = `api://${import.meta.env.VITE_ENTRA_CLIENT_ID}/access_as_user`;

export function AuthProvider({ children }: { children: React.ReactNode }): React.ReactElement {
  const { instance, accounts } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const account = accounts[0] ?? null;

  const getAccessToken = async (): Promise<string> => {
    if (!account) throw new Error('Not authenticated');
    const result = await instance.acquireTokenSilent({
      account,
      scopes: [API_SCOPE],
    });
    return result.accessToken;
  };

  const signOut = (): void => {
    void instance.logoutRedirect({ account });
  };

  return (
    <AuthContext.Provider value={{ account, isAuthenticated, getAccessToken, signOut }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
