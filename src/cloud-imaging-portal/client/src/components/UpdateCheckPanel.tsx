import { useEffect, useState } from 'react';
import { RefreshCw, ExternalLink } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from './ui/card.tsx';
import { fetchUpdateStatus, displayVersion, type UpdateStatus, type UpdateCheckStatus } from '../lib/updateCheck.ts';

/** Plain-language explanation for each non-`ok` status, so the panel never shows a bare code. */
const STATUS_MESSAGE: Record<UpdateCheckStatus, string> = {
  ok: '',
  disabled: 'Version checking is turned off. Enable it below to see whether a newer release is available.',
  unreachable: 'Could not reach github.com. This is expected if outbound internet access is restricted.',
  'rate-limited': 'GitHub temporarily declined the request. It will be retried automatically later.',
  unknown: 'No published release was found to compare against.',
};

interface Props {
  enabled: boolean;
  onChange: (enabled: boolean) => void;
  disabled?: boolean;
}

/**
 * Version panel and opt-in toggle for the Configuration page.
 *
 * The toggle reflects unsaved state from the parent form, but the status shown is whatever the
 * backend last resolved, so it only changes after the setting is saved and re-checked.
 */
export function UpdateCheckPanel({ enabled, onChange, disabled = false }: Props): React.ReactElement {
  const [status, setStatus] = useState<UpdateStatus | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = async () => {
    setLoading(true);
    setStatus(await fetchUpdateStatus());
    setLoading(false);
  };

  useEffect(() => { void refresh(); }, []);

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-2">
          <RefreshCw className="h-5 w-5 text-primary" aria-hidden="true" />
          <CardTitle>Version</CardTitle>
        </div>
        <CardDescription>
          Shows the Cloud Imaging release this deployment is running, and optionally checks
          whether a newer one has been published.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid grid-cols-2 gap-4 rounded-md border border-border bg-muted/30 p-4">
          <div>
            <p className="text-xs text-muted-foreground">Installed version</p>
            <p className="mt-0.5 font-mono text-sm">
              {loading ? '…' : displayVersion(status?.current ?? null)}
            </p>
          </div>
          <div>
            <p className="text-xs text-muted-foreground">Latest available</p>
            <p className="mt-0.5 font-mono text-sm">
              {loading ? '…' : status?.latest ? displayVersion(status.latest) : 'Not available'}
            </p>
          </div>
        </div>

        {!loading && status && status.status !== 'ok' && (
          <p className="text-xs text-muted-foreground">{STATUS_MESSAGE[status.status]}</p>
        )}

        {!loading && status?.status === 'ok' && !status.updateAvailable && (
          <p className="text-xs text-muted-foreground">This deployment is up to date.</p>
        )}

        {!loading && status?.updateAvailable && status.releaseUrl && (
          <a
            href={status.releaseUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1.5 text-sm text-primary hover:underline"
          >
            View the release notes
            <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
          </a>
        )}

        {!loading && status?.checkedAt && (
          <p className="text-xs text-muted-foreground">
            Last checked {new Date(status.checkedAt).toLocaleString()}.
          </p>
        )}

        <div className="flex items-center justify-between gap-4 rounded-md border border-border bg-muted/30 p-4">
          <div>
            <p className="text-sm font-medium">Check GitHub for new releases</p>
            <p className="mt-0.5 text-xs text-muted-foreground">
              {enabled
                ? 'Enabled. The portal contacts github.com periodically to read the latest published release number.'
                : 'Disabled. The portal makes no outbound request and no version comparison is shown.'}
            </p>
          </div>
          <button
            onClick={() => !disabled && onChange(!enabled)}
            disabled={disabled}
            role="switch"
            aria-checked={enabled}
            aria-label="Check GitHub for new releases"
            className={[
              'relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors focus:outline-none focus:ring-2 focus:ring-primary focus:ring-offset-2',
              enabled ? 'bg-primary' : 'bg-muted',
              disabled ? 'cursor-not-allowed opacity-50' : 'cursor-pointer',
            ].join(' ')}
          >
            <span className={[
              'inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform',
              enabled ? 'translate-x-6' : 'translate-x-1',
            ].join(' ')} />
          </button>
        </div>
      </CardContent>
    </Card>
  );
}
