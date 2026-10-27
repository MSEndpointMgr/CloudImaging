import { useState } from 'react';
import { ShieldCheck, KeyRound, RefreshCw, History, Ban } from 'lucide-react';
import { apiFetch } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { Button } from './ui/button.tsx';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card.tsx';
import { Badge } from './ui/badge.tsx';
import { CopyableId } from './ui/copyable-id.tsx';
import { Select, type SelectOption } from './ui/select.tsx';
import { useToast } from '../context/toastContext.tsx';
import { ConfirmImpactDialog, type ConfirmImpactCopy } from './ConfirmImpactDialog.tsx';

/** The certificate the last rotation replaced, while it is still accepted. */
export interface PreviousCertMetadata {
  thumbprintDisplay?: string;
  expiresAt?: string;
  acceptedUntil?: string;
}

export interface CertMetadata {
  thumbprintDisplay?: string;
  issuedAt?: string;
  expiresAt?: string;
  isActive?: boolean;
  previous?: PreviousCertMetadata | null;
}

interface BootMediaCertPanelProps {
  certMeta: CertMetadata | null;
  onCertChanged: () => void;
}

type CertAction = 'generate' | 'rotate' | 'stopPrevious';

/** Default overlap: long enough for USB media in a drawer to boot once and update itself. */
const DEFAULT_PREVIOUS_ACCEPTED_DAYS = '14';

const OVERLAP_OPTIONS: SelectOption[] = [
  { value: '0', label: 'Stop immediately', description: 'Use when the current certificate is compromised.' },
  { value: '7', label: '7 days' },
  { value: '14', label: '14 days', description: 'Recommended' },
  { value: '30', label: '30 days' },
];

/**
 * Boot media certificate management panel for the Configuration page.
 * Generate creates the first certificate. Rotate replaces it and can keep the replaced one accepted
 * for a while, so existing USB media can update itself to a boot image built with the new one.
 */
export function BootMediaCertPanel({ certMeta, onCertChanged }: BootMediaCertPanelProps): React.ReactElement {
  const { notify, update } = useToast();
  const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState<CertAction | null>(null);
  const [overlapDays, setOverlapDays] = useState(DEFAULT_PREVIOUS_ACCEPTED_DAYS);

  const hasCert = Boolean(certMeta);
  const previousAcceptedUntil = certMeta?.previous?.acceptedUntil;
  const previous = previousAcceptedUntil && new Date(previousAcceptedUntil) > new Date()
    ? { ...certMeta?.previous, acceptedUntil: previousAcceptedUntil }
    : null;

  const copyFor = (action: CertAction): ConfirmImpactCopy => {
    switch (action) {
      case 'generate':
        return {
          confirmTitle: 'Generate certificate?',
          impact: 'A new certificate is created and activated. Boot media you build from now on uses it.',
          confirmLabel: 'Generate',
          destructive: false,
        };
      case 'rotate':
        return {
          confirmTitle: 'Rotate certificate?',
          impact: overlapDays === '0'
            ? 'A new certificate is issued and the current one stops working immediately. Every USB drive built with it stops authenticating with the Device Gateway and must be rebuilt. This cannot be undone.'
            : `A new certificate is issued and becomes active. USB media built with the current certificate keeps working for ${overlapDays} days. Build and publish a new boot image now: existing media updates itself to it when it boots in that time. Media that does not boot in time must be rebuilt.`,
          confirmLabel: 'Rotate',
          destructive: true,
        };
      case 'stopPrevious':
        return {
          confirmTitle: 'Stop accepting the previous certificate?',
          impact: 'USB media built with the previous certificate stops authenticating with the Device Gateway now, including media that has not updated itself yet. Those drives must be rebuilt. This cannot be undone.',
          confirmLabel: 'Stop accepting',
          destructive: true,
        };
    }
  };

  const request = (action: CertAction): Promise<Response> => {
    const path = action === 'stopPrevious' ? '/api/cert/previous/stop' : `/api/cert/${action}`;
    const body = action === 'rotate' ? { confirmed: true, previousAcceptedDays: Number(overlapDays) } : {};
    return apiFetch(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify(body),
    });
  };

  const successCopy = (action: CertAction): { title: string; description: string } => {
    if (action === 'stopPrevious') {
      return { title: 'Previous certificate no longer accepted', description: 'Only boot media built with the active certificate can authenticate.' };
    }
    if (action === 'rotate' && overlapDays !== '0') {
      return { title: 'Certificate rotated', description: 'Build and publish a new boot image so existing USB media can update itself to it.' };
    }
    return {
      title: action === 'generate' ? 'Certificate activated' : 'Certificate rotated',
      description: 'Rebuild and redistribute boot media so devices use the new certificate.',
    };
  };

  const runAction = async (action: CertAction) => {
    setPending(null);
    setBusy(true);
    const toastId = notify({
      status: 'loading',
      title: action === 'stopPrevious' ? 'Updating certificates…' : action === 'generate' ? 'Creating certificate…' : 'Rotating certificate…',
    });
    try {
      const res = await request(action);
      if (res.ok) {
        update(toastId, { status: 'success', ...successCopy(action) });
        onCertChanged();
      } else {
        const msg = await res.text().catch(() => `HTTP ${res.status}`);
        update(toastId, { status: 'error', title: 'Certificate update failed', description: msg });
      }
    } catch {
      update(toastId, {
        status: 'error',
        title: 'Certificate update failed',
        description: 'A network error occurred. Please try again.',
      });
    } finally {
      setBusy(false);
    }
  };

  const fmtDate = (d?: string) => (d ? new Date(d).toLocaleDateString() : '-');

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-2">
          <ShieldCheck className="h-5 w-5 text-primary" aria-hidden="true" />
          <CardTitle>Boot media certificate</CardTitle>
        </div>
        <CardDescription>
          Authenticates the Cloud Imaging Client to the Device Gateway when a device boots
          from prepared media. Devices trust the certificate embedded in their boot media, so
          replacing it requires rebuilding that media.
        </CardDescription>
      </CardHeader>

      <CardContent className="space-y-4">
        {/* Current certificate */}
        {certMeta ? (
          <dl className="grid grid-cols-2 gap-x-6 gap-y-3 rounded-md border border-border bg-muted/30 p-4 text-sm sm:grid-cols-4">
            <div className="col-span-2 space-y-0.5 sm:col-span-4">
              <dt className="text-muted-foreground">Thumbprint</dt>
              <dd className="font-mono">
                {certMeta.thumbprintDisplay
                  ? <CopyableId value={certMeta.thumbprintDisplay} label="certificate thumbprint" wrap />
                  : '-'}
              </dd>
            </div>
            <div className="space-y-0.5">
              <dt className="text-muted-foreground">Issued</dt>
              <dd>{fmtDate(certMeta.issuedAt)}</dd>
            </div>
            <div className="space-y-0.5">
              <dt className="text-muted-foreground">Expires</dt>
              <dd>{fmtDate(certMeta.expiresAt)}</dd>
            </div>
            <div className="space-y-0.5">
              <dt className="text-muted-foreground">Status</dt>
              <dd>
                <Badge variant={certMeta.isActive ? 'success' : 'muted'}>
                  {certMeta.isActive ? 'Active' : 'Inactive'}
                </Badge>
              </dd>
            </div>
          </dl>
        ) : (
          <div className="rounded-md border border-dashed border-border bg-muted/20 p-4 text-sm text-muted-foreground">
            No certificate has been configured yet. Generate one to start signing boot media.
          </div>
        )}

        {/* Actions */}
        <div className="space-y-3">
          {previous && (
            <ActionRow
              icon={<History className="h-4 w-4" aria-hidden="true" />}
              title="Previous certificate still accepted"
              description={(
                <>
                  USB media built before the last rotation keeps working until{' '}
                  {formatDateTime(previous.acceptedUntil)}, so it can update itself to the newest boot image.
                  {previous.thumbprintDisplay && (
                    <span className="mt-1 block font-mono">
                      <CopyableId value={previous.thumbprintDisplay} label="previous certificate thumbprint" wrap />
                    </span>
                  )}
                </>
              )}
              action={(
                <Button variant="outline" onClick={() => setPending('stopPrevious')} disabled={busy}>
                  <Ban aria-hidden="true" />
                  Stop accepting…
                </Button>
              )}
            />
          )}

          {hasCert ? (
            <ActionRow
              icon={<RefreshCw className="h-4 w-4" aria-hidden="true" />}
              title="Rotate certificate"
              description="Issue a replacement certificate. You choose how long USB media built with the current one keeps working."
              action={(
                <Button variant="outline" onClick={() => { setOverlapDays(DEFAULT_PREVIOUS_ACCEPTED_DAYS); setPending('rotate'); }} disabled={busy}>
                  <RefreshCw aria-hidden="true" />
                  Rotate…
                </Button>
              )}
            />
          ) : (
            <ActionRow
              icon={<KeyRound className="h-4 w-4" aria-hidden="true" />}
              title="Generate certificate"
              description="Create the first certificate and activate it so you can build boot media."
              action={(
                <Button onClick={() => setPending('generate')} disabled={busy}>
                  <KeyRound aria-hidden="true" />
                  Generate
                </Button>
              )}
            />
          )}
        </div>
      </CardContent>

      {/* Impact confirmation overlay */}
      {pending && (
        <ConfirmImpactDialog
          copy={copyFor(pending)}
          busy={busy}
          onCancel={() => setPending(null)}
          onConfirm={() => void runAction(pending)}
          titleId="cert-confirm-title"
          details={pending === 'rotate' ? (
            <div className="space-y-1 pt-1">
              <label htmlFor="cert-previous-accepted" className="text-sm font-medium">
                Keep accepting the current certificate for
              </label>
              <Select
                id="cert-previous-accepted"
                value={overlapDays}
                onValueChange={setOverlapDays}
                options={OVERLAP_OPTIONS}
                disabled={busy}
              />
            </div>
          ) : undefined}
        />
      )}
    </Card>
  );
}

/** A single labelled action with a description and its trigger control. */
function ActionRow({
  icon,
  title,
  description,
  action,
}: {
  icon: React.ReactNode;
  title: string;
  description: React.ReactNode;
  action: React.ReactNode;
}): React.ReactElement {
  return (
    <div className="flex flex-col gap-3 rounded-md border border-border p-3 sm:flex-row sm:items-center sm:justify-between">
      <div className="flex items-center gap-3">
        <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-md bg-muted text-muted-foreground">
          {icon}
        </div>
        <div className="min-w-0 space-y-0.5">
          <p className="text-sm font-medium">{title}</p>
          <div className="text-sm text-muted-foreground">{description}</div>
        </div>
      </div>
      <div className="shrink-0 sm:pl-3">{action}</div>
    </div>
  );
}
