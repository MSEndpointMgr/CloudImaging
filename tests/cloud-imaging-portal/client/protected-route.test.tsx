import { describe, it, expect, vi, beforeEach } from 'vitest';

/** Runtime value of MSAL's InteractionType.Redirect. Imported by name in ProtectedRoute itself;
 *  asserted as a literal here because bare module specifiers don't resolve from tests/. */
const INTERACTION_TYPE_REDIRECT = 'redirect';

/**
 * Verifies the real ProtectedRoute behaviour rather than restating intent.
 *
 * The specific regression guarded here: mandatory sign-in must request ONLY the portal API
 * scope. Bundling the optional Microsoft Graph profile-photo scope into this request means a
 * tenant that blocks user consent to Graph cannot sign in at all, turning a cosmetic avatar
 * into a hard sign-in failure.
 *
 * The component is invoked directly rather than rendered. It holds no React state, and every
 * hook it calls is mocked below with a plain function, so there is no need for a DOM testing
 * library the project doesn't otherwise depend on.
 */

const useMsalAuthentication = vi.fn(() => ({ error: null, result: null, login: vi.fn() }));
const useIsAuthenticated = vi.fn(() => true);

vi.mock('@azure/msal-react', () => ({
  useMsalAuthentication: (...args: unknown[]) => useMsalAuthentication(...(args as [])),
  useIsAuthenticated: () => useIsAuthenticated(),
}));

const TEST_CLIENT_ID = '00000000-0000-0000-0000-000000000000';
const API_SCOPE = `api://${TEST_CLIENT_ID}/user_impersonation`;

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/msal.ts', () => ({
  getApiScope: () => API_SCOPE,
  getMsalInstance: () => ({ getActiveAccount: () => null, getAllAccounts: () => [] }),
}));

const useAuth = vi.fn<() => { hasPortalAccess: boolean }>(() => ({ hasPortalAccess: true }));
vi.mock('../../../src/cloud-imaging-portal/client/src/context/authContext.tsx', () => ({
  useAuth: () => useAuth(),
  GRAPH_PHOTO_SCOPE: 'User.Read',
  rolesFromAccount: () => [],
}));

const { ProtectedRoute } = await import(
  '../../../src/cloud-imaging-portal/client/src/components/ProtectedRoute.tsx'
);

/** Invokes the component and returns the interaction type and scopes it asked MSAL for. */
function invokeAndCaptureRequest(): { interactionType: string; scopes: string[] } {
  ProtectedRoute({ children: 'content' });
  const [interactionType, request] = useMsalAuthentication.mock.calls[0] as unknown as [
    string,
    { scopes: string[] },
  ];
  return { interactionType, scopes: request.scopes };
}

describe('ProtectedRoute: mandatory sign-in', () => {
  beforeEach(() => {
    useMsalAuthentication.mockClear();
    useIsAuthenticated.mockReturnValue(true);
    useAuth.mockReturnValue({ hasPortalAccess: true });
  });

  it('requests an interactive redirect for the portal API scope', () => {
    const { interactionType, scopes } = invokeAndCaptureRequest();
    expect(interactionType).toBe(INTERACTION_TYPE_REDIRECT);
    expect(scopes).toEqual([API_SCOPE]);
  });

  it('does NOT bundle the Graph profile-photo scope into mandatory sign-in', () => {
    const { scopes } = invokeAndCaptureRequest();
    // Reintroducing 'User.Read' here breaks sign-in outright in tenants that block user
    // consent to Microsoft Graph. The avatar is requested separately, after sign-in.
    expect(scopes).not.toContain('User.Read');
  });

  it('renders its children once authenticated and holding a portal role', () => {
    const result = ProtectedRoute({ children: 'content' });
    expect(JSON.stringify(result)).toContain('content');
  });

  it('does not render children when the user holds no portal role', () => {
    useAuth.mockReturnValue({ hasPortalAccess: false });
    const result = ProtectedRoute({ children: 'content' });
    expect(JSON.stringify(result)).not.toContain('content');
  });

  it('does not render children while unauthenticated', () => {
    useIsAuthenticated.mockReturnValue(false);
    const result = ProtectedRoute({ children: 'content' });
    expect(JSON.stringify(result)).not.toContain('content');
  });
});
