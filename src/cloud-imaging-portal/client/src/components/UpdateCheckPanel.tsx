import { useEffect, useState } from 'react';
import { RefreshCw, ExternalLink } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from './ui/card.tsx';
import { Switch } from './ui/switch.tsx';
import { Button, type ButtonStatus } from './ui/button.tsx';
import { fetchUpdateStatus, triggerUpdateCheck, displayVersion, type UpdateStatus, type UpdateCheckStatus } from '../lib/updateCheck.ts';

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
  const [checkStatus, setCheckStatus] = useState<ButtonStatus>('idle');

  const refresh = async () => {
    setLoading(true);
    setStatus(await fetchUpdateStatus());
    setLoading(false);
  };

  useEffect(() => { void refresh(); }, []);

  const checkNow = async () => {
    setCheckStatus('loading');
    const result = await triggerUpdateCheck();
    if (result) {
      setStatus(result);
      setCheckStatus('success');
    } else {
      setCheckStatus('error');
    }
    setTimeout(() => setCheckStatus('idle'), 2000);
  };

  // Outcome and recency belong together: on their own rows they read as two unrelated facts.
  const statusLine = loading || !status
    ? ''
    : [
        status.status !== 'ok'
          ? STATUS_MESSAGE[status.status]
          : status.updateAvailable
            ? ''
            : 'This deployment is up to date.',
        status.checkedAt ? `Last checked ${new Date(status.checkedAt).toLocaleString()}.` : '',
      ]
        .filter(Boolean)
        .join(' ');

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center justify-between gap-4">
          <div className="flex items-center gap-2">
            <RefreshCw className="h-5 w-5 text-primary" aria-hidden="true" />
            <CardTitle>Version</CardTitle>
          </div>
          <Button
            type="button"
            variant="outline"
            status={checkStatus}
            onClick={() => void checkNow()}
            disabled={disabled || loading || status?.status === 'disabled'}
            title={status?.status === 'disabled' ? 'Enable version checking below to check now' : 'Check GitHub now'}
          >
            <RefreshCw aria-hidden="true" />
            Check now
          </Button>
        </div>
        <CardDescription>
          Shows the Cloud Imaging release this deployment is running, and optionally checks
          whether a newer one has been published.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="flex items-center justify-between gap-4 rounded-md border border-border bg-muted/30 p-4">
          <div>
            <p className="text-sm font-medium">Check GitHub for new releases</p>
            <p className="mt-0.5 text-sm text-muted-foreground">
              When enabled, the portal periodically contacts github.com to read the latest published release number.
            </p>
          </div>
          <Switch
            checked={enabled}
            onCheckedChange={onChange}
            disabled={disabled}
            label="Check GitHub for new releases"
          />
        </div>

        <div className="grid grid-cols-1 gap-4 rounded-md border border-border bg-muted/30 p-4 sm:grid-cols-3">
          <div>
            <p className="text-xs text-muted-foreground">Environment type</p>
            <p className="mt-0.5 font-mono text-sm">
              {loading ? '…' : status?.environmentLabel ?? 'Unknown'}
            </p>
          </div>
          <div>
            <p className="text-xs text-muted-foreground">Current version</p>
            <p className="mt-0.5 font-mono text-sm">
              {loading ? '…' : status?.current ? displayVersion(status.current) : 'Unknown'}
            </p>
          </div>
          <div>
            <p className="text-xs text-muted-foreground">Latest available</p>
            <p className="mt-0.5 font-mono text-sm">
              {loading ? '…' : status?.latest ? displayVersion(status.latest) : 'Not available'}
            </p>
          </div>
        </div>

        {statusLine && <p className="text-xs text-muted-foreground">{statusLine}</p>}

        {!loading && status?.updateAvailable && status.releaseUrl && (
          <a
            href={status.releaseUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-2 text-sm text-primary hover:underline"
          >
            View the release notes
            <ExternalLink className="h-4 w-4" aria-hidden="true" />
          </a>
        )}
      </CardContent>
    </Card>
  );
}
