import { createContext, useContext, useEffect, useRef, useState } from 'react';
import { useMsal, useIsAuthenticated } from '@azure/msal-react';
import type { AccountInfo } from '@azure/msal-browser';
import { getApiScope } from '../lib/msal.ts';

/** Portal application roles carried in a signed-in user's token. */
export type PortalRole = 'CloudImaging.Administrator' | 'CloudImaging.Technician';

/**
 * Delegated Microsoft Graph scope needed to read the signed-in user's own profile photo
 * (`GET /me/photo/$value`). Deliberately NOT requested at sign-in alongside the portal API
 * scope — acquiring it is always attempted silently (see AuthProvider below) and never
 * triggers an interactive consent prompt, so a tenant that hasn't consented to it behaves
 * exactly like a user with no photo set: Header falls back to the initials avatar.
 */
const GRAPH_PHOTO_SCOPE = 'User.Read';

interface AuthContextValue {
  account: AccountInfo | null;
  isAuthenticated: boolean;
  /** App roles from the user's token (e.g. CloudImaging.Administrator). */
  roles: string[];
  /** True when the user holds the CloudImaging.Administrator role. */
  isAdministrator: boolean;
  /** True when the user holds any portal role (Administrator or Technician). */
  hasPortalAccess: boolean;
  /**
   * Object URL for the signed-in user's Entra ID profile photo, or null when one isn't
   * available — no photo set, the tenant hasn't consented to {@link GRAPH_PHOTO_SCOPE}, a
   * Graph outage, or it just hasn't finished loading yet. Header renders an initials avatar
   * whenever this is null, so callers never need to handle the failure themselves.
   */
  avatarUrl: string | null;
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

  const [avatarUrl, setAvatarUrl] = useState<string | null>(null);
  const avatarUrlRef = useRef<string | null>(null);

  const setAvatarObjectUrl = (url: string | null) => {
    if (avatarUrlRef.current) {
      URL.revokeObjectURL(avatarUrlRef.current);
    }
    avatarUrlRef.current = url;
    setAvatarUrl(url);
  };

  useEffect(() => {
    if (!account) {
      setAvatarObjectUrl(null);
      return;
    }

    let cancelled = false;

    void (async () => {
      try {
        // Silent-only: acquireTokenSilent throws (rather than prompting) whenever
        // GRAPH_PHOTO_SCOPE hasn't been consented for this tenant, so this never
        // interrupts sign-in with an extra consent screen.
        const result = await instance.acquireTokenSilent({ account, scopes: [GRAPH_PHOTO_SCOPE] });
        const res = await fetch('https://graph.microsoft.com/v1.0/me/photo/$value', {
          headers: { Authorization: `Bearer ${result.accessToken}` },
        });
        if (cancelled) return;
        if (!res.ok) {
          // Most commonly 404 (user has no photo) — not an error worth logging.
          setAvatarObjectUrl(null);
          return;
        }
        setAvatarObjectUrl(URL.createObjectURL(await res.blob()));
      } catch {
        if (!cancelled) setAvatarObjectUrl(null);
      }
    })();

    return () => {
      cancelled = true;
    };
    // Re-run only when the signed-in account actually changes, not on every MSAL re-render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [account?.homeAccountId]);

  // Revoke the last object URL on unmount.
  useEffect(() => () => {
    if (avatarUrlRef.current) {
      URL.revokeObjectURL(avatarUrlRef.current);
    }
  }, []);

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
    <AuthContext.Provider value={{ account, isAuthenticated, roles, isAdministrator, hasPortalAccess, avatarUrl, getAccessToken, signOut }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
