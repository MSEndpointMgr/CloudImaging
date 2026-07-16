import { useState, useEffect } from 'react';
import { PreFlightAuthorizationToggle } from '../components/PreFlightAuthorizationToggle.tsx';
import { BootMediaCertPanel } from '../components/BootMediaCertPanel.tsx';

interface PortalConfig {
  devicePreFlightAuthorizationEnabled: boolean;
  sasTokenUrlExpiryMinutes: number;
  bootImageSasExpiryMinutes: number;
  certValidityPeriodDays: number;
  clockSkewToleranceSeconds: number;
}

interface CertMeta {
  thumbprintDisplay?: string;
  issuedAt?: string;
  expiresAt?: string;
  isActive?: boolean;
}

/**
 * Deployment configuration page (T147, FR-026, FR-068).
 * Combines pre-flight authorization toggle, SAS/cert settings, and cert management panel.
 */
export default function DeploymentConfigPage(): React.ReactElement {
  const [config, setConfig] = useState<PortalConfig>({
    devicePreFlightAuthorizationEnabled: false,
    sasTokenUrlExpiryMinutes:  60,
    bootImageSasExpiryMinutes: 60,
    certValidityPeriodDays:    365,
    clockSkewToleranceSeconds: 30,
  });
  const [certMeta, setCertMeta] = useState<CertMeta | null>(null);
  const [loading, setLoading]   = useState(true);
  const [saving, setSaving]     = useState(false);
  const [saved, setSaved]       = useState(false);
  const [error, setError]       = useState<string | null>(null);

  const load = async () => {
    setLoading(true);
    try {
      const [cfgRes, certRes] = await Promise.all([
        fetch('/api/portal-config', { credentials: 'include' }),
        fetch('/api/cert/active',   { credentials: 'include' }),
      ]);
      if (cfgRes.ok)  setConfig(await cfgRes.json() as PortalConfig);
      if (certRes.ok) setCertMeta(await certRes.json() as CertMeta);
    } catch { /* use defaults */ }
    finally { setLoading(false); }
  };

  useEffect(() => { void load(); }, []);

  const save = async () => {
    setSaving(true); setError(null); setSaved(false);
    try {
      const res = await fetch('/api/portal-config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(config),
      });
      if (res.ok || res.status === 204) { setSaved(true); setTimeout(() => setSaved(false), 3000); }
      else setError('Failed to save configuration.');
    } catch { setError('Network error.'); }
    finally { setSaving(false); }
  };

  if (loading) return <p className="text-muted-foreground">Loading…</p>;

  const num = (key: keyof PortalConfig, min: number, max: number) => (
    <input type="number" min={min} max={max}
      value={config[key] as number}
      onChange={e => setConfig(c => ({ ...c, [key]: Number(e.target.value) }))}
      className="w-32 border border-input rounded-md px-3 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-primary" />
  );

  return (
    <div className="max-w-2xl space-y-6">
      <h1 className="text-xl font-semibold">Deployment Configuration</h1>

      {/* Pre-flight authorization toggle */}
      <PreFlightAuthorizationToggle
        enabled={config.devicePreFlightAuthorizationEnabled}
        onChange={v => setConfig(c => ({ ...c, devicePreFlightAuthorizationEnabled: v }))}
      />

      {/* Numeric settings */}
      <div className="rounded-md border border-border p-4 space-y-4">
        <h3 className="text-sm font-semibold">Token &amp; SAS Settings</h3>
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">OS Image SAS Expiry (minutes)</label>
            {num('sasTokenUrlExpiryMinutes', 15, 1440)}
          </div>
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">Boot Image SAS Expiry (minutes)</label>
            {num('bootImageSasExpiryMinutes', 15, 1440)}
          </div>
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">Cert Validity Period (days)</label>
            {num('certValidityPeriodDays', 30, 3650)}
          </div>
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">Clock Skew Tolerance (seconds)</label>
            {num('clockSkewToleranceSeconds', 0, 300)}
          </div>
        </div>
      </div>

      {error && <p className="text-sm text-destructive">{error}</p>}
      {saved && <p className="text-sm text-green-600">Configuration saved.</p>}

      <button onClick={save} disabled={saving}
        className="px-4 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90 disabled:opacity-50">
        {saving ? 'Saving…' : 'Save Configuration'}
      </button>

      {/* Cert management panel */}
      <BootMediaCertPanel certMeta={certMeta} onCertChanged={() => void load()} />
    </div>
  );
}
