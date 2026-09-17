import { useSyncExternalStore } from 'react';
import { AlertTriangle, LogOut, RefreshCw } from 'lucide-react';
import { useAuth } from '../context/authContext.tsx';
import { useBranding } from '../context/brandingContext.tsx';
import { clearRedirectBudget, getSilentFailureDiagnostic, isSignInLoopDetected, subscribeToSignInLoop } from '../lib/apiClient.ts';
import { Button } from './ui/button.tsx';

/**
 * Replaces the whole app once sign-in has been abandoned, and renders its children until then.
 *
 * Sits above ProtectedRoute rather than inside it because the failing account still looks
 * authenticated to MSAL: it holds a valid ID token, so ProtectedRoute would show either the
 * portal or the no-access notice while every API call behind it silently restarts sign-in.
 */
export function SignInLoopGate({ children }: { children: React.ReactNode }): React.ReactElement {
  const detected = useSyncExternalStore(subscribeToSignInLoop, isSignInLoopDetected);
  return detected ? <SignInLoopScreen /> : <>{children}</>;
}

/**
 * Shown when sign-in has redirected to Entra ID repeatedly without ever producing a session the
 * portal backend accepts.
 *
 * This state is always a deployment problem rather than a user problem, so the copy names the
 * settings to check: the backend rejecting every token with 401 is what drives the redirect, and
 * it does that when its Entra app settings disagree with the ones the SPA signed in against.
 * Without this screen the browser simply kept bouncing until Entra answered the account picker
 * with "We couldn't sign you in. Please try again.", which says nothing about the cause.
 */
export function SignInLoopScreen(): React.ReactElement {
  const { account, signOut } = useAuth();
  const { branding } = useBranding();

  const appName = branding.applicationName ?? 'Cloud Imaging';
  const email = account?.username ?? '';
  const diagnostic = getSilentFailureDiagnostic();

  const retry = (): void => {
    clearRedirectBudget();
    window.location.reload();
  };

  return (
    <div className="flex min-h-screen items-start justify-center bg-muted/30 p-6 pt-[15vh] text-foreground">
      <div className="w-full max-w-lg rounded-xl border border-border bg-card p-8 shadow-sm">
        <span className="flex h-14 w-14 items-center justify-center rounded-full bg-amber-500/10 text-amber-500">
          <AlertTriangle size={24} aria-hidden="true" />
        </span>

        <h1 className="mt-5 text-lg font-semibold text-foreground">Sign-in could not be completed</h1>

        <p className="mt-2 text-sm text-muted-foreground">
          {appName} sent you to Microsoft Entra ID several times in a row
          {email ? (
            <>
              {' '}
              as <span className="font-medium text-foreground">{email}</span>
            </>
          ) : null}
          , and each time it came back without a usable session. Retrying on its own will not
          resolve this, so sign-in has been stopped here.
        </p>

        <p className="mt-4 text-sm text-muted-foreground">
          Signing out and back in clears the stored session and normally restores access.
        </p>

        {diagnostic ? (
          <div className="mt-4 rounded-lg border border-border bg-muted/40 p-3">
            <p className="text-xs font-medium text-foreground">Diagnostic</p>
            <p className="mt-1 font-mono text-xs break-all text-muted-foreground">
              {diagnostic.errorCode} &middot; {diagnostic.accountCount} cached account
              {diagnostic.accountCount === 1 ? '' : 's'} &middot; {diagnostic.at}
            </p>
            <p className="mt-1 font-mono text-xs break-all text-muted-foreground">{diagnostic.message}</p>
          </div>
        ) : null}

        <p className="mt-4 text-sm font-medium text-foreground">What an administrator should check</p>
        <ul className="mt-2 space-y-2 text-sm text-muted-foreground">
          <li>
            The portal backend&apos;s <code className="font-mono text-xs">ENTRA_TENANT_ID</code> and{' '}
            <code className="font-mono text-xs">ENTRA_CLIENT_ID</code> application settings. They must
            match the portal app registration the site signs in against. A wrong or empty value makes
            every token fail validation.
          </li>
          <li>
            That the portal app registration exposes the{' '}
            <code className="font-mono text-xs">user_impersonation</code> scope under Expose an API,
            with an Application ID URI of{' '}
            <code className="font-mono text-xs">api://&lt;portal client ID&gt;</code>.
          </li>
          <li>
            That the redirect URI registered for the single-page application matches this site&apos;s
            address exactly.
          </li>
        </ul>

        <div className="mt-6 flex items-center gap-3">
          <Button size="sm" onClick={retry}>
            <RefreshCw aria-hidden="true" /> Try again
          </Button>
          <Button size="sm" variant="outline" onClick={signOut}>
            <LogOut aria-hidden="true" /> Sign out
          </Button>
        </div>
      </div>
    </div>
  );
}
