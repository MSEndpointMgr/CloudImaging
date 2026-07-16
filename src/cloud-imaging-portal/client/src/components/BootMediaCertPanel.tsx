import { useState } from 'react';
import { UploadProgressBar } from './UploadProgressBar.tsx';

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

/**
 * Boot media certificate management panel for the Configuration page (T179, FR-068).
 * Provides Generate and Rotate actions; rotation requires confirmation.
 */
export function BootMediaCertPanel({ certMeta, onCertChanged }: BootMediaCertPanelProps): React.ReactElement {
  const [busy, setBusy]       = useState(false);
  const [progress, setProgress] = useState(0);
  const [error, setError]     = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  const doAction = async (action: 'generate' | 'rotate') => {
    const body = action === 'rotate' ? { confirmed: true } : {};

    if (action === 'rotate' && !confirm(
      'Rotating the certificate will invalidate all USB drives prepared with the current boot image. ' +
      'You must regenerate and redistribute boot media after rotation. Continue?'
    )) return;

    setBusy(true); setError(null); setSuccess(null); setProgress(10);
    try {
      const res = await fetch(`/api/cert/${action}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(body),
      });
      setProgress(90);
      if (res.ok || res.status === 201) {
        setSuccess(action === 'generate'
          ? 'New certificate generated and activated.'
          : 'Certificate rotated. Regenerate boot media to distribute the new certificate.');
        setProgress(100);
        onCertChanged();
      } else {
        const msg = await res.text().catch(() => `HTTP ${res.status}`);
        setError(msg);
      }
    } catch { setError('Network error.'); }
    finally { setBusy(false); setTimeout(() => setProgress(0), 1500); }
  };

  const fmtDate = (d?: string) => d ? new Date(d).toLocaleDateString() : '—';

  return (
    <div className="rounded-md border border-border p-4 space-y-4">
      <div>
        <h3 className="text-sm font-semibold">Boot Media Certificate</h3>
        <p className="text-xs text-muted-foreground mt-0.5">
          Used for mTLS authentication between Cloud Imaging Client and Device Gateway API (FR-069).
        </p>
      </div>

      {certMeta ? (
        <div className="text-xs space-y-1">
          <p><span className="text-muted-foreground">Thumbprint:</span> {certMeta.thumbprintDisplay ?? '—'}</p>
          <p><span className="text-muted-foreground">Issued:</span> {fmtDate(certMeta.issuedAt)}</p>
          <p><span className="text-muted-foreground">Expires:</span> {fmtDate(certMeta.expiresAt)}</p>
          <p>
            <span className="text-muted-foreground">Status:</span>{' '}
            <span className={certMeta.isActive ? 'text-green-600 font-medium' : 'text-muted-foreground'}>
              {certMeta.isActive ? 'Active' : 'Inactive'}
            </span>
          </p>
        </div>
      ) : (
        <p className="text-xs text-muted-foreground">No active certificate configured.</p>
      )}

      {progress > 0 && <UploadProgressBar percent={progress} label="Processing…" error={error} />}
      {success && <p className="text-xs text-green-600">{success}</p>}
      {error && progress === 0 && <p className="text-xs text-destructive">{error}</p>}

      <div className="flex gap-2">
        <button
          onClick={() => void doAction('generate')}
          disabled={busy}
          className="px-3 py-1.5 text-xs bg-primary text-primary-foreground rounded hover:bg-primary/90 disabled:opacity-50"
        >
          {certMeta ? 'Regenerate' : 'Generate'} Certificate
        </button>
        {certMeta?.isActive && (
          <button
            onClick={() => void doAction('rotate')}
            disabled={busy}
            className="px-3 py-1.5 text-xs border border-destructive text-destructive rounded hover:bg-destructive/10 disabled:opacity-50"
          >
            Rotate Certificate…
          </button>
        )}
      </div>
    </div>
  );
}
