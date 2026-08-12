import { useState } from 'react';
import { ShieldCheck, KeyRound, RefreshCw, AlertTriangle } from 'lucide-react';
import { apiFetch } from '../lib/apiClient.ts';
import { Button } from './ui/button.tsx';
import { Badge } from './ui/badge.tsx';
import { useToast } from '../context/toastContext.tsx';

interface CertMetadata {
  thumbprintDisplay?: string;
  issuedAt?: string;
  expiresAt?: string;
  isActive?: boolean;
}

interface BootMediaCertPanelProps {
  certMeta: CertMetadata | null;
  onCertChanged: () => void;
}

type CertAction = 'generate' | 'rotate';

/** Copy shown for each action's button and its impact confirmation prompt. */
interface ActionCopy {
  confirmTitle: string;
  impact: string;
  confirmLabel: string;
  destructive: boolean;
}

/**
 * Boot media certificate management panel for the Configuration page.
 * Provides Generate/Regenerate and Rotate actions, each guarded by an impact
 * confirmation prompt because they invalidate previously distributed boot media.
 */
export function BootMediaCertPanel({ certMeta, onCertChanged }: BootMediaCertPanelProps): React.ReactElement {
  const { notify, update } = useToast();
  const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState<CertAction | null>(null);

  const hasCert = Boolean(certMeta);

  const copyFor = (action: CertAction): ActionCopy =>
    action === 'generate'
      ? {
          confirmTitle: hasCert ? 'Regenerate certificate?' : 'Generate certificate?',
          impact: hasCert
            ? 'A brand-new certificate will be created and immediately activated, replacing the current one. Every USB drive already prepared with the existing boot media will stop authenticating with the Device Gateway and must be rebuilt and redistributed. This action cannot be undone.'
            : 'A new certificate will be created and activated for signing boot media. You can then build and distribute boot media using it.',
          confirmLabel: hasCert ? 'Regenerate' : 'Generate',
          destructive: hasCert,
        }
      : {
          confirmTitle: 'Rotate certificate?',
          impact:
            'A new certificate will be issued and the current one retired. All USB drives prepared with the current boot media will stop authenticating with the Device Gateway. You must regenerate and redistribute boot media after rotating. This action cannot be undone.',
          confirmLabel: 'Rotate',
          destructive: true,
        };

  const runAction = async (action: CertAction) => {
    setPending(null);
    setBusy(true);
    const toastId = notify({
      status: 'loading',
      title: action === 'generate' ? 'Updating certificate…' : 'Rotating certificate…',
    });
    try {
      const res = await apiFetch(`/api/cert/${action}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(action === 'rotate' ? { confirmed: true } : {}),
      });
      if (res.ok || res.status === 201) {
        update(toastId, {
          status: 'success',
          title: action === 'generate' ? 'Certificate activated' : 'Certificate rotated',
          description: 'Rebuild and redistribute boot media so devices use the new certificate.',
        });
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

  const fmtDate = (d?: string) => (d ? new Date(d).toLocaleDateString() : '—');

  return (
    <div className="rounded-lg border border-border bg-card p-5 space-y-5">
      {/* Heading */}
      <div className="flex items-start gap-3">
        <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
          <ShieldCheck className="h-5 w-5" aria-hidden="true" />
        </div>
        <div className="space-y-0.5">
          <h3 className="text-sm font-semibold leading-none">Boot media certificate</h3>
          <p className="text-xs text-muted-foreground">
            Authenticates the Cloud Imaging Client to the Device Gateway when a device boots
            from prepared media. Devices trust the certificate embedded in their boot media, so
            replacing it requires rebuilding that media.
          </p>
        </div>
      </div>

      {/* Current certificate */}
      {certMeta ? (
        <dl className="grid grid-cols-2 gap-x-6 gap-y-3 rounded-md border border-border bg-muted/30 p-4 text-xs sm:grid-cols-4">
          <div className="space-y-0.5">
            <dt className="text-muted-foreground">Thumbprint</dt>
            <dd className="font-mono">{certMeta.thumbprintDisplay ?? '—'}</dd>
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
        <div className="rounded-md border border-dashed border-border bg-muted/20 p-4 text-xs text-muted-foreground">
          No certificate has been configured yet. Generate one to start signing boot media.
        </div>
      )}

      {/* Actions */}
      <div className="space-y-3">
        <ActionRow
          icon={<KeyRound className="h-4 w-4" aria-hidden="true" />}
          title={hasCert ? 'Regenerate certificate' : 'Generate certificate'}
          description={
            hasCert
              ? 'Create a new certificate and make it active immediately. Boot media built with the previous certificate stops working.'
              : 'Create the first certificate and activate it so you can build boot media.'
          }
          action={
            <Button onClick={() => setPending('generate')} disabled={busy}>
              {hasCert ? 'Regenerate' : 'Generate'}
            </Button>
          }
        />

        {certMeta?.isActive && (
          <ActionRow
            icon={<RefreshCw className="h-4 w-4" aria-hidden="true" />}
            title="Rotate certificate"
            description="Issue a replacement certificate and retire the current one. Existing boot media must be rebuilt and redistributed."
            action={
              <Button
                variant="outline"
                onClick={() => setPending('rotate')}
                disabled={busy}
                className="border-destructive text-destructive hover:bg-destructive/10 hover:text-destructive"
              >
                Rotate…
              </Button>
            }
          />
        )}
      </div>

      {/* Impact confirmation overlay */}
      {pending && (
        <ConfirmImpactDialog
          copy={copyFor(pending)}
          busy={busy}
          onCancel={() => setPending(null)}
          onConfirm={() => void runAction(pending)}
        />
      )}
    </div>
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
  description: string;
  action: React.ReactNode;
}): React.ReactElement {
  return (
    <div className="flex flex-col gap-3 rounded-md border border-border p-3 sm:flex-row sm:items-center sm:justify-between">
      <div className="flex items-start gap-3">
        <div className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-md bg-muted text-muted-foreground">
          {icon}
        </div>
        <div className="space-y-0.5">
          <p className="text-xs font-medium">{title}</p>
          <p className="text-xs text-muted-foreground">{description}</p>
        </div>
      </div>
      <div className="shrink-0 sm:pl-3">{action}</div>
    </div>
  );
}

/** Modal overlay that warns the operator about the impact before a certificate change. */
function ConfirmImpactDialog({
  copy,
  busy,
  onCancel,
  onConfirm,
}: {
  copy: ActionCopy;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}): React.ReactElement {
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="cert-confirm-title"
    >
      <div className="w-full max-w-md rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start gap-3 p-5">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-destructive/10 text-destructive">
            <AlertTriangle className="h-5 w-5" aria-hidden="true" />
          </div>
          <div className="space-y-1.5">
            <h2 id="cert-confirm-title" className="text-sm font-semibold">
              {copy.confirmTitle}
            </h2>
            <p className="text-xs leading-relaxed text-muted-foreground">{copy.impact}</p>
          </div>
        </div>
        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="outline" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
          <Button
            onClick={onConfirm}
            disabled={busy}
            loading={busy}
            className={
              copy.destructive
                ? 'bg-destructive text-destructive-foreground hover:bg-destructive/90'
                : undefined
            }
          >
            {copy.confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}
