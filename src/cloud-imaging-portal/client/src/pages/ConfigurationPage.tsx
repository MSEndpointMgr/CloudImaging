import { useState, useEffect } from 'react';

interface PortalConfig {
  devicePreFlightAuthorizationEnabled: boolean;
  sasTokenUrlExpiryMinutes: number;
  bootImageSasExpiryMinutes: number;
  certValidityPeriodDays: number;
  clockSkewToleranceSeconds: number;
}

/** Deployment configuration page (US6, T097). Administrator-only writes. */
export default function ConfigurationPage(): React.ReactElement {
  const [config, setConfig]   = useState<PortalConfig>({
    devicePreFlightAuthorizationEnabled: false,
    sasTokenUrlExpiryMinutes:  60,
    bootImageSasExpiryMinutes: 60,
    certValidityPeriodDays:    365,
    clockSkewToleranceSeconds: 30,
  });
  const [loading, setLoading] = useState(true);
  const [saving, setSaving]   = useState(false);
  const [saved, setSaved]     = useState(false);
  const [error, setError]     = useState<string | null>(null);

  useEffect(() => {
    void (async () => {
      try {
        const res = await fetch('/api/configuration', { credentials: 'include' });
        if (res.ok) setConfig(await res.json() as PortalConfig);
      } catch { /* use defaults */ }
      finally { setLoading(false); }
    })();
  }, []);

  const handleSave = async () => {
    setSaving(true); setError(null); setSaved(false);
    try {
      const res = await fetch('/api/configuration', {
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
    <input
      type="number" min={min} max={max}
      value={config[key] as number}
      onChange={e => setConfig({ ...config, [key]: Number(e.target.value) })}
      className="w-32 border border-input rounded-md px-3 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-primary"
    />
  );

  return (
    <div className="max-w-xl space-y-6">
      <h1 className="text-xl font-semibold">Deployment Configuration</h1>

      <div className="space-y-4">
        {/* Pre-flight authorization toggle (FR-026) */}
        <div className="flex items-center justify-between rounded-md border border-border p-4">
          <div>
            <p className="text-sm font-medium">Device Pre-Flight Authorization</p>
            <p className="text-xs text-muted-foreground mt-0.5">
              When enabled, devices must be in Autopilot or Corporate Identifiers before imaging.
            </p>
          </div>
          <button
            onClick={() => setConfig({ ...config, devicePreFlightAuthorizationEnabled: !config.devicePreFlightAuthorizationEnabled })}
            className={[
              'relative inline-flex h-6 w-11 items-center rounded-full transition-colors',
              config.devicePreFlightAuthorizationEnabled ? 'bg-primary' : 'bg-muted',
            ].join(' ')}
          >
            <span className={[
              'inline-block h-4 w-4 transform rounded-full bg-white transition-transform',
              config.devicePreFlightAuthorizationEnabled ? 'translate-x-6' : 'translate-x-1',
            ].join(' ')} />
          </button>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium mb-1">SAS URL Expiry (minutes)</label>
            {num('sasTokenUrlExpiryMinutes', 15, 1440)}
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Boot Image SAS Expiry (minutes)</label>
            {num('bootImageSasExpiryMinutes', 15, 1440)}
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Cert Validity Period (days)</label>
            {num('certValidityPeriodDays', 30, 3650)}
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Clock Skew Tolerance (seconds)</label>
            {num('clockSkewToleranceSeconds', 0, 300)}
          </div>
        </div>
      </div>

      {error && <p className="text-sm text-destructive">{error}</p>}
      {saved && <p className="text-sm text-green-600">Configuration saved.</p>}

      <button
        onClick={handleSave}
        disabled={saving}
        className="px-4 py-2 bg-primary text-primary-foreground rounded-md hover:bg-primary/90 disabled:opacity-50"
      >
        {saving ? 'Saving…' : 'Save Configuration'}
      </button>
    </div>
  );
}
