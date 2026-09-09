import { useEffect, useState } from 'react';
import { ArrowUpCircle, X, ExternalLink } from 'lucide-react';
import { useAuth } from '../context/authContext.tsx';
import { fetchUpdateStatus, displayVersion, hasDismissedUpdate, dismissUpdate } from '../lib/updateCheck.ts';

/**
 * Notifies Administrators that a newer Cloud Imaging release is available.
 *
 * Renders nothing unless the check is enabled, succeeded, and found a genuinely newer release,
 * so a disabled check or a deployment with no outbound internet access shows no trace of this.
 * Dismissal is remembered per account and per version, so dismissing one release does not
 * suppress the next.
 */
export function UpdateAvailableBanner(): React.ReactElement | null {
  const { account, isAdministrator } = useAuth();
  const [version, setVersion] = useState<string | null>(null);
  const [releaseUrl, setReleaseUrl] = useState<string | null>(null);
  const [dismissed, setDismissed] = useState(false);

  const accountKey = account?.homeAccountId || account?.username || '';

  useEffect(() => {
    if (!isAdministrator || !accountKey) return;
    let cancelled = false;

    void (async () => {
      const status = await fetchUpdateStatus();
      if (cancelled || !status?.updateAvailable || !status.latest) return;
      if (hasDismissedUpdate(accountKey, status.latest)) return;
      setVersion(status.latest);
      setReleaseUrl(status.releaseUrl);
    })();

    return () => { cancelled = true; };
  }, [isAdministrator, accountKey]);

  if (!version || dismissed) return null;

  const onDismiss = () => {
    dismissUpdate(accountKey, version);
    setDismissed(true);
  };

  return (
    <div className="flex items-start gap-3 rounded-lg border border-primary/30 bg-primary/5 px-4 py-3">
      <ArrowUpCircle className="mt-0.5 h-5 w-5 shrink-0 text-primary" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">
          Cloud Imaging {displayVersion(version)} is available
        </p>
        <p className="mt-0.5 text-xs text-muted-foreground">
          Upgrading restarts the API components briefly and may require rebuilding boot media.
          Review the upgrade guide before starting.
        </p>
        {releaseUrl && (
          <a
            href={releaseUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="mt-1.5 inline-flex items-center gap-1.5 text-xs text-primary hover:underline"
          >
            View the release notes
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </a>
        )}
      </div>
      <button
        onClick={onDismiss}
        aria-label="Dismiss this update notification"
        className="shrink-0 rounded p-1 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
      >
        <X className="h-4 w-4" aria-hidden="true" />
      </button>
    </div>
  );
}
