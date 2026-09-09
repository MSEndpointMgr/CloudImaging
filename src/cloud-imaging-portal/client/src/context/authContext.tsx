import { createContext, useContext, useEffect, useRef, useState } from 'react';
import { useMsal, useIsAuthenticated } from '@azure/msal-react';
import type { AccountInfo } from '@azure/msal-browser';
import { getApiScope } from '../lib/msal.ts';
import { ensureSessionFresh } from '../lib/apiClient.ts';

/** Portal application roles carried in a signed-in user's token. */
export type PortalRole = 'CloudImaging.Administrator' | 'CloudImaging.Technician' | 'CloudImaging.Reader';

/**
 * Delegated Microsoft Graph scope needed to read the signed-in user's own profile photo
 * (`GET /me/photo/$value`). Purely cosmetic: Header/AccessDenied always render the user's
 * name and initials from ID token claims regardless of this scope. It is requested only
 * after sign-in (see {@link AuthProvider}'s avatar effect below), never bundled with the
 * mandatory portal API scope, so a tenant that blocks user consent to Graph (or an admin
 * who hasn't pre-consented it) can never break sign-in — worst case, no avatar photo.
 */
export const GRAPH_PHOTO_SCOPE = 'User.Read';

interface AuthContextValue {
  account: AccountInfo | null;
  isAuthenticated: boolean;
  /** App roles from the user's token (e.g. CloudImaging.Administrator). */
  roles: string[];
  /** True when the user holds the CloudImaging.Administrator role. */
  isAdministrator: boolean;
  /** True when the user holds the CloudImaging.Technician role. */
  isTechnician: boolean;
  /** True when the user holds the CloudImaging.Reader role (Dashboard + Reports only). */
  isReader: boolean;
  /** True when the user holds any portal role (Administrator, Technician, or Reader). */
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
  const isTechnician = roles.includes('CloudImaging.Technician');
  const isReader = roles.includes('CloudImaging.Reader');
  const hasPortalAccess = isAdministrator || isTechnician || isReader;

  const [avatarUrl, setAvatarUrl] = useState<string | null>(null);
  const avatarUrlRef = useRef<string | null>(null);

  // Re-check the session whenever the tab comes back to the foreground. A portal left open
  // overnight always returns with a refresh token Entra has already expired (24h ceiling for
  // browser SPAs), and without this the staleness only surfaced once the user clicked
  // something and that page's queries stalled on a doomed silent renewal. Checking on focus
  // means the sign-in redirect happens while the user is still orienting, not mid-click.
  useEffect(() => {
    if (!account) return;

    const check = (): void => {
      if (document.visibilityState === 'visible') void ensureSessionFresh();
    };

    check();
    document.addEventListener('visibilitychange', check);
    return () => document.removeEventListener('visibilitychange', check);
    // Keyed on the account identity, not the object, so ordinary MSAL re-renders don't re-run it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [account?.homeAccountId]);

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
        // Silent only. A failure here is nearly always one of two things: the scope was never
        // consented, or the whole session is stale (in which case the session check above is
        // already redirecting). Neither is worth an interactive popup: outside a user gesture
        // the browser blocks it anyway, and it used to sit on MSAL's iframe timeout first,
        // adding a second stall on top of the one the data fetches were already paying.
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
    <AuthContext.Provider value={{ account, isAuthenticated, roles, isAdministrator, isTechnician, isReader, hasPortalAccess, avatarUrl, getAccessToken, signOut }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
